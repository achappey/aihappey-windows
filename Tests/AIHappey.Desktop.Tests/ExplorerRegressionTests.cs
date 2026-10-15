using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using AIHappey.Desktop.Core;

internal static class ExplorerRegressionTests
{
    public static Task RunAsync(Action<bool, string> check, string root)
    {
        var id = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow;
        var request = new ExplorerRequest(1, id, now.ToUnixTimeSeconds(), "HeaderAuth", "test_family", [@"C:\a file.txt", @"\\server\share\日本語.pdf"]);
        byte[] Encode(ExplorerRequest value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web);
        void Reject(Action action, string label)
        {
            try { action(); } catch (Exception error) when (error is InvalidOperationException or ArgumentException or JsonException or UnauthorizedAccessException or IOException)
            { check(true, label); return; }
            check(false, label);
        }
        check(ExplorerRequest.Parse(Encode(request), id, request.Host, request.PackageFamily, now).Paths.SequenceEqual(request.Paths), "Explorer Unicode, spaces, UNC and ordered multi-selection");
        check(ExplorerRequest.ParseActivation("--shell-request " + id) == id && ExplorerRequest.ParseActivation("") is null, "Explorer identifier-only activation");
        check(ExplorerRequest.ParseActivation("\"C:\\Program Files\\WindowsApps\\package\\aihappey.exe\" --shell-request " + id) == id, "Packaged command line with quoted executable");
        check(ExplorerRequest.ParseActivation("C:\\portable\\aihappey.exe --shell-request " + id) == id, "Full desktop activation command line");
        check(ExplorerRequest.ParseActivation("\"C:\\a folder\\aihappey.exe\"") is null, "Normal desktop/debug launch without request");
        Reject(() => ExplorerRequest.ParseActivation("\"C:\\other.exe\" --shell-request " + id), "Reject foreign executable prefix");
        foreach (var invalid in new[] { "--shell-request ../bad", "--shell-request " + id + " extra", "C:\\file", "--submit", "--shell-request " + id.ToUpperInvariant() })
            Reject(() => ExplorerRequest.ParseActivation(invalid), "Reject malformed activation");
        foreach (var invalid in new[] { "relative.txt", @"C:\a\..\file", @"\\.\pipe\x", @"\\?\C:\x", @"C:\file:stream", @"C:\a\file" + '\0', "" })
            Reject(() => ExplorerRequest.ValidatePath(invalid), "Reject unsafe filesystem path");
        foreach (var invalid in new[] { request with { Version = 2 }, request with { Id = Guid.NewGuid().ToString("N") },
            request with { Host = "AzureAuth" }, request with { PackageFamily = "other" }, request with { Paths = [] }, request with { Paths = new string[1025] },
            request with { CreatedUnixSeconds = now.AddHours(-25).ToUnixTimeSeconds() }, request with { CreatedUnixSeconds = now.AddMinutes(2).ToUnixTimeSeconds() } })
            Reject(() => ExplorerRequest.Parse(Encode(invalid), id, request.Host, request.PackageFamily, now), "Reject invalid request envelope");
        Reject(() => ExplorerRequest.Parse(new byte[ExplorerRequest.MaximumBytes + 1], id, request.Host, request.PackageFamily, now), "Bound request allocation");
        Reject(() => ExplorerRequest.Parse("{}"u8, id, request.Host, request.PackageFamily, now), "Reject empty request envelope");
        check(DesktopPaths.ResolveProfile("HeaderAuth", null, root) == Path.Combine(root, "AIHappey", "Desktop", "HeaderAuth")
            && DesktopPaths.ResolveProfile("HeaderAuth", "family", root) != DesktopPaths.ResolveProfile("HeaderAuth", null, root), "Portable paths preserved, installed state isolated");
        check(DesktopPaths.InstanceKey("HeaderAuth", null, "sid", 1) != DesktopPaths.InstanceKey("HeaderAuth", "family", "sid", 1)
            && DesktopPaths.InstanceKey("HeaderAuth", "family", "sid", 1) != DesktopPaths.InstanceKey("AzureAuth", "family", "sid", 1)
            && DesktopPaths.InstanceKey("HeaderAuth", "family", "sid", 1) != DesktopPaths.InstanceKey("HeaderAuth", "family", "other", 1), "Distribution, host and user instance isolation");
        var state = Path.Combine(root, "package"); Directory.CreateDirectory(state);
        var store = new ExplorerRequestStore(state, request.Host, request.PackageFamily);
        var inbox = Path.Combine(state, "ShellRequests");
        store.Signal(id);
        check(store.TakeSignals().SequenceEqual([id]) && store.TakeSignals().Count == 0, "Argv-only redirected request signals consumed once");
        File.WriteAllBytes(Path.Combine(inbox, id + ".json"), Encode(request));
        check(store.Claim(id, now)?.Id == id && store.Claim(id, now) is null, "Atomic claim prevents duplicate activation");
        store.Complete(id, "attached");
        check(store.Claim(id, now) is null && !File.Exists(Path.Combine(inbox, id + ".claimed")), "Acknowledgement removes paths and prevents replay");
        var stale = Path.Combine(inbox, id + ".status"); File.SetLastWriteTimeUtc(stale, now.AddHours(-25).UtcDateTime);
        store.Cleanup(now); check(!File.Exists(stale), "Expiry removes orphan metadata");
        var malicious = new DirectoryInfo(inbox).GetAccessControl();
        malicious.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        new DirectoryInfo(inbox).SetAccessControl(malicious);
        Reject(() => new ExplorerRequestStore(state, request.Host, request.PackageFamily), "Reject non-private inbox");
        return Task.CompletedTask;
    }
}
