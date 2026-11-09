using System.Security.Cryptography;
using AIHappey.Desktop.Core;
using NAudio.Wave;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

internal static class TranscriptionNativeTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        Directory.CreateDirectory(root); var encoder = new WindowsTranscriptionMediaEncoder();
        var shortPath = Path.Combine(root, "short.wav"); WriteWave(shortPath, 2 * 48000 * 4 + 44);
        check(Math.Abs(await encoder.DurationAsync(shortPath, default) - 2) < 0.001, "Windows native duration probe reads PCM audio");
        var m4a = Path.Combine(root, "short.m4a");
        await encoder.EncodeAsync(shortPath, m4a, new(0.5, 1.5), 1200, default);
        using (var audio = new MediaFoundationReader(m4a))
            check(audio.WaveFormat.Channels == 1 && Math.Abs(audio.TotalTime.TotalSeconds - 1) < 0.25,
                "Windows AAC encoder handles PCM-derived high bitrate, downmixes mono and trims a one-second range");

        // Real audio+video container verifies extraction, not merely a video MIME label on audio.
        var input = await StorageFile.GetFileFromPathAsync(shortPath);
        var composition = new MediaComposition();
        composition.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 0, 0, 0), TimeSpan.FromSeconds(2)));
        composition.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(input));
        var folder = await StorageFolder.GetFolderFromPathAsync(root);
        var video = await folder.CreateFileAsync("video.mp4", CreationCollisionOption.FailIfExists);
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Qvga);
        check(await composition.RenderToFileAsync(video, MediaTrimmingPreference.Precise, profile) == TranscodeFailureReason.None,
            "native video fixture renders with audio");
        var extracted = Path.Combine(root, "video-audio.m4a");
        await encoder.EncodeAsync(video.Path, extracted, new(0, 1.5), 128, default);
        using (var audio = new MediaFoundationReader(extracted))
            check(audio.WaveFormat.Channels == 1 && Math.Abs(audio.TotalTime.TotalSeconds - 1.5) < 0.25, "native splitting extracts only mono AAC from video");

        var path = Path.Combine(root, "100mb.wav"); WriteWave(path, 100 * 1024 * 1024);
        var original = TranscriptionSource.FromPath("100mb.wav", "audio/wav", path);
        var duration = await encoder.DurationAsync(path, default);
        var options = new TranscriptionPreferences(); var plan = TranscriptionChunkPlanning.Create(duration, original.Length, 15, 5);
        var temporary = Path.Combine(root, "temporary");
        var library = new TranscriptionLibraryStore(Path.Combine(root, "library"), "native-codec-account");
        var chunkCount = 0;
        await new TranscriptionFileProcessor(new(encoder, temporary)).TranscribeAsync(original, options, (file, preferences, ct) =>
        {
            using var reader = new MediaFoundationReader(FindChunk(temporary, file.Name));
            var index = int.Parse(file.Name.Split(".part-")[1][..3]) - 1; var range = plan.Ranges[index];
            check(file.Length <= plan.TargetPayloadBytes && reader.WaveFormat.Channels == 1
                && Math.Abs(reader.TotalTime.TotalSeconds - (range.EndSecond - range.StartSecond)) < 0.25,
                "native 100 MB input has bounded decodable audio and exact requested range " + index);
            Interlocked.Increment(ref chunkCount);
            return Task.FromResult(new TranscriptionResult(file.Name, file.MediaType, "fake/native-codec", preferences.RequestOptions(),
                new() { ["text"] = "native chunk " + index }));
        }, (result, source) => library.SaveAsync(result, source), default);
        var saved = (await library.ListAsync()).Single();
        check(chunkCount == plan.Ranges.Count && chunkCount > 1 && !Directory.EnumerateDirectories(temporary).Any(),
            "actual 100 MB Windows splitting completes every chunk and removes all temporary media");
        await using var before = File.OpenRead(path); await using var after = File.OpenRead(saved.Path);
        var beforeHash = await SHA256.HashDataAsync(before); var afterHash = await SHA256.HashDataAsync(after);
        check(beforeHash.SequenceEqual(afterHash),
            "native full flow persists one transcript with byte-identical original media");

        var invalidPath = Path.Combine(root, "unsupported.webm");
        await File.WriteAllBytesAsync(invalidPath, new byte[16 * 1024 * 1024]);
        try
        {
            using var files = await new TranscriptionFileSplitter(encoder, temporary).SplitAsync(
                TranscriptionSource.FromPath("unsupported.webm", "audio/webm", invalidPath), options, default);
            check(false, "native unsupported codec must fail");
        }
        catch (InvalidOperationException) { check(!Directory.EnumerateDirectories(temporary).Any(), "native unsupported codec reports error and cleans staging without upload"); }
    }

    private static string FindChunk(string root, string name)
    {
        var index = int.Parse(name.Split(".part-")[1][..3]) - 1;
        return Directory.EnumerateFiles(root, $"chunk-{index}.m4a", SearchOption.AllDirectories).Single();
    }

    private static void WriteWave(string path, int size)
    {
        using var stream = File.Create(path); using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(size - 8); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)2); writer.Write(48000); writer.Write(48000 * 4);
        writer.Write((short)4); writer.Write((short)16); writer.Write("data"u8); writer.Write(size - 44);
        stream.SetLength(size);
    }
}
