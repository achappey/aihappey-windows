using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Desktop.Core;

public sealed class DesktopMcpServer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Url { get; set; } = "";
    public string? RegistryUrl { get; set; }
    public string? Version { get; set; }
    public IReadOnlyList<McpIcon> Icons { get; set; } = [];
    public bool Enabled { get; set; }
    [JsonIgnore] public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ProtectedHeaders { get; set; }

    public DesktopMcpServer Clone() => new() { Id = Id, Name = Name, Description = Description, Url = Url,
        RegistryUrl = RegistryUrl, Version = Version, Icons = Icons.ToArray(), Enabled = Enabled, Headers = new(Headers, StringComparer.OrdinalIgnoreCase) };
    public McpCatalogItem CatalogItem => new(Id, Name, Description, Url, Version, RegistryUrl) { Icons = Icons.ToArray() };
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 2048 || string.IsNullOrWhiteSpace(Name) || Name.Length > 256)
            throw new InvalidOperationException(DesktopResources.Get("McpInvalidName"));
        Url = McpValidation.Endpoint(Url).AbsoluteUri;
        Headers = McpValidation.Headers(Headers);
        Icons = (Icons ?? []).Where(icon => icon is not null && McpIcons.Supported(icon)).Take(32).ToArray();
    }
}

/// <summary>Replaceable credential protection (e.g. a future OAuth token store). Never part of conversation serialization.</summary>
public interface IMcpSecretProtector
{
    string Protect(string value, string purpose);
    string Unprotect(string value, string purpose);
}

public sealed class WindowsMcpSecretProtector : IMcpSecretProtector
{
    public string Protect(string value, string purpose) => Convert.ToBase64String(ProtectedData.Protect(
        Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(purpose), DataProtectionScope.CurrentUser));
    public string Unprotect(string value, string purpose) => Encoding.UTF8.GetString(ProtectedData.Unprotect(
        Convert.FromBase64String(value), Encoding.UTF8.GetBytes(purpose), DataProtectionScope.CurrentUser));
}

public sealed record McpStoreResult(IReadOnlyList<DesktopMcpServer> Servers, bool HasInvalidEntries);

public sealed class DesktopMcpStore(string root, IMcpSecretProtector? secretProtector = null)
{
    private readonly IMcpSecretProtector secrets = secretProtector ?? new WindowsMcpSecretProtector();
    private string PathFor(string partition) => Path.Combine(root, McpValidation.Hash(partition) + ".json");
    private static string Purpose(string partition, DesktopMcpServer server) => "AIHappey.Desktop.MCP|" + partition + "|" + server.Id + "|" + server.Url;

    public async Task<McpStoreResult> LoadAsync(string partition, CancellationToken ct = default)
    {
        var path = PathFor(partition);
        if (!File.Exists(path)) return new([], false);
        var servers = new List<DesktopMcpServer>(); var invalid = false;
        try
        {
            await using var stream = File.OpenRead(path);
            if (stream.Length > 8 * 1024 * 1024) return new([], true);
            var stored = await JsonSerializer.DeserializeAsync<List<DesktopMcpServer>>(stream, JsonSerializerOptions.Web, ct) ?? [];
            foreach (var server in stored.Take(200))
            {
                try
                {
                    server.Validate();
                    if (!string.IsNullOrEmpty(server.ProtectedHeaders))
                        server.Headers = McpValidation.Headers(JsonSerializer.Deserialize<Dictionary<string, string>>(
                            secrets.Unprotect(server.ProtectedHeaders, Purpose(partition, server))) ?? []);
                    server.ProtectedHeaders = null;
                    if (servers.Any(s => s.Id == server.Id)) { invalid = true; continue; }
                    servers.Add(server);
                }
                catch (Exception e) when (e is CryptographicException or FormatException or JsonException or InvalidOperationException or ArgumentException)
                { invalid = true; }
            }
            invalid |= stored.Count > 200;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { invalid = true; }
        return new(servers, invalid);
    }

    public async Task SaveAsync(string partition, IEnumerable<DesktopMcpServer> servers, CancellationToken ct = default)
    {
        var copies = servers.Select(s => s.Clone()).ToList();
        if (copies.Count > 200 || copies.Select(s => s.Id).Distinct().Count() != copies.Count)
            throw new InvalidOperationException(DesktopResources.Get("McpServerLimit"));
        foreach (var server in copies)
        {
            server.Validate();
            if (server.Headers.Count > 0) server.ProtectedHeaders = secrets.Protect(JsonSerializer.Serialize(server.Headers), Purpose(partition, server));
        }
        Directory.CreateDirectory(root);
        var path = PathFor(partition); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, copies, JsonSerializerOptions.Web, ct);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
