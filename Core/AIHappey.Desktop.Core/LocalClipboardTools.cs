using System.Text.Json;

namespace AIHappey.Desktop.Core;

/// <summary>Plain text only; the shell supplies the UI-thread native clipboard boundary.</summary>
public sealed class LocalClipboardTools(Func<CancellationToken, Task<string?>> read,
    Func<string, CancellationToken, Task> write, Func<bool>? isCurrent = null)
{
    public const string GetTool = "local_clipboard_get";
    public const string SetTool = "local_clipboard_set";
    public const int MaximumCharacters = 50_000;

    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isCurrent?.Invoke() == false) throw new OperationCanceledException(ct);
    }

    public Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(async () =>
    {
        Check(ct);
        switch (name)
        {
            case GetTool:
                var text = await read(ct); Check(ct);
                var truncated = text?.Length > MaximumCharacters;
                return DesktopLocalTools.Result(new { hasText = text is not null, text = text is null ? "" : text[..Math.Min(text.Length, MaximumCharacters)], truncated });
            case SetTool:
                if (!input.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String
                    || value.GetString() is not { } copy || copy.Length > MaximumCharacters || copy.Contains('\0'))
                    throw new LocalToolException(DesktopResources.Get("LocalClipboardTextInvalid"));
                Check(ct); await write(copy, ct); Check(ct);
                // Do not echo sensitive clipboard data into another output field.
                return DesktopLocalTools.Result(new { copied = true, characterCount = copy.Length });
            default: throw new LocalToolException("Unsupported clipboard tool.");
        }
    }, ct);
}
