using System.Text.Json;

namespace AIHappey.Desktop.Core;

public sealed record LocalInstalledApp(string AppId, string Name);
public sealed record LocalInstalledAppList(IReadOnlyList<LocalInstalledApp> Apps, bool HasMore);
public sealed record LocalAppLaunchRequest(string Kind, string Target);

/// <summary>Targets, never command lines. Native shell access is supplied by the shell's Windows boundary.</summary>
public sealed class LocalAppLauncherTools(
    Func<string, int, CancellationToken, Task<LocalInstalledAppList>> list,
    Func<LocalAppLaunchRequest, CancellationToken, Task> launch,
    Func<bool>? isCurrent = null)
{
    public const string ListTool = "local_apps_list";
    public const string LaunchTool = "local_apps_launch";
    // Positive list: ShellExecute must never turn the document route into script/shortcut/installer execution.
    private static readonly HashSet<string> documentExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".log", ".json", ".xml", ".yaml", ".yml", ".csv", ".tsv", ".pdf", ".rtf", ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".mp3", ".wav", ".flac", ".mp4", ".mkv", ".webm" };

    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isCurrent?.Invoke() == false) throw new OperationCanceledException(ct);
    }

    public Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(async () =>
    {
        Check(ct);
        object result;
        if (name == ListTool)
        {
            if (input.EnumerateObject().Any(p => p.Name is not ("query" or "limit"))) throw Invalid();
            var query = input.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString()! : "";
            if (input.TryGetProperty("query", out q) && q.ValueKind != JsonValueKind.String || query.Length > 256 || query.Any(char.IsControl)) throw Invalid();
            var limit = DesktopLocalTools.Integer(input, "limit", 50, 100);
            var apps = await list(query, limit, ct); Check(ct);
            result = new { apps = apps.Apps.Take(limit).ToArray(), count = Math.Min(limit, apps.Apps.Count), hasMore = apps.HasMore || apps.Apps.Count > limit };
        }
        else if (name == LaunchTool)
        {
            // Validate unknown fields at runtime too. Silently ignoring an arguments field would be misleading and unsafe.
            if (input.EnumerateObject().Any(p => p.Name is not ("kind" or "target"))) throw Invalid();
            var request = Validate(new(DesktopLocalTools.Required(input, "kind"), DesktopLocalTools.Required(input, "target")));
            Check(ct); await launch(request, ct); Check(ct);
            result = new { launched = true, request.Kind, request.Target };
        }
        else throw new LocalToolException("Unsupported app launcher tool.");
        return DesktopLocalTools.Result(null, DesktopLocalTools.Text(JsonSerializer.Serialize(result, JsonSerializerOptions.Web)));
    }, ct);

    internal static LocalAppLaunchRequest Validate(LocalAppLaunchRequest request)
    {
        var target = request.Target;
        if (target.Length is < 1 or > 32700 || target.Any(char.IsControl)) throw Invalid();
        switch (request.Kind)
        {
            case "app":
                // IDs are matched exactly against fresh native Apps-folder discovery, never interpreted as shell commands.
                if (target.Length > 512 || target.IndexOfAny(['"', '/', '\\', ':', ';', '|', '<', '>']) >= 0) throw Invalid();
                break;
            case "url":
                if (target.Any(char.IsWhiteSpace) || target.Contains('"') || !Uri.TryCreate(target, UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0) throw Invalid();
                target = uri.AbsoluteUri;
                break;
            case "executable":
            case "document":
                target = LocalFilePaths.Normalize(target); LocalFilePaths.CheckNoLinks(target);
                if (!File.Exists(target) || (File.GetAttributes(target) & FileAttributes.Directory) != 0) throw Invalid();
                var extension = Path.GetExtension(target);
                if (request.Kind == "executable" ? !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) : !documentExtensions.Contains(extension)) throw Invalid();
                break;
            default: throw Invalid();
        }
        return request with { Target = target };
    }

    private static LocalToolException Invalid() => new(DesktopResources.Get("LocalAppInvalid"));
}
