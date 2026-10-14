using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace AIHappey.Desktop.Core;

public sealed record ExplorerRequest(int Version, string Id, long CreatedUnixSeconds, string Host, string PackageFamily, string[] Paths)
{
    public const int MaximumPaths = 1024;
    public const int MaximumBytes = 1024 * 1024;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    public static string? ParseActivation(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return null;
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0] != "--shell-request" || !ValidId(parts[1]))
            throw new InvalidOperationException("Invalid Explorer activation.");
        return parts[1];
    }
    public static bool ValidId(string value) => value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static ExplorerRequest Parse(ReadOnlySpan<byte> bytes, string id, string host, string packageFamily, DateTimeOffset now)
    {
        if (bytes.Length > MaximumBytes || !ValidId(id)) throw new InvalidOperationException("Invalid Explorer request size or identifier.");
        var request = JsonSerializer.Deserialize<ExplorerRequest>(bytes, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("Empty Explorer request.");
        if (request.Version != 1 || request.Id != id || request.Host != host || request.PackageFamily != packageFamily
            || request.Paths is not { Length: > 0 and <= MaximumPaths }) throw new InvalidOperationException("Invalid Explorer request envelope.");
        var age = now.ToUnixTimeSeconds() - request.CreatedUnixSeconds;
        if (age < -60 || age > Lifetime.TotalSeconds) throw new InvalidOperationException("Expired Explorer request.");
        foreach (var path in request.Paths) ValidatePath(path);
        return request;
    }
    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || path.Contains('\0')
            || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(path) || path.Split('\\', '/').Any(p => p is "." or "..")
            || path.AsSpan(2).Contains(':')) throw new InvalidOperationException("Invalid filesystem path.");
    }
}

/// <summary>Same-user, package-scoped inbox. Identifiers are capabilities only within that trust boundary.</summary>
public sealed class ExplorerRequestStore
{
    private readonly string directory;
    private readonly string host;
    private readonly string family;
    private static SecurityIdentifier User => WindowsIdentity.GetCurrent().User!;
    public ExplorerRequestStore(string packageState, string host, string family)
    {
        directory = Path.Combine(packageState, "ShellRequests"); this.host = host; this.family = family;
        // Check every existing ancestor to avoid following an attacker-controlled junction.
        CheckAncestors(packageState);
        var security = new DirectorySecurity();
        security.SetOwner(User); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(User, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (!Directory.Exists(directory)) new DirectoryInfo(directory).Create(security);
        CheckPrivate(new DirectoryInfo(directory));
    }
    private static void CheckAncestors(string path)
    {
        for (var item = new DirectoryInfo(path); item is not null; item = item.Parent)
            if (item.Exists && item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("Unsafe request directory.");
    }
    private static void CheckPrivate(FileSystemInfo item)
    {
        if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("Unsafe request entry.");
        FileSystemSecurity acl = item is DirectoryInfo dir ? dir.GetAccessControl() : ((FileInfo)item).GetAccessControl();
        if (!User.Equals(acl.GetOwner(typeof(SecurityIdentifier)))) throw new InvalidOperationException("Invalid request owner.");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !User.Equals(rule.IdentityReference)
                && !new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Equals(rule.IdentityReference))
                throw new InvalidOperationException("Request entry is not private.");
    }
    public ExplorerRequest? Claim(string id, DateTimeOffset now)
    {
        if (!ExplorerRequest.ValidId(id)) throw new InvalidOperationException("Invalid request identifier.");
        CheckPrivate(new DirectoryInfo(directory));
        var source = Path.Combine(directory, id + ".json");
        var claimed = Path.Combine(directory, id + ".claimed");
        if (File.Exists(Path.Combine(directory, id + ".status")) || File.Exists(claimed)) return null;
        CheckPrivate(new FileInfo(source));
        if (new FileInfo(source).Length > ExplorerRequest.MaximumBytes) throw new InvalidOperationException("Explorer selection is too large.");
        try { File.Move(source, claimed, false); }
        catch (IOException) when (File.Exists(claimed) || File.Exists(Path.Combine(directory, id + ".status"))) { return null; }
        try
        {
            using var stream = new FileStream(claimed, FileMode.Open, FileAccess.Read, FileShare.None);
            if (stream.Length > ExplorerRequest.MaximumBytes) throw new InvalidOperationException("Explorer selection is too large.");
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            return ExplorerRequest.Parse(bytes, id, host, family, now);
        }
        catch { Complete(id, "rejected"); throw; }
    }
    public void Complete(string id, string outcome)
    {
        if (!ExplorerRequest.ValidId(id)) throw new InvalidOperationException("Invalid request identifier.");
        var status = Path.Combine(directory, id + ".status");
        using (var stream = new FileStream(status, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, new { outcome, completedUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        File.Delete(Path.Combine(directory, id + ".claimed"));
    }
    public void Cleanup(DateTimeOffset now)
    {
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
        {
            if (!ExplorerRequest.ValidId(Path.GetFileNameWithoutExtension(file.Name))) continue;
            CheckPrivate(file);
            if (now.UtcDateTime - file.LastWriteTimeUtc > ExplorerRequest.Lifetime) file.Delete();
        }
    }
    public bool RecoverInterrupted()
    {
        var recovered = false;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.claimed"))
        {
            var id = Path.GetFileNameWithoutExtension(file.Name);
            if (!ExplorerRequest.ValidId(id)) continue;
            CheckPrivate(file);
            // Never replay an interrupted request into a potentially different draft or account.
            if (!File.Exists(Path.Combine(directory, id + ".status"))) Complete(id, "interrupted");
            else file.Delete();
            recovered = true;
        }
        return recovered;
    }
}
