using System.Text.Json;
using AIHappey.Desktop.Core;
using AIHappey_Desktop_AzureAuth;
using AIHappey_Desktop_HeaderAuth;

internal static class ProviderSettingsRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        void Check(bool value, string description) => check(value, "provider settings: " + description);
        var directory = Path.Combine(root, "provider-settings");
        var defaults = new Dictionary<string, string[]> { ["language"] = [" openai ", "OpenAI", "Anthropic"], ["image"] = ["Google"] };
        var settings = new DesktopSettings();
        Check(ProviderPreferences.Filter(Models(), settings).Count == 0, "unconfigured types start empty, not implicitly all enabled");
        Check(ProviderPreferences.SeedDefaults(settings, defaults), "first-run defaults are seeded");
        Check(settings.EnabledProvidersByType["language"].SequenceEqual(["OpenAI", "Anthropic"]), "display names and IDs normalize and deduplicate");
        Check(!ProviderPreferences.SeedDefaults(settings, defaults), "seeding is idempotent");
        defaults["language"][0] = "Google";
        Check(settings.EnabledProvidersByType["language"][0] == "OpenAI", "configuration arrays are not aliased");
        var clone = settings.Clone(); clone.EnabledProvidersByType["language"][0] = "Google";
        ProviderPreferences.SetEnabled(clone, "image", "OpenAI", true);
        Check(settings.EnabledProvidersByType["language"][0] == "OpenAI" && settings.EnabledProvidersByType["image"].SequenceEqual(["Google"]), "cloning isolates both dictionary and arrays");
        ProviderPreferences.SetEnabled(settings, "language", "OpenAI", false);
        ProviderPreferences.SetEnabled(settings, "language", "Anthropic", false);
        await SettingsStore.SaveAsync(directory, settings);
        var loaded = await SettingsStore.LoadAsync(directory, new());
        Check(loaded.EnabledProvidersByType.ContainsKey("language") && loaded.EnabledProvidersByType["language"].Length == 0, "explicit-empty survives a real save and reload");
        Check(!ProviderPreferences.SeedDefaults(loaded, defaults) && loaded.EnabledProvidersByType["language"].Length == 0, "restart cannot re-enable a disabled type");
        Check(ProviderPreferences.SeedDefaults(loaded, new Dictionary<string, string[]> { ["decision"] = ["Echo"] }), "newly configured unsaved types can be seeded later");
        Check(!loaded.EnabledProvidersByType.ContainsKey("speech"), "unconfigured types remain distinguishable from explicit-empty");
        ProviderPreferences.SetEnabled(loaded, "language", "OpenAI", true);
        ProviderPreferences.SetEnabled(loaded, "image", "Google", false);
        var models = Models();
        Check(ProviderPreferences.Filter(models, loaded).Select(m => m.Id).SequenceEqual(["openai/chat"]), "language and image enablement are independent for the same provider");
        ProviderPreferences.SetEnabled(loaded, "image", "Google", true);
        Check(ProviderPreferences.Filter(models, loaded).Select(m => m.Id).SequenceEqual(["openai/chat", "google/image"]), "each model uses its own type's filter");
        var favorites = new HashSet<string> { ModelOverviewCatalog.FavoriteKey(models[1]) };
        Check(ProviderPreferences.Filter(models, loaded, favorites).Count == 3, "explicit favorites remain picker shortcuts across disabled providers");
        Check(ProviderPreferences.Filter(models, loaded).Count == 2, "overview filtering does not use picker favorite exceptions");
        var overridden = new ChatTarget("gateway/model", "Source override") { ModelType = "language", ProviderKey = "openai" };
        Check(ProviderPreferences.Filter([overridden], loaded).Count == 1, "source-provider metadata wins over a routing ID prefix");
        var unknown = new ChatTarget("custom/model", "Unknown") { ModelType = "decision" };
        ProviderPreferences.SetEnabled(loaded, "decision", "custom", true);
        Check(ProviderPreferences.Filter([unknown], loaded).Count == 1, "unknown discovered providers remain usable");
        var groups = ProviderPreferences.Groups(models.Concat([overridden, models[0]]));
        Check(groups["language"].SequenceEqual(["Anthropic", "OpenAI"]) && groups["image"].SequenceEqual(["Google", "OpenAI"]), "groups are sorted, deduplicated, and derive from the complete discovery catalog");
        Check(ProviderPreferences.Groups([]).Count == 0 && loaded.EnabledProvidersByType["decision"].Contains("custom"), "empty discovery does not erase saved preferences");
        var legacy = JsonSerializer.Deserialize<DesktopSettings>("""{"enabledProvidersByType":{"realtime":["openai"],"audio":[],"image":null,"bogus":["OpenAI"],"language":[null," ","OpenAI","openai"]}}""", JsonSerializerOptions.Web)!;
        Check(legacy.EnabledProvidersByType["audio"].Length == 0 && !legacy.EnabledProvidersByType.ContainsKey("realtime"), "explicit audio wins over the legacy realtime alias");
        Check(legacy.EnabledProvidersByType["image"].Length == 0 && !legacy.EnabledProvidersByType.ContainsKey("bogus") && legacy.EnabledProvidersByType["language"].SequenceEqual(["OpenAI"]), "null lists, invalid types, empty names, and duplicates normalize safely");
        var realtime = ProviderPreferences.Normalize(new Dictionary<string, string[]> { ["realtime"] = ["OpenAI"] });
        Check(realtime["audio"].SequenceEqual(["OpenAI"]), "legacy realtime configuration migrates to audio");
        var nullSettings = JsonSerializer.Deserialize<DesktopSettings>("""{"enabledProvidersByType":null}""", JsonSerializerOptions.Web)!;
        Check(nullSettings.EnabledProvidersByType.Count == 0, "null settings object is safe");

        // Distinct credential sentinels cannot accidentally match legitimate setting names (e.g. "threshold").
        var before = new Dictionary<string, string> { ["X-OpenAI-Key"] = "fixture-secret-old-61932", ["X-Anthropic-Key"] = "fixture-secret-removed-72184", ["X-Other"] = "fixture-secret-untouched-81293" };
        var after = new Dictionary<string, string> { ["x-openai-key"] = "fixture-secret-changed-91024", ["X-Google-Key"] = "fixture-secret-new-10635", ["X-Other"] = "fixture-secret-custom-21746" };
        var changes = ProviderPreferences.CredentialChanges(before, after);
        Check(changes.Count == 3 && changes["OpenAI"] && changes["Google"] && !changes["Anthropic"], "key edits enable and removals disable recognized providers, case-insensitively");
        ProviderPreferences.ApplyCredentialChanges(loaded, changes);
        Check(AiModelCatalog.Types.All(type => ProviderPreferences.IsEnabled(loaded, type, "OpenAI") && ProviderPreferences.IsEnabled(loaded, type, "Google") && !ProviderPreferences.IsEnabled(loaded, type, "Anthropic")), "key synchronization covers every capability");
        Check(ProviderPreferences.CredentialChanges(after, after).Count == 0, "unchanged keys cannot overwrite manual provider selections");
        Check(ProviderPreferences.CredentialChanges(new Dictionary<string, string>(), new Dictionary<string, string> { ["X-OpenAI-Key"] = " " })["OpenAI"] == false, "blank keys never enable providers");
        var serialized = JsonSerializer.Serialize(loaded, JsonSerializerOptions.Web);
        Check(before.Values.Concat(after.Values).All(value => !serialized.Contains(value, StringComparison.Ordinal))
            && before.Keys.Concat(after.Keys).All(key => !serialized.Contains(key, StringComparison.OrdinalIgnoreCase)),
            "provider preferences never serialize credential values or headers");
        var large = Enumerable.Range(0, 12000).Select(i => new ChatTarget("disabled/" + i, "Hidden") { ModelType = "language" }).Concat(models);
        Check(ProviderPreferences.Filter(large, loaded).Count == 3, "large catalog filtering excludes disabled providers before picker limits");

        var configPath = Path.Combine(root, "provider-defaults.json");
        await File.WriteAllTextAsync(configPath, """{"defaultProvidersByType":{"language":["Echo"],"decision":[],"realtime":["OpenAI"]}}""");
        var header = HeaderConfiguration.LoadFile(configPath);
        var enterprise = JsonSerializer.Deserialize<EnterpriseConfiguration>(await File.ReadAllTextAsync(configPath), JsonSerializerOptions.Web)!;
        var headerSettings = new DesktopSettings(); var enterpriseSettings = new DesktopSettings();
        ProviderPreferences.SeedDefaults(headerSettings, header.DefaultProvidersByType);
        ProviderPreferences.SeedDefaults(enterpriseSettings, enterprise.DefaultProvidersByType);
        Check(JsonSerializer.Serialize(headerSettings.EnabledProvidersByType) == JsonSerializer.Serialize(enterpriseSettings.EnabledProvidersByType), "both sample config shapes produce identical default preferences");
        Check(headerSettings.EnabledProvidersByType["decision"].Length == 0 && headerSettings.EnabledProvidersByType["audio"].SequenceEqual(["OpenAI"]), "sample defaults support explicit-empty and realtime alias");
        Check(HeaderConfiguration.LoadFile(Path.Combine(root, "absent-config.json")).DefaultProvidersByType is null, "HeaderAuth configuration is optional");
        enterprise.AiUrl = "https://ai.example"; enterprise.AgentsUrl = "https://agents.example";
        ProviderPreferences.SetEnabled(enterpriseSettings, "language", "Echo", false);
        enterprise.ApplySettingsPolicy(enterpriseSettings);
        Check(enterpriseSettings.EnabledProvidersByType["language"].Length == 0 && enterpriseSettings.Ai.RemoteUrl == enterprise.AiUrl, "enterprise endpoint policy never reapplies provider defaults");

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => SettingsStore.SaveAsync(directory, loaded.Clone())));
        Check((await SettingsStore.LoadAsync(directory, new())).EnabledProvidersByType["language"].Length > 0, "overlapping saves produce a complete parseable settings file");
        var savedJson = await File.ReadAllTextAsync(Path.Combine(directory, "settings.json"));
        Directory.CreateDirectory(Path.Combine(directory, "settings.json.tmp"));
        try { await SettingsStore.SaveAsync(directory, new()); throw new Exception("Expected a save failure."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Check(await File.ReadAllTextAsync(Path.Combine(directory, "settings.json")) == savedJson, "failed writes preserve the previous committed preferences");
        Directory.Delete(Path.Combine(directory, "settings.json.tmp"));
        await SettingsStore.SaveAsync(directory, loaded);
        Check(true, "a failed save releases the write gate for retry");
    }
    private static ChatTarget[] Models() =>
    [
        new("openai/chat", "Chat") { ModelType = "language" },
        new("anthropic/chat", "Other chat") { ModelType = "language" },
        new("openai/image", "Image") { ModelType = "image" },
        new("google/image", "Other image") { ModelType = "image" }
    ];
}
