using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace AIHappey.Desktop.Core;

/// <summary>Installed Windows OCR only. No remote image tools, downloads or screen capture.</summary>
public sealed class LocalOcrTools(Func<bool>? isCurrent = null)
{
    public const string LanguagesTool = "local_ocr_list_languages";
    public const string RecognizeTool = "local_ocr_recognize_text";
    public const int MaximumFileBytes = 20 * 1024 * 1024;
    public const int MaximumPixels = 16_000_000;
    public const int MaximumCharacters = 50_000;
    public const int MaximumLines = 500;
    public const int MaximumWords = 2000;
    private static readonly SemaphoreSlim workerGate = new(1, 1);

    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isCurrent?.Invoke() == false) throw new OperationCanceledException(ct);
    }

    public Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(async () =>
    {
        Check(ct);
        if (name == LanguagesTool)
        {
            var languages = OcrEngine.AvailableRecognizerLanguages.Select(l => new { language = l.LanguageTag, name = l.DisplayName }).ToArray();
            Check(ct);
            return DesktopLocalTools.Result(new { languages, count = languages.Length, maxImageDimension = OcrEngine.MaxImageDimension });
        }
        if (name != RecognizeTool) throw new LocalToolException("Unsupported OCR tool.");
        var path = LocalFilePaths.Normalize(DesktopLocalTools.Required(input, "path"));
        if (Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff"))
            throw new LocalToolException(DesktopResources.Get("LocalOcrUnsupported"));
        string? language = null;
        if (input.TryGetProperty("language", out var requested))
        {
            if (requested.ValueKind != JsonValueKind.String || requested.GetString() is not { Length: > 0 and <= 85 } tag)
                throw new LocalToolException(DesktopResources.Get("LocalOcrLanguageUnavailable"));
            language = tag;
        }
        return await RecognizeAsync(path, language, ct);
    }, ct);

    private async Task<JsonElement> RecognizeAsync(string path, string? language, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        try
        {
            await workerGate.WaitAsync(token);
            var work = Task.Run(async () =>
            {
                try { return await RecognizeImageAsync(path, language, token); }
                finally { workerGate.Release(); }
            });
            _ = work.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return await work.WaitAsync(token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new LocalToolException(DesktopResources.Get("LocalOcrTimeout")); }
    }

    private async Task<JsonElement> RecognizeImageAsync(string path, string? language, CancellationToken ct)
    {
        Check(ct);
        byte[] bytes;
        try
        {
            LocalFilePaths.CheckNoLinks(path); Check(ct);
            if (Directory.Exists(path)) throw new LocalToolException(DesktopResources.Get("LocalFileNotFile"));
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            LocalFilePaths.CheckOpenedFile(file.SafeFileHandle, path); Check(ct);
            if (file.Length > MaximumFileBytes) throw new LocalToolException(DesktopResources.Get("LocalOcrTooLarge"));
            bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, ct);
            if (await file.ReadAsync(new byte[1], ct) != 0) throw new LocalToolException(DesktopResources.Get("LocalFileChanged"));
        }
        catch (FileNotFoundException) { throw new LocalToolException(DesktopResources.Get("LocalFileNotFound")); }
        catch (DirectoryNotFoundException) { throw new LocalToolException(DesktopResources.Get("LocalFileNotFound")); }
        catch (UnauthorizedAccessException) { throw new LocalToolException(DesktopResources.Get("LocalFileAccessDenied")); }
        Check(ct);
        OcrEngine? engine;
        try
        {
            if (language is null) engine = OcrEngine.TryCreateFromUserProfileLanguages();
            else
            {
                if (!Language.IsWellFormed(language)) throw new LocalToolException(DesktopResources.Get("LocalOcrLanguageUnavailable"));
                var selected = new Language(language);
                if (!OcrEngine.IsLanguageSupported(selected)) throw new LocalToolException(DesktopResources.Get("LocalOcrLanguageUnavailable"));
                engine = OcrEngine.TryCreateFromLanguage(selected);
            }
        }
        catch (ArgumentException) { throw new LocalToolException(DesktopResources.Get("LocalOcrLanguageUnavailable")); }
        catch (COMException) { throw new LocalToolException(DesktopResources.Get("LocalOcrUnavailable")); }
        if (engine is null) throw new LocalToolException(DesktopResources.Get(language is null ? "LocalOcrUnavailable" : "LocalOcrLanguageUnavailable"));

        try
        {
            // Decode the validated bytes, never reopen the path through a second API.
            using var memory = new InMemoryRandomAccessStream();
            await memory.WriteAsync(bytes.AsBuffer()).AsTask(ct); memory.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(memory).AsTask(ct); Check(ct);
            if (decoder.DecoderInformation.CodecId != BitmapDecoder.PngDecoderId && decoder.DecoderInformation.CodecId != BitmapDecoder.JpegDecoderId
                && decoder.DecoderInformation.CodecId != BitmapDecoder.BmpDecoderId && decoder.DecoderInformation.CodecId != BitmapDecoder.TiffDecoderId)
                throw new LocalToolException(DesktopResources.Get("LocalOcrUnsupported"));
            var width = decoder.PixelWidth; var height = decoder.PixelHeight;
            if (width == 0 || height == 0 || (ulong)width * height > MaximumPixels || width > OcrEngine.MaxImageDimension || height > OcrEngine.MaxImageDimension)
                throw new LocalToolException(DesktopResources.Get("LocalOcrTooLarge"));
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);
            Check(ct);
            var result = await engine.RecognizeAsync(bitmap).AsTask(ct); Check(ct);
            var text = new StringBuilder(); var lines = new List<object>(); var wordCount = 0; var truncated = false; var geometryTruncated = false;
            foreach (var line in result.Lines)
            {
                Check(ct);
                if (lines.Count == MaximumLines || text.Length == MaximumCharacters) { truncated = true; break; }
                if (text.Length > 0) text.Append('\n');
                var take = Math.Min(line.Text.Length, MaximumCharacters - text.Length);
                var lineText = line.Text[..take]; text.Append(lineText);
                if (take < line.Text.Length) truncated = true;
                var words = new List<object>();
                foreach (var word in line.Words)
                {
                    if (wordCount == MaximumWords) { geometryTruncated = true; break; }
                    if (word.Text.Length > 512) { geometryTruncated = true; continue; }
                    wordCount++;
                    var rect = word.BoundingRect;
                    words.Add(new { text = word.Text, x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height });
                }
                lines.Add(new { text = lineText, words });
            }
            Check(ct);
            return DesktopLocalTools.Result(new { path, language = engine.RecognizerLanguage.LanguageTag, width, height, frameIndex = 0,
                text = text.ToString(), lines, wordCount, truncated, geometryTruncated, textAngle = result.TextAngle });
        }
        catch (Exception error) when (error is COMException or ArgumentException)
        { throw new LocalToolException(DesktopResources.Get("LocalOcrInvalidImage")); }
    }
}
