using System.Text.Json;
using AIHappey.Desktop.Core;

namespace AIHappey_Desktop_AzureAuth;

public sealed class EnterpriseConfiguration
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string AiUrl { get; set; } = "";
    public string AgentsUrl { get; set; } = "";
    public string[] AiScopes { get; set; } = [];
    public string[] AgentsScopes { get; set; } = [];
    public string? ChatbotInstructions { get; set; }
    public Dictionary<string, string[]>? DefaultProvidersByType { get; set; }

    public static EnterpriseConfiguration Load()
    {
        var overridePath = Path.Combine(DesktopPaths.ProfileDirectory("AzureAuth"), "desktop.json");
        var path = File.Exists(overridePath) ? overridePath : Path.Combine(AppContext.BaseDirectory, "desktop.json");
        try { return JsonSerializer.Deserialize<EnterpriseConfiguration>(File.ReadAllText(path), JsonSerializerOptions.Web) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    public DesktopSettings Settings => new()
    {
        Ai = new() { Location = RuntimeLocation.Remote, RemoteUrl = AiUrl },
        Agents = new() { Location = RuntimeLocation.Remote, RemoteUrl = AgentsUrl }
    };

    public void ApplySettingsPolicy(DesktopSettings settings)
    {
        // Gateway configuration is host-owned; settings.json only supplies user preferences.
        var configured = Settings;
        settings.Ai = configured.Ai;
        settings.Agents = configured.Agents;
    }

    public void ValidateDestination(Uri? destination, ServiceKind service)
    {
        var allowed = DesktopSettings.RemoteUri(service == ServiceKind.Ai ? AiUrl : AgentsUrl);
        if (destination is null || !allowed.IsBaseOf(destination))
            throw new InvalidOperationException(DesktopResources.Get("EnterpriseDestinationRejected"));
    }
}
