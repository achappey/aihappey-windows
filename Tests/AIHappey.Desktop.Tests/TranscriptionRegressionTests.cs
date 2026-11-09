using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;

internal static class TranscriptionRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        Directory.CreateDirectory(root);
        using var fixture = typeof(TranscriptionRegressionTests).Assembly.GetManifestResourceStream("Desktop.TranscriptionFixture")!;
        var oracle = (await JsonNode.ParseAsync(fixture))!.AsObject();
        foreach (var row in oracle["plans"]!.AsArray().OfType<JsonObject>())
        {
            var input = row["input"]!; var expected = row["expected"]!;
            var options = new TranscriptionPreferences { FileSplitEnabled = input["enabled"]!.GetValue<bool>(),
                FileSplitMaxSizeMb = input["maxSize"]!.GetValue<int>(), FileSplitOverlapSeconds = input["overlap"]!.GetValue<int>() };
            var size = input["size"]!.GetValue<long>(); var duration = input["duration"]!.GetValue<double>();
            var encoder = new FakeEncoder { Duration = duration };
            var source = Source(input["name"]!.GetValue<string>(), size);
            using (var files = await new TranscriptionFileSplitter(encoder, Path.Combine(root, "plans")).SplitAsync(source, options, default))
            {
                check(files.Split == expected["split"]!.GetValue<bool>() && files.Chunks.Count == expected["chunks"]!.AsArray().Count,
                    source.Name + ": split trigger and chunk count match actual browser");
                for (var i = 0; i < files.Chunks.Count; i++)
                {
                    var chunk = files.Chunks[i]; var reference = expected["chunks"]![i]!;
                    check(chunk.Index == reference["index"]!.GetValue<int>() && chunk.Total == reference["total"]!.GetValue<int>()
                        && Math.Abs(chunk.StartSecond - reference["startSecond"]!.GetValue<double>()) < 1e-9
                        && Math.Abs(chunk.EndSecond - reference["endSecond"]!.GetValue<double>()) < 1e-9 && chunk.File.Name == reference["name"]!.GetValue<string>(),
                        source.Name + ": exact browser range/name " + i);
                }
                check(encoder.Bitrates.SequenceEqual(expected["bitrates"]!.AsArray().Select(n => n!.GetValue<int>())), source.Name + ": exact browser bitrate targets");
            }
            check(!Directory.EnumerateDirectories(Path.Combine(root, "plans")).Any(), source.Name + ": temporary original and chunks removed");
        }

        var merge = oracle["merge"]!;
        var original = Source(merge["original"]!["name"]!.GetValue<string>(), merge["original"]!["size"]!.GetValue<long>());
        var results = merge["chunks"]!.AsArray().Select(node =>
        {
            var chunk = node!["chunk"]!; var file = chunk["file"]!;
            return new TranscriptionChunkResult(new(Source(file["name"]!.GetValue<string>(), file["size"]!.GetValue<long>(), file["type"]!.GetValue<string>()),
                chunk["index"]!.GetValue<int>(), chunk["total"]!.GetValue<int>(), chunk["startSecond"]!.GetValue<double>(), chunk["endSecond"]!.GetValue<double>()),
                new("chunk.m4a", "audio/mp4", "provider/model", new(), (JsonObject)node["result"]!.DeepClone()));
        }).ToArray();
        var before = JsonSerializer.Serialize(results.Select(r => r.Result.Response));
        var merged = TranscriptionChunkMerging.Merge(original, results, merge["duration"]!.GetValue<double>());
        var actual = (JsonObject)merged.Response.DeepClone(); actual["response"]!.AsObject().Remove("timestamp");
        actual["request"]!["body"] = JsonNode.Parse(actual["request"]!["body"]!.GetValue<string>());
        check(JsonNode.DeepEquals(actual, merge["expected"]), "merged text, overlap duplication, offsets, language, duration, warnings and metadata match actual browser");
        check(before == JsonSerializer.Serialize(results.Select(r => r.Result.Response)), "merging never mutates provider responses");
        var noLanguage = results.Select(r => new TranscriptionChunkResult(r.Chunk, r.Result with { Response = (JsonObject)r.Result.Response.DeepClone() })).ToArray();
        noLanguage.Single(r => r.Chunk.Index == 0).Result.Response.Remove("language");
        check(!TranscriptionChunkMerging.Merge(original, noLanguage, null).Response.ContainsKey("language"), "absent first language is not replaced by later language");

        var legacy = JsonSerializer.Deserialize<DesktopSettings>("{\"transcriptions\":{}}", JsonSerializerOptions.Web)!;
        check(legacy.Transcriptions.FileSplitEnabled && legacy.Transcriptions.FileSplitOverlapSeconds == 5 && legacy.Transcriptions.FileSplitMaxSizeMb == 15,
            "fresh and legacy missing settings default enabled / 5 seconds / 15 MB");
        var settings = legacy.Clone(); settings.Transcriptions.FileSplitEnabled = false; settings.Transcriptions.FileSplitOverlapSeconds = 12; settings.Transcriptions.FileSplitMaxSizeMb = 77;
        settings.Transcriptions.ProviderOptions["future"] = new() { ["keep"] = true };
        await SettingsStore.SaveAsync(Path.Combine(root, "settings"), settings);
        var loaded = await SettingsStore.LoadAsync(Path.Combine(root, "settings"), new());
        check(!loaded.Transcriptions.FileSplitEnabled && loaded.Transcriptions.FileSplitOverlapSeconds == 12 && loaded.Transcriptions.FileSplitMaxSizeMb == 77
            && loaded.Transcriptions.ProviderOptions["future"]["keep"]!.GetValue<bool>(), "saved opt-out and custom split/provider settings survive storage and cloning");
        check(ComposerAttachments.MaximumFileBytes == 25 * 1024 * 1024, "chat attachment limit is unchanged, not used by transcription");
        await UploadsAsync(check);
        await TransactionsAsync(check, root);
    }

    private static async Task UploadsAsync(Action<bool, string> check)
    {
        var options = new TranscriptionPreferences(); options.ProviderOptions["openai"]["temperature"] = 0.5;
        var bytes = Enumerable.Range(0, 81923).Select(i => (byte)i).ToArray();
        foreach (var length in new[] { 1, 2, 3, 81921, 81922, 81923 })
        {
            var file = new TranscriptionSource("quote\".wav", "audio/wav", length, _ => Task.FromResult<Stream>(new MemoryStream(bytes[..length])));
            using var content = new TranscriptionUploadContent("provider/quote\"", file, options.RequestOptions(), default);
            using var output = new MemoryStream(); await content.CopyToAsync(output);
            var body = JsonNode.Parse(output.ToArray())!;
            check(Convert.FromBase64String(body["audio"]!.GetValue<string>()).SequenceEqual(bytes[..length])
                && content.Headers.ContentLength == output.Length && body["model"]!.GetValue<string>() == "provider/quote\""
                && body["providerOptions"]!["openai"]!["temperature"] is null, "streamed JSON/base64 contract handles block remainder " + length);
        }
        using (var content = new TranscriptionUploadContent("p/model", Source("large.wav", 100 * TranscriptionChunkPlanning.MB), options.RequestOptions(), default))
        {
            var output = new CountingStream(); await content.CopyToAsync(output);
            check(output.Count == content.Headers.ContentLength && output.MaximumWrite <= 120000, "100 MB upload streams through bounded buffers without chat admission");
        }
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var session = new DesktopSession(new TestHost(), new TestRuntime(), new());
        var result = await new DesktopTranscriptionClient(new DesktopChatClient(session, http), http).GenerateAsync(
            new("p/model", "Transcription"), Source("small.wav", 3), options, default);
        check(handler.Path!.EndsWith("/api/transcriptions", StringComparison.Ordinal) && handler.Body!["model"]!.GetValue<string>() == "p/model"
            && result.Response["text"]!.GetValue<string>() == "ok", "actual transcription client retains gateway route and model contract");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try
        {
            using var content = new TranscriptionUploadContent("p/model", Source("cancel.wav", 100), new(), canceled.Token);
            await content.CopyToAsync(new CountingStream()); check(false, "upload cancellation");
        }
        catch (OperationCanceledException) { check(true, "streamed upload cancellation is propagated"); }
    }

    private static async Task TransactionsAsync(Action<bool, string> check, string root)
    {
        var work = Path.Combine(root, "transactions"); var store = new TranscriptionLibraryStore(Path.Combine(root, "library"), "account");
        var processor = new TranscriptionFileProcessor(new(new FakeEncoder(), work));
        var options = new TranscriptionPreferences(); var calls = 0;
        var source = Source("100mb.wav", 100 * TranscriptionChunkPlanning.MB);
        var result = await processor.TranscribeAsync(source, options, async (chunk, draft, ct) =>
        {
            var index = int.Parse(chunk.Name.Split(".part-")[1][..3]) - 1; Interlocked.Increment(ref calls);
            await Task.Delay((12 - index) * 2, ct);
            return new(chunk.Name, chunk.MediaType, "p/model", draft.RequestOptions(), new() { ["text"] = "chunk " + index,
                ["segments"] = new JsonArray(new JsonObject { ["text"] = "chunk " + index, ["startSecond"] = 1d, ["endSecond"] = 2d }) });
        }, (completed, original) => store.SaveAsync(completed, original), default);
        var saved = (await store.ListAsync()).Single();
        check(calls > 1 && saved.Item.Filename == source.Name && new FileInfo(saved.Path).Length == source.Length
            && saved.Item.Response["text"]!.GetValue<string>().StartsWith("chunk 0\nchunk 1\n", StringComparison.Ordinal), "100 MB source saves once with original media after out-of-order chunk completion");
        check(!Directory.EnumerateDirectories(work).Any() && (await new TranscriptionLibraryStore(Path.Combine(root, "library"), "other-account").ListAsync()).Count == 0,
            "successful transaction cleans temporary files and isolates accounts");
        check(TranscriptionFiles.ExportText(saved.Item.Response).Contains("chunk 0", StringComparison.Ordinal), "merged timestamps and text remain exportable after reload");
        foreach (var mode in new[] { "request-failure", "codec-failure", "oversized-chunk", "cancel", "persist-failure" })
        {
            var encoder = new FakeEncoder { Fail = mode == "codec-failure", Oversized = mode == "oversized-chunk" };
            var failed = new TranscriptionFileProcessor(new(encoder, work)); var committed = 0; var sent = 0;
            using var cancellation = new CancellationTokenSource();
            try
            {
                await failed.TranscribeAsync(source, options, async (chunk, draft, ct) =>
                {
                    Interlocked.Increment(ref sent);
                    if (mode == "request-failure") throw new IOException("provider failure");
                    if (mode == "cancel") { cancellation.Cancel(); await Task.Delay(1, ct); }
                    return new(chunk.Name, chunk.MediaType, "p/model", new(), new() { ["text"] = "ok" });
                }, (_, _) => { if (mode == "persist-failure") throw new IOException("disk failure"); committed++; return Task.CompletedTask; }, cancellation.Token);
                check(false, mode + " must fail");
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException)
            {
                check(committed == 0 && !Directory.EnumerateDirectories(work).Any(), mode + ": no partial item and all temporary media removed");
                if (mode is "codec-failure" or "oversized-chunk") check(sent == 0, mode + ": no fallback upload of oversized original");
            }
        }
        using (var stop = new CancellationTokenSource())
        {
            var committed = false;
            await processor.TranscribeAsync(Source("short.wav", 3), options, (chunk, _, _) =>
            {
                stop.Cancel(); return Task.FromResult(new TranscriptionResult(chunk.Name, chunk.MediaType, "p/model", new(), new() { ["text"] = "completed" }));
            }, (_, _) => { committed = true; return Task.CompletedTask; }, stop.Token);
            check(committed, "Stop arriving after successful response does not discard a completed result");
        }
        var changed = new TranscriptionSource("changed.wav", "audio/wav", 2, _ => Task.FromResult<Stream>(new MemoryStream(new byte[3])));
        try { await changed.CopyToAsync(new MemoryStream(), default); check(false, "source growth"); }
        catch (IOException) { check(true, "changed input is rejected instead of silently truncating media"); }
    }

    internal static TranscriptionSource Source(string name, long size, string type = "audio/wav") => new(name, type, size,
        _ => Task.FromResult<Stream>(new ZeroStream(size)));
    private sealed class ZeroStream(long length) : Stream
    {
        private long position;
        public override int Read(byte[] buffer, int offset, int count) { var read = (int)Math.Min(count, length - position); Array.Clear(buffer, offset, read); position += read; return read; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); var read = (int)Math.Min(buffer.Length, length - position); buffer.Span[..read].Clear(); position += read; return ValueTask.FromResult(read); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class CountingStream : Stream
    {
        public long Count; public int MaximumWrite;
        public override void Write(byte[] buffer, int offset, int count) { Count += count; MaximumWrite = Math.Max(MaximumWrite, count); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); Count += buffer.Length; MaximumWrite = Math.Max(MaximumWrite, buffer.Length); return ValueTask.CompletedTask; }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => Count; public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class FakeEncoder : ITranscriptionMediaEncoder
    {
        public double Duration = 600; public bool Fail, Oversized; public List<int> Bitrates = [];
        public Task<double> DurationAsync(string path, CancellationToken ct) => Fail ? Task.FromException<double>(new IOException("unsupported codec")) : Task.FromResult(Duration);
        public Task EncodeAsync(string input, string output, TranscriptionRange range, int bitrate, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Bitrates.Add(bitrate); using var file = File.Create(output); file.SetLength(Oversized ? 16 * TranscriptionChunkPlanning.MB : 3); return Task.CompletedTask; }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public string? Path; public JsonNode? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath; Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"ok\"}", Encoding.UTF8, "application/json") };
        }
    }
}
