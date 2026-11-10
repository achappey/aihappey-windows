using AIHappey.Desktop.Core;

internal static class DefaultModelUpdatesRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var now = DateTimeOffset.Parse("2026-11-10T12:00:00Z");
        ChatTarget Model(string id, double age, string type = "language") => new(id, id)
            { ModelType = type, Created = now.AddDays(-age).ToUnixTimeSeconds() };
        var preferences = new AiModelPreferences { LanguageModel = ["old"] };
        var defaults = new Dictionary<string, string[]> { ["language"] = ["new"] };
        foreach (var (age, qualifies) in new[] { (29d, false), (30d, false), (30 + 1 / 86400d, true), (90d, true) })
            check((DefaultModelUpdates.Find(preferences, defaults, [Model("old", age), Model("new", 2)], now).Count > 0) == qualifies,
                "default update uses strict 30-day model release boundary: " + age);

        var catalog = new[] { Model("old", 60), Model("recent", 1), Model("new-1", 45), Model("new-2", 40), Model("new", 10),
            Model("same-date", 60), Model("older", 61), Model("image", 1, "image") };
        var mixed = new AiModelPreferences { LanguageModel = ["recent", "old"] };
        var ordered = new Dictionary<string, string[]> { ["language"] = [" new-2 ", "new-1", "new-2", ""] };
        check(DefaultModelUpdates.Find(mixed, ordered, catalog, now)["language"].SequenceEqual(["new-2", "new-1"])
            && mixed.LanguageModel!.SequenceEqual(["recent", "old"]) && ordered["language"][0] == " new-2 ",
            "one old saved model qualifies mixed-age lists; configured order is normalized without mutating inputs");

        foreach (var ids in new[] { new[] { "old" }, ["same-date"], ["older"], ["missing"], ["image"], ["new", "missing"], Array.Empty<string>() })
            check(DefaultModelUpdates.Find(preferences, new Dictionary<string, string[]> { ["language"] = ids }, catalog, now).Count == 0,
                "default update rejects unchanged/equal/older/missing/wrong-type/empty replacements: " + string.Join(",", ids));
        foreach (var bad in new long?[] { null, 0, -1, now.ToUnixTimeSeconds() + 1, long.MaxValue })
        {
            check(DefaultModelUpdates.Find(preferences, defaults, [Model("old", 60) with { Created = bad }, Model("new", 1)], now).Count == 0,
                "unverifiable current date cannot establish an update: " + bad);
            check(DefaultModelUpdates.Find(preferences, defaults, [Model("old", 60), Model("new", 1) with { Created = bad }], now).Count == 0,
                "unverifiable configured date cannot establish an update: " + bad);
        }
        foreach (var ids in new string[]?[] { null, [] })
            check(DefaultModelUpdates.Find(new() { LanguageModel = ids }, defaults, catalog, now).Count == 0,
                "unset and explicitly empty user defaults do not trigger updates");
        check(DefaultModelUpdates.Find(preferences, null, catalog, now).Count == 0
            && DefaultModelUpdates.Find(preferences, new Dictionary<string, string[]>(), catalog, now).Count == 0,
            "only explicitly configured defaults can trigger an update");
        preferences.LanguageModel = ["old", "new"];
        check(DefaultModelUpdates.Find(preferences, new Dictionary<string, string[]> { ["language"] = ["new", "old"] }, catalog, now).Count == 0
            && DefaultModelUpdates.Find(preferences, defaults, catalog, now).Count == 0,
            "reordering or removing saved defaults does not introduce a newer replacement");

        var settings = new DesktopSettings { AllowedToolList = ["keep-tool"] };
        settings.AiModels.TranscriptionModel = "keep-stt";
        var configured = new Dictionary<string, string[]>();
        var allModels = new List<ChatTarget>();
        foreach (var type in AiModelPreferences.MultiModelTypes)
        {
            settings.AiModels.SetDefaults(type, [type + "-old"]);
            configured[type] = [type + "-new"];
            allModels.Add(Model(type + "-old", type == "image" ? 10 : 60, type));
            allModels.Add(Model(type + "-new", 1, type));
        }
        configured.Remove("video");
        var updates = DefaultModelUpdates.Find(settings.AiModels, configured, allModels, now);
        check(updates.Keys.SequenceEqual(["language", "speech"]), "only qualifying explicitly configured model types are offered");
        var next = settings.Clone();
        foreach (var (type, ids) in updates) next.AiModels.SetDefaults(type, ids);
        await SettingsStore.SaveAsync(root, next);
        var restarted = await SettingsStore.LoadAsync(root, new());
        check(restarted.AiModels.LanguageModel!.SequenceEqual(["language-new"]) && restarted.AiModels.SpeechModel!.SequenceEqual(["speech-new"])
            && restarted.AiModels.ImageModel!.SequenceEqual(["image-old"]) && restarted.AiModels.VideoModel!.SequenceEqual(["video-old"])
            && restarted.AiModels.TranscriptionModel == "keep-stt" && restarted.AllowedToolList.SequenceEqual(["keep-tool"])
            && settings.AiModels.LanguageModel!.SequenceEqual(["language-old"]),
            "selective default application survives real settings persistence and preserves unrelated types/settings");
        check(DefaultModelUpdates.Find(restarted.AiModels, configured, allModels, now).Count == 0,
            "successfully applied lists do not trigger another warning after restart");
    }
}
