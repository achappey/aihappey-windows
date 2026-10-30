using System.Text.Json;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class ComposerUsageRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        ConversationMessage Message(string metadata = "{}", Role role = Role.assistant, params string[] parts) => new()
        {
            Message = new UIMessage { Role = role, Metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadata)!,
                Parts = parts.Select(p => (UIMessagePart)PortableConversations.Part(JsonSerializer.Deserialize<JsonElement>(p))).ToList() }
        };
        check(ComposerUsage.Read([]) == new ComposerUsage(null, null), "composer: empty history has no reported usage or cost");
        var first = Message("""{"usage":{"totalTokens":400},"providerMetadata":{"gateway":{"cost":0.001}}}""");
        check(ComposerUsage.Read([first]) == new ComposerUsage(400, null), "composer: a single cost remains in its footer, not duplicated as a total");
        var second = Message("""{"usage":{"totalTokens":100},"providerMetadata":{"gateway":{"cost":0.002}},"gateway":{"cost":90},"cost":80}""");
        var user = Message("""{"usage":{"totalTokens":9999},"cost":999}""", Role.user);
        var result = ComposerUsage.Read([first, second, user, Message()]);
        check(result.Tokens == 100 && Math.Abs(result.Cost!.Value - .003) < 1e-12,
            "composer: latest assistant context is not a token sum; canonical cost wins once; user and pending metadata are ignored");
        var tool = Message("""{"totalTokens":0,"gateway":{"cost":"0.004"}}""", Role.assistant,
            """{"type":"tool-fixture","toolCallId":"fixture","state":"output-available","output":{"_meta":{"gateway":{"cost":"0.005"}}}}""");
        result = ComposerUsage.Read([tool]);
        check(result.Tokens == 0 && Math.Abs(result.Cost!.Value - .009) < 1e-12,
            "composer: legacy zero usage and numeric-string tool-result costs match browser aggregation");
        var invalid = Message("""{"usage":{"totalTokens":"bad"},"gateway":{"cost":"not a number"}}""", Role.assistant,
            """{"type":"tool-fixture","toolCallId":"invalid","output":{"_meta":{"gateway":{"cost":true}}}}""");
        check(ComposerUsage.Read([first, invalid]) == new ComposerUsage(400, null), "composer: malformed optional metadata never clears valid context or creates cost entries");
        invalid.Message.Metadata!["usage"] = new Dictionary<string, object> { ["totalTokens"] = double.NaN };
        invalid.Message.Metadata["cost"] = double.PositiveInfinity;
        check(ComposerUsage.Read([first, invalid]).Tokens == 400, "composer: non-finite live values are safely ignored");
        check(ComposerUsage.Read([first, Message("""{"cost":0}""")]).Cost == .001,
            "composer: explicit zero is a valid cost entry for browser-equivalent total visibility");
        check(ComposerUsage.Read([Message("""{"cost":0}"""), Message("""{"cost":0}""")]).Cost == 0,
            "composer: zero total remains visible rather than being treated as unknown");
        var conversation = new Conversation { Messages = [first, second, tool] };
        var before = PortableConversations.Write(conversation);
        result = ComposerUsage.Read(conversation.Messages);
        check(PortableConversations.Write(conversation) == before && result.Tokens == 0,
            "composer: summary is read-only and accepts reset-to-zero usage in restored conversations");
    }
}
