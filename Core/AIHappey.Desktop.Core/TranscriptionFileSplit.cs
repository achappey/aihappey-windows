using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed record TranscriptionChunk(TranscriptionSource File, int Index, int Total, double StartSecond, double EndSecond);
public sealed record TranscriptionRange(double StartSecond, double EndSecond);
public sealed record TranscriptionChunkResult(TranscriptionChunk Chunk, TranscriptionResult Result);
public sealed record TranscriptionSplitPlan(long TargetPayloadBytes, int BitrateKbps, IReadOnlyList<TranscriptionRange> Ranges);

/// <summary>Literal port of browser transcriptionFileSplit.ts range, budget and bitrate calculations.</summary>
public static class TranscriptionChunkPlanning
{
    public const long MB = 1024 * 1024;
    public static bool ShouldSplit(long size, TranscriptionPreferences options) => options.FileSplitEnabled && size > options.FileSplitMaxSizeMb * MB;
    public static TranscriptionSplitPlan Create(double duration, long size, int maxSizeMb, double overlapInput)
    {
        if (!double.IsFinite(duration) || duration <= 0 || size <= 0 || maxSizeMb < 1 || !double.IsFinite(overlapInput))
            throw new InvalidOperationException(DesktopResources.Get("TranscriptionCodecUnavailable"));
        var target = (long)Math.Max(MB, Math.Floor((maxSizeMb * MB - MB) / (4d / 3)));
        var chunkDuration = Math.Max(1, duration * ((double)target / size));
        var overlap = Math.Min(Math.Max(0, overlapInput), Math.Max(0, chunkDuration - 0.25));
        var stride = Math.Max(0.25, chunkDuration - overlap);
        var ranges = new List<TranscriptionRange>();
        for (double start = 0; start < duration; start += stride)
        {
            var end = Math.Min(duration, start + chunkDuration);
            ranges.Add(new(start, end)); if (end >= duration) break;
        }
        var sourceBitrate = Math.Max(16, Math.Ceiling(size * 8d / (1024 * duration)));
        var bitrate = (int)Math.Max(24, Math.Min(sourceBitrate,
            Math.Floor(target * 8d * 0.85 / (1024 * Math.Max(1, ranges[0].EndSecond - ranges[0].StartSecond)))));
        return new(target, bitrate, ranges);
    }
}

public interface ITranscriptionMediaEncoder
{
    Task<double> DurationAsync(string inputPath, CancellationToken ct);
    Task EncodeAsync(string inputPath, string outputPath, TranscriptionRange range, int bitrateKbps, CancellationToken ct);
}

/// <summary>Owns the immutable original and temporary chunks until transcription and persistence finish.</summary>
public sealed class TranscriptionSplitFiles(TranscriptionSource original, IReadOnlyList<TranscriptionChunk> chunks,
    bool split, double? duration = null, string? directory = null) : IDisposable
{
    public TranscriptionSource Original { get; } = original;
    public IReadOnlyList<TranscriptionChunk> Chunks { get; } = chunks;
    public bool Split { get; } = split;
    public double? DurationInSeconds { get; } = duration;
    public void Dispose() { if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true); }
}

public sealed class TranscriptionFileSplitter(ITranscriptionMediaEncoder encoder, string? temporaryRoot = null)
{
    public async Task<TranscriptionSplitFiles> SplitAsync(TranscriptionSource file, TranscriptionPreferences options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var shouldSplit = TranscriptionChunkPlanning.ShouldSplit(file.Length, options);
        var directory = Path.Combine(temporaryRoot ?? Path.Combine(Path.GetTempPath(), "AIHappey", "transcription"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "input" + Path.GetExtension(file.Name));
            await using (var output = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await file.CopyToAsync(output, ct);
            var original = TranscriptionSource.FromPath(file.Name, file.MediaType, input);
            if (!shouldSplit) return new(original, [new(original, 0, 1, 0, 0)], false, directory: directory);
            var duration = await encoder.DurationAsync(input, ct);
            var plan = TranscriptionChunkPlanning.Create(duration, file.Length, options.FileSplitMaxSizeMb, options.FileSplitOverlapSeconds);
            if (plan.Ranges.Count <= 1) return new(original, [new(original, 0, 1, 0, duration)], false, duration, directory);
            var chunks = new List<TranscriptionChunk>();
            for (var index = 0; index < plan.Ranges.Count; index++)
            {
                ct.ThrowIfCancellationRequested(); var range = plan.Ranges[index];
                var output = Path.Combine(directory, $"chunk-{index}.m4a");
                await encoder.EncodeAsync(input, output, range, plan.BitrateKbps, ct);
                var name = $"{Path.GetFileNameWithoutExtension(file.Name)}.part-{index + 1:D3}-of-{plan.Ranges.Count:D3}.m4a";
                var source = TranscriptionSource.FromPath(name, "audio/mp4", output);
                if (source.Length > plan.TargetPayloadBytes) throw new InvalidOperationException(DesktopResources.Get("TranscriptionChunkTooLarge"));
                chunks.Add(new(source, index, plan.Ranges.Count, range.StartSecond, range.EndSecond));
            }
            return new(original, chunks, true, duration, directory);
        }
        catch (OperationCanceledException) { Directory.Delete(directory, true); throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Directory.Delete(directory, true);
            if (!shouldSplit) throw;
            throw new InvalidOperationException(DesktopResources.Format("TranscriptionFileSplitFailedTooLarge", file.Name), error);
        }
    }
}

/// <summary>Browser file-level transaction: all chunk requests complete, merge, then save one original.</summary>
public sealed class TranscriptionFileProcessor(TranscriptionFileSplitter splitter)
{
    public async Task<TranscriptionResult> TranscribeAsync(TranscriptionSource source, TranscriptionPreferences preferences,
        Func<TranscriptionSource, TranscriptionPreferences, CancellationToken, Task<TranscriptionResult>> generate,
        Func<TranscriptionResult, TranscriptionSource, Task> persist, CancellationToken ct)
    {
        var snapshot = preferences.Clone();
        using var files = await splitter.SplitAsync(source, snapshot, ct);
        var results = await Task.WhenAll(files.Chunks.Select(async chunk =>
            new TranscriptionChunkResult(chunk, await generate(chunk.File, snapshot, ct))));
        var result = files.Split ? TranscriptionChunkMerging.Merge(files.Original, results, files.DurationInSeconds) : results[0].Result;
        // Like the existing desktop flow, a completed response is committed even if Stop arrives now.
        await persist(result, files.Original);
        return result;
    }
}

/// <summary>Literal browser merge semantics: keep overlap text; never deduplicate or invent timestamps.</summary>
public static class TranscriptionChunkMerging
{
    public static TranscriptionResult Merge(TranscriptionSource original, IEnumerable<TranscriptionChunkResult> results, double? duration)
    {
        var ordered = results.OrderBy(r => r.Chunk.Index).ToArray();
        if (ordered.Length == 0) throw new InvalidOperationException(DesktopResources.Get("TranscriptionInvalidResponse"));
        var first = ordered[0].Result.Response; var lastResult = ordered[^1].Result; var last = lastResult.Response;
        var merged = (JsonObject)last.DeepClone();
        merged["text"] = string.Join("\n", ordered.Select(r => OpenAIChatConfig.Text(r.Result.Response["text"])?.Trim()).Where(t => !string.IsNullOrEmpty(t)));
        var segments = new JsonArray(); var warnings = new JsonArray();
        foreach (var item in ordered)
        {
            if (item.Result.Response["segments"] is JsonArray sourceSegments)
                foreach (var segment in sourceSegments.OfType<JsonObject>())
                {
                    var copy = (JsonObject)segment.DeepClone();
                    copy["startSecond"] = segment["startSecond"]!.GetValue<double>() + item.Chunk.StartSecond;
                    copy["endSecond"] = segment["endSecond"]!.GetValue<double>() + item.Chunk.StartSecond;
                    segments.Add(copy);
                }
            if (item.Result.Response["warnings"] is JsonArray sourceWarnings)
                foreach (var warning in sourceWarnings) warnings.Add(warning?.DeepClone());
        }
        merged["segments"] = segments; merged["warnings"] = warnings;
        if (first.ContainsKey("language")) merged["language"] = first["language"]?.DeepClone(); else merged.Remove("language");
        if (duration is { } seconds) merged["durationInSeconds"] = seconds;
        var request = last["request"]?.DeepClone() as JsonObject ?? new();
        request["body"] = JsonSerializer.Serialize(new { split = true, fileName = original.Name, fileSize = original.Length,
            chunks = ordered.Select(r => new { index = r.Chunk.Index, total = r.Chunk.Total, name = r.Chunk.File.Name,
                size = r.Chunk.File.Length, mediaType = r.Chunk.File.MediaType, startSecond = r.Chunk.StartSecond, endSecond = r.Chunk.EndSecond }) }, JsonSerializerOptions.Web);
        merged["request"] = request;
        merged["response"] = new JsonObject { ["timestamp"] = DateTimeOffset.UtcNow, ["modelId"] = last["response"]?["modelId"]?.DeepClone() ?? JsonValue.Create(""),
            ["body"] = new JsonObject { ["split"] = true, ["originalResponseBodies"] = new JsonArray(ordered.Select(r => r.Result.Response["response"]?["body"]?.DeepClone()).ToArray()) } };
        return new(original.Name, original.MediaType, lastResult.Model, (JsonObject)lastResult.Options.DeepClone(), merged);
    }
}
