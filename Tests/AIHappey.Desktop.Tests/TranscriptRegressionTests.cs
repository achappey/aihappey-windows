using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class TranscriptRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        Dictionary<string, object> Metadata(string json) => JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
        ChatMessagePresentation Present(string? model, bool enabled = true, string? providerKey = null,
            Role role = Role.assistant, ServiceKind service = ServiceKind.Ai, string fallback = "openai/fallback")
            => ChatMessagePresentation.Resolve(new UIMessage { Role = role, Metadata = new()
                { ["model"] = model!, ["providerKey"] = providerKey! } }, service, fallback, enabled);
        check(Present("openai/gpt-chat") is { Model: "gpt-chat", Provider.Id: "openai" }, "assistant header: catalog logo replaces matching provider prefix");
        check(Present("openai/gpt-chat", false) is { Model: "openai/gpt-chat", Provider: null }, "assistant header: disabling logos restores the full label");
        check(Present("openrouter/anthropic/claude") is { Model: "anthropic/claude", Provider.Id: "openrouter" }, "assistant header: strip only the outer provider, preserve nested model path");
        check(Present("unknown-provider/model") is { Model: "unknown-provider/model", Provider: null }, "assistant header: unknown provider preserves full label");
        check(Present("gpt-chat", providerKey: " OPENAI ") is { Model: "gpt-chat", Provider.Id: "openai" }, "assistant header: explicit provider metadata supports raw model IDs");
        check(Present("anthropic/model", providerKey: "openai") is { Model: "anthropic/model", Provider.Id: "openai" }, "assistant header: explicit identity wins but a mismatched prefix is never removed");
        check(Present("openai/model", providerKey: "unknown") is { Model: "openai/model", Provider: null }, "assistant header: unknown explicit identity never guesses another provider");
        check(Present("openai/") is { Model: "openai/" }, "assistant header: a provider-only prefix never produces an empty label");
        check(Present("raw-model") is { Model: "raw-model", Provider: null }, "assistant header: raw model without identity does not inherit current provider");
        check(Present(null) is { Model: "fallback", Provider.Id: "openai" } && Present(" ") is { Model: "fallback" }, "assistant header: missing model uses conversation target for streaming and old history");
        foreach (var role in new[] { Role.user, Role.system })
            check(Present("openai/model", role: role) is { Model: "openai/model", Provider: null }, "assistant header: user/system never get logos or shortened labels");
        check(Present("openai/agent", service: ServiceKind.Agents) is { Model: "openai/agent", Provider: null }, "assistant header: agent path is not treated as a provider prefix");
        var agents = DesktopAgentTargets.Project([
            new CatalogItem(CatalogKind.Agent, "backend-id", "OpenAIAgent", "") { Model = "openai/gpt-fixture" },
            new CatalogItem(CatalogKind.Agent, "LocalAgent", "LocalAgent", "") { Origin = CatalogOrigin.Local, Model = "anthropic/claude" }]);
        ChatMessagePresentation AgentPresent(string json, bool enabled = true, IReadOnlyList<ChatTarget>? catalog = null)
            => ChatMessagePresentation.Resolve(new UIMessage { Role = Role.assistant, Metadata = Metadata(json) },
                ServiceKind.Agents, "remote:backend-id", enabled, catalog);
        check(AgentPresent("{}", catalog: agents) is { Model: "OpenAIAgent", Provider.Id: "openai" }
            && AgentPresent("""{"model":"remote:backend-id"}""", catalog: agents) is { Model: "OpenAIAgent", Provider.Id: "openai" },
            "agent headers: streaming/missing metadata resolve catalog names and known model identity");
        check(AgentPresent("""{"model":"local:LocalAgent"}""", catalog: agents) is { Model: "LocalAgent", Provider.Id: "anthropic" },
            "agent headers: local agent author omits routing prefix and picker-only local qualifier");
        check(AgentPresent("""{"model":"remote:OpenAIAgent"}""") is { Model: "OpenAIAgent", Provider: null }
            && AgentPresent("""{"model":"remote:OpenAIAgent"}""", false) is { Model: "OpenAIAgent", Provider: null },
            "agent headers: interrupted legacy history hides routing prefix even without a catalog or logos");
        check(AgentPresent("""{"model":"OpenAIAgent","agentModel":"openai/gpt-fixture"}""") is { Model: "OpenAIAgent", Provider.Id: "openai" }
            && AgentPresent("""{"model":"OpenAIAgent","agentModel":"openai/gpt-fixture"}""", false) is { Model: "OpenAIAgent", Provider: null },
            "agent headers: persisted underlying model supplies an optional logo but never replaces the agent name");
        check(AgentPresent("""{"model":"openai/Agent","providerKey":" OPENAI "}""") is { Model: "openai/Agent", Provider.Id: "openai" },
            "agent headers: explicit provider identity leaves provider-like agent names intact");
        check(AgentPresent("""{"model":"OpenAIAgent","agentModel":"openai/gpt-fixture","providerKey":"unknown"}""") is { Model: "OpenAIAgent", Provider: null }
            && AgentPresent("""{"model":"OpenAIAgent","agentModel":"unknown/model"}""", catalog: agents) is { Model: "OpenAIAgent", Provider: null },
            "agent headers: unknown message identity never borrows a known catalog provider");
        var agentOutput = new ConversationMessage { Message = new UIMessage { Role = Role.assistant,
            Metadata = Metadata("""{"model":"OpenAIAgent","agentModel":"openai/gpt-fixture","providerKey":"openai"}""") } };
        new MessageAssembler(agentOutput).Apply(new StreamEvent("finish", JsonSerializer.SerializeToElement(new
            { type = "finish", messageMetadata = new { model = "FinalAgentName" } }), null));
        var agentHistory = new Conversation { Service = ServiceKind.Agents, Target = "remote:backend-id", Messages = [agentOutput] };
        var agentJson = PortableConversations.Write(agentHistory);
        check(ChatMessagePresentation.Resolve(agentOutput.Message, ServiceKind.Agents, agentHistory.Target, true, agents)
            is { Model: "FinalAgentName", Provider.Id: "openai" },
            "agent headers: server completion label wins while the turn's provider snapshot survives metadata merging");
        check(PortableConversations.Write(agentHistory) == agentJson && agentHistory.Target == "remote:backend-id",
            "agent header presentation leaves portable metadata and stored routing identity unchanged");
        var historical = new Conversation { Target = "anthropic/current", Messages = [new() { Message = new UIMessage
            { Role = Role.assistant, Metadata = Metadata("""{"model":"openai/historical"}""") } }] };
        var beforePresentation = PortableConversations.Write(historical);
        check(ChatMessagePresentation.Resolve(historical.Messages[0].Message, ServiceKind.Ai, historical.Target, true) is { Model: "historical", Provider.Id: "openai" },
            "assistant header: each historical message retains its own provider rather than current target");
        ChatMessagePresentation.Resolve(historical.Messages[0].Message, ServiceKind.Ai, historical.Target, false);
        check(PortableConversations.Write(historical) == beforePresentation, "assistant header: presentation never rewrites history metadata");
        var timestamp = DateTimeOffset.UtcNow;
        foreach (var (value, expected) in new[] { (0d, "0.00"), (.001d, "0.00>"), (.0049d, "0.00>"), (.005d, "0.01"), (.0123d, "0.01"), (1.005d, "1.01"), (42.125d, "42.13") })
            check(MessageUsage.FormatCost(value) == expected, $"price matches browser formatting for {value}");
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            check(MessageUsage.FormatCost(value) is null, "non-finite prices omitted");
        foreach (var tokens in new[] { 0, -1, 17 })
        {
            var usage = MessageUsage.Read(Metadata("{\"usage\":{\"totalTokens\":" + tokens + "},\"providerMetadata\":{\"gateway\":{\"cost\":0.001}}}"), "test/model", timestamp);
            check(usage.Tokens == (tokens > 0 ? "17" : null) && usage.Price == "0.00>", "zero/negative tokens omitted independently of cost: " + tokens);
        }
        check(MessageUsage.Read(Metadata("""{"totalTokens":17,"gateway":{"cost":0}}"""), "test/model", timestamp) == ("17", "0.00"), "legacy metadata keeps tokens and zero cost");
        check(MessageUsage.Read(Metadata("{}"), "test/model", timestamp) == (null, null), "missing metadata omits both usage badges");
        var activityConversation = new List<ConversationMessage>
        {
            new() { Timestamp = timestamp.AddMinutes(-2), Message = new UIMessage { Id = "first", Role = Role.assistant, Parts =
            [
                PortableConversations.Part(JsonDocument.Parse("""{"type":"tool-first","toolCallId":"one","input":{"value":1}}""").RootElement),
                PortableConversations.Part(JsonDocument.Parse("""{"type":"data-first","id":"one","data":{"value":1}}""").RootElement)
            ] } },
            new() { Timestamp = timestamp, Message = new UIMessage { Id = "second", Role = Role.assistant, Parts =
            [
                PortableConversations.Part(JsonDocument.Parse("""{"type":"tool-second","toolCallId":"two","state":"output-available","input":{"value":2},"output":{"value":2}}""").RootElement),
                PortableConversations.Part(JsonDocument.Parse("""{"type":"data-second","id":"two","data":{"value":2}}""").RootElement)
            ] } }
        };
        var beforeActivityProjection = PortableConversations.Write(new Conversation { Messages = activityConversation });
        var projectedActivity = ConversationActivityProjection.Project(activityConversation);
        check(projectedActivity.Tools.Select(item => item.Part.Type).SequenceEqual(["tool-second", "tool-first"])
            && projectedActivity.Data.Select(item => item.Part.Type).SequenceEqual(["data-second", "data-first"]),
            "conversation activity projects tools and data newest-first");
        check(PortableConversations.Write(new Conversation { Messages = activityConversation }) == beforeActivityProjection,
            "conversation activity projection does not mutate portable history");
        foreach (var gateway in new[] { "null", "[]", "\"invalid\"", "{}", "{\"cost\":null}", "{\"cost\":\"0.12\"}", "{\"cost\":{}}", "{\"cost\":1e999}" })
            check(MessageUsage.Read(Metadata("{\"usage\":{\"totalTokens\":17},\"providerMetadata\":{\"gateway\":" + gateway + "}}"), "test/model", timestamp) == ("17", null), "invalid/missing cost preserves positive tokens: " + gateway);
        check(MessageUsage.Read(Metadata("""{"usage":"invalid"}"""), "test/model", timestamp) == (null, null), "malformed optional usage does not break rendering");
        var now = new DateTimeOffset(2026, 10, 8, 14, 0, 0, TimeSpan.Zero);
        foreach (var language in new[] { "en", "nl" })
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Desktop.Resources." + language)!;
            var values = XDocument.Load(stream).Root!.Elements("data")
                .ToDictionary(data => data.Attribute("name")!.Value, data => data.Element("value")!.Value);
            string Format(TimeSpan elapsed) => RelativeTime.Format(now - elapsed, now, key => values[key]);
            foreach (var (elapsed, key, count) in new[]
            {
                (TimeSpan.FromSeconds(-30), "TimeJustNow", 0),
                (TimeSpan.FromSeconds(59), "TimeJustNow", 0),
                (TimeSpan.FromMinutes(1), "TimeMinuteAgo", 1),
                (TimeSpan.FromMinutes(2), "TimeMinutesAgo", 2),
                (TimeSpan.FromMinutes(59), "TimeMinutesAgo", 59),
                (TimeSpan.FromHours(1), "TimeHourAgo", 1),
                (TimeSpan.FromHours(2), "TimeHoursAgo", 2),
                (TimeSpan.FromDays(1), "TimeDayAgo", 1),
                (TimeSpan.FromDays(2), "TimeDaysAgo", 2),
                (TimeSpan.FromDays(7), "TimeWeekAgo", 1),
                (TimeSpan.FromDays(14), "TimeWeeksAgo", 2),
                (TimeSpan.FromDays(30), "TimeMonthAgo", 1),
                (TimeSpan.FromDays(60), "TimeMonthsAgo", 2),
                (TimeSpan.FromDays(365), "TimeYearAgo", 1),
                (TimeSpan.FromDays(730), "TimeYearsAgo", 2)
            }) check(Format(elapsed) == string.Format(values[key], count), $"{language}: relative time {elapsed} selects translated singular/plural unit");
            check(Format(TimeSpan.FromMinutes(2)) == (language == "en" ? "2 minutes ago" : "2 minuten geleden"), language + ": relative time uses real translations");
            check(values["GeneratedByAi"].Length > 0 && values["GeneratedByAiWarning"].Length > 0, language + ": AI disclosure translations present");
            foreach (var key in new[] { "SettingsChat.Text", "SettingsChatTab.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
                "SettingsShowTokens.Header", "SettingsShowTokens.OnContent", "SettingsShowTokens.OffContent",
                "SettingsShowTokens.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name", "SettingsShowProviderLogo.Header",
                "SettingsShowProviderLogo.OnContent", "SettingsShowProviderLogo.OffContent",
                "SettingsShowProviderLogo.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
                "SettingsApiDebug.Header", "SettingsApiDebug.OnContent", "SettingsApiDebug.OffContent",
                "SettingsApiDebug.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name" })
                check(values.TryGetValue(key, out var text) && text.Length > 0, language + ": chat display setting is translated: " + key);
            check(values["MessagePrice"].Contains("{0}"), language + ": accessible price badge label translated");
            check(values["MessagePriceWarning"] == (language == "en" ? "Indicative. Actual provider charges may vary."
                : "Indicatief. Werkelijke kosten kunnen afwijken."), language + ": pricing warning matches browser");
            foreach (var key in new[] { "Activities", "ToggleActivities", "ToolsCount", "DataCount", "NoToolActivities", "NoDataActivities", "ExplainToolCall", "ToolExplanationFallback", "ToolOutputTitle" })
                check(values.TryGetValue(key, out var translated) && translated.Length > 0, language + ": conversation activity resource present: " + key);
        }
    }
}
