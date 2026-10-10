using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class ChatSettingsRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        const string legacyInstructions = "Legacy custom instructions must not be sent";
        var directory = Path.Combine(root, "legacy-chat-settings");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(path, """
            {"chat":{"systemInstructions":"Legacy custom instructions must not be sent",
              "maxOutputTokens":123,"enabledSkillIds":["provider/sample"],"activePlugins":["files"],
              "providerMetadata":{"custom":{"futureOption":true}}}}
            """);
        var loaded = await SettingsStore.LoadAsync(directory, new());
        check(loaded.Chat.MaxOutputTokens == 123 && loaded.Chat.EnabledSkillIds.SequenceEqual(["provider/sample"])
            && loaded.Chat.ActivePlugins.SequenceEqual(["files"]), "chat settings: legacy instruction option does not prevent loading other preferences");
        check(!JsonSerializer.Serialize(loaded.Chat.Clone(), JsonSerializerOptions.Web).Contains("systemInstructions"),
            "chat settings: removed instructions do not survive preference cloning");
        await SettingsStore.SaveAsync(directory, loaded);
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        check(persisted["chat"]!.AsObject().ContainsKey("systemInstructions") == false
            && persisted["chat"]!["providerMetadata"]!["custom"]!["futureOption"]!.GetValue<bool>(),
            "chat settings: saving drops legacy instructions without discarding native provider options");

        var mcp = McpTurnSnapshot.Empty;
        mcp.AddContext(JsonSerializer.SerializeToElement(new { availableSkills = new { skills = new[] { new { skill_id = "provider/sample" } } } }));
        var options = new DesktopContextOptions { AppName = "Fixture app", ChatbotInstructions = "Distributor instructions\\nRemain supported" };
        var information = new JsonObject { ["platform"] = "Fixture platform" };
        var session = new DesktopSession(new TestHost(), new TestRuntime(), loaded) { ContextOptions = options, ActiveLanguage = "nl" };
        var system = session.CaptureSystemContext(true, information, mcp);
        var parts = system.Parts.Select(p => JsonNode.Parse(PortableConversations.Text(p))!).ToArray();
        check(system.Role == Role.system && parts.Length == 4 && !JsonSerializer.Serialize(system, PortableConversations.Json).Contains(legacyInstructions),
            "chat settings: captured system context has no legacy custom-instruction part");
        check(parts[0]["availableSkills"]!["skills"]![0]!["skill_id"]!.GetValue<string>() == "provider/sample"
            && parts[1]["chatBotInstructions"]!.GetValue<string>() == "Distributor instructions\nRemain supported",
            "chat settings: skill catalog and distributor-managed app instructions remain in context");
        check(parts[2]["systemInformation"]!["appName"]!.GetValue<string>() == "Fixture app"
            && parts[2]["systemInformation"]!["platform"]!.GetValue<string>() == "Fixture platform"
            && !information.ContainsKey("appName") && parts[3]["preferredLanguage"]!.GetValue<string>() == "nl"
            && parts[3]["darkMode"]!.GetValue<bool>(), "chat settings: system information, language and appearance remain unchanged");
        var identity = new DesktopSystemContextComposer().Compose(new(options, information,
            new("user", "User name", "user-id", "tenant-id"), "en", false, DateTimeOffset.UtcNow));
        var userContext = JsonNode.Parse(PortableConversations.Text(identity.Parts.Last()))!;
        check(userContext["username"]!.GetValue<string>() == "user" && userContext["name"]!.GetValue<string>() == "User name"
            && userContext["id"]!.GetValue<string>() == "user-id" && userContext["tenantId"]!.GetValue<string>() == "tenant-id",
            "chat settings: safe host identity fields remain in context");

        var user = new UIMessage { Id = "user", Role = Role.user, Parts = [new TextUIPart { Text = "Hello" }] };
        var stale = new UIMessage { Role = Role.system, Parts = [new TextUIPart { Text = legacyInstructions }] };
        var messages = DesktopSystemContext.RequestMessages(ServiceKind.Ai, [stale, user], system);
        check(messages.Count == 2 && ReferenceEquals(messages[0], system) && ReferenceEquals(messages[1], user),
            "chat settings: stale imported instructions are replaced by the current system context");
        check(DesktopSystemContext.RequestMessages(ServiceKind.Agents, [stale, user], system).SequenceEqual([user]),
            "chat settings: agent requests still omit app system context");
        using var handler = new TestHandler(); using var http = new HttpClient(handler);
        var client = new DesktopChatClient(session, http);
        await foreach (var _ in client.StreamAsync(ServiceKind.Ai, "custom/model", "chat", [stale, user], default)) { }
        check(handler.LastBody is not null && !handler.LastBody.Contains(legacyInstructions)
            && handler.LastBody.Contains("chatBotInstructions") && handler.LastBody.Contains("Distributor instructions"),
            "chat settings: the actual request path excludes legacy instructions and preserves app instructions");
    }
}
