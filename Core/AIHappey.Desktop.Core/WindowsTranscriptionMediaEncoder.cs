using NAudio.Wave;
using NAudio.MediaFoundation;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace AIHappey.Desktop.Core;

/// <summary>Windows Media Foundation decoder and native trimmed AAC transcoder; no bundled codec runtime.</summary>
public sealed class WindowsTranscriptionMediaEncoder : ITranscriptionMediaEncoder
{
    public Task<double> DurationAsync(string inputPath, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var reader = new MediaFoundationReader(inputPath);
        return reader.TotalTime.TotalSeconds;
    }, ct);

    public async Task EncodeAsync(string inputPath, string outputPath, TranscriptionRange range, int bitrateKbps, CancellationToken ct)
    {
        var input = await StorageFile.GetFileFromPathAsync(inputPath).AsTask(ct);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(outputPath)!).AsTask(ct);
        var output = await folder.CreateFileAsync(Path.GetFileName(outputPath), CreationCollisionOption.FailIfExists).AsTask(ct);
        var profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High);
        profile.Audio.ChannelCount = 1; profile.Audio.SampleRate = 44100;
        // Windows exposes discrete AAC rates and cannot accept the browser's arbitrarily high
        // PCM-derived target. Keep the identical plan, choosing a supported encoder rate below it.
        var rates = MediaFoundationEncoder.GetEncodeBitrates(AudioSubtypes.MFAudioFormat_AAC, 44100, 1).Order().ToArray();
        if (rates.Length == 0) throw new InvalidOperationException(DesktopResources.Get("TranscriptionCodecUnavailable"));
        profile.Audio.Bitrate = (uint)(rates.LastOrDefault(rate => rate <= bitrateKbps * 1000, rates[0]));
        var transcoder = new MediaTranscoder { TrimStartTime = TimeSpan.FromSeconds(range.StartSecond),
            TrimStopTime = TimeSpan.FromSeconds(range.EndSecond), AlwaysReencode = true };
        // Explicit stream ownership is essential on cancellation: file-based transcoding can
        // retain an input handle past the canceled task and prevent the splitter's cleanup.
        using var inputStream = await input.OpenAsync(FileAccessMode.Read);
        using var outputStream = await output.OpenAsync(FileAccessMode.ReadWrite);
        var preparation = transcoder.PrepareStreamTranscodeAsync(inputStream, outputStream, profile);
        PrepareTranscodeResult prepared;
        try { using (ct.Register(preparation.Cancel)) prepared = await preparation.AsTask(); }
        finally { preparation.Close(); }
        ct.ThrowIfCancellationRequested();
        if (!prepared.CanTranscode) throw new InvalidOperationException(DesktopResources.Get("TranscriptionCodecUnavailable"));
        var action = prepared.TranscodeAsync();
        try { using (ct.Register(action.Cancel)) await action.AsTask(); }
        finally { action.Close(); }
        ct.ThrowIfCancellationRequested();
    }
}
