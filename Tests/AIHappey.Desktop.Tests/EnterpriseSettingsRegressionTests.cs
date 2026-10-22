using AIHappey.Desktop.Core;
using AIHappey_Desktop_AzureAuth;

internal static class EnterpriseSettingsRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var directory = Path.Combine(root, "enterprise-settings");
        var config = new EnterpriseConfiguration
        {
            AiUrl = "https://ai.enterprise.example/gateway",
            AgentsUrl = "https://agents.enterprise.example"
        };
        var saved = new DesktopSettings
        {
            Ai = new() { Location = RuntimeLocation.Remote, RemoteUrl = "https://old-ai.example" },
            Agents = new() { Location = RuntimeLocation.Remote, RemoteUrl = "https://old-agents.example" },
            Language = "nl", ConvertAttachmentsToText = false,
            Chat = new() { ShowTokens = true, ShowProviderLogo = false },
            AiModels = new() { LanguageModel = "provider/model" },
            ModelContext = new() { ToolTimeoutMinutes = 19 }
        };
        await SettingsStore.SaveAsync(directory, saved);
        var loaded = await SettingsStore.LoadAsync(directory, config.Settings, config.ApplySettingsPolicy);
        loaded.Validate(false);
        check(loaded.Ai.RemoteUrl == config.AiUrl && loaded.Agents.RemoteUrl == config.AgentsUrl,
            "enterprise settings: host gateways override stale saved addresses before validation");
        check(loaded.Language == "nl" && !loaded.ConvertAttachmentsToText && loaded.Chat.ShowTokens
            && !loaded.Chat.ShowProviderLogo && loaded.AiModels.LanguageModel == "provider/model"
            && loaded.ModelContext.ToolTimeoutMinutes == 19,
            "enterprise settings: gateway policy preserves user preferences");
        foreach (var service in new[] { ServiceKind.Ai, ServiceKind.Agents })
        {
            var endpoint = await new RemoteRuntimeResolver().ResolveAsync(service, loaded, default);
            config.ValidateDestination(new Uri(endpoint, "v1/models"), service);
            config.ValidateDestination(new Uri(endpoint, "api/chat"), service);
            check(true, $"enterprise settings: {service} catalog and chat destinations pass the real token guard");
        }
        await SettingsStore.SaveAsync(directory, loaded);
        config.AiUrl = "https://new-ai.enterprise.example/api";
        config.AgentsUrl = "https://new-agents.enterprise.example/api";
        loaded = await SettingsStore.LoadAsync(directory, config.Settings, config.ApplySettingsPolicy);
        check(loaded.Ai.RemoteUrl == config.AiUrl && loaded.Agents.RemoteUrl == config.AgentsUrl && loaded.Language == "nl",
            "enterprise settings: changed distributor gateways take effect on restart without resetting preferences");
        await SettingsStore.SaveAsync(directory, new DesktopSettings());
        loaded = await SettingsStore.LoadAsync(directory, config.Settings, config.ApplySettingsPolicy);
        loaded.Validate(false);
        check(loaded.Ai.Location == RuntimeLocation.Remote && loaded.Agents.Location == RuntimeLocation.Remote,
            "enterprise settings: legacy local settings are replaced before remote-only validation");
        var firstRun = await SettingsStore.LoadAsync(Path.Combine(root, "enterprise-first-run"), new(), config.ApplySettingsPolicy);
        firstRun.Validate(false);
        check(firstRun.Ai.RemoteUrl == config.AiUrl && firstRun.Agents.RemoteUrl == config.AgentsUrl,
            "enterprise settings: policy also applies when no saved settings exist");
        var publicSettings = await SettingsStore.LoadAsync(directory, config.Settings);
        check(publicSettings.Ai.Location == RuntimeLocation.Local && publicSettings.Agents.Location == RuntimeLocation.Local,
            "enterprise settings: loading without host policy preserves HeaderAuth endpoint selection");

        foreach (var destination in new[]
        {
            (Uri?)null, new Uri("https://attacker.example/api/v1/models"),
            new Uri("http://new-ai.enterprise.example/api/v1/models"),
            new Uri("https://new-ai.enterprise.example:444/api/v1/models"),
            new Uri("https://new-agents.enterprise.example/api/v1/models"),
            new Uri("https://new-ai.enterprise.example/api-other/v1/models"),
            new Uri("https://new-ai.enterprise.example/api/../v1/models")
        })
        {
            try { config.ValidateDestination(destination, ServiceKind.Ai); }
            catch (InvalidOperationException)
            {
                check(true, "enterprise settings: unapproved token destination remains rejected");
                continue;
            }
            throw new Exception("Unapproved enterprise destination was accepted.");
        }
    }
}
