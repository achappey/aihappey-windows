using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using AIHappey.Vercel.Models;

internal static class PrimitiveRoutingRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        foreach (var type in new[] { "image", "video", "speech" })
        {
            ChatTarget[] candidates = [new("p/first", "First") { ModelType = type,
                ModelMetadata = JsonSerializer.SerializeToElement(new { description = "Candidate capability", providerHeaders = new { secret = "never-send" } }) },
                new("q/second", "Second") { ModelType = type }];
            var request = DecisionRouting.PrimitiveRequest("openai/decision", "Choose carefully", "The submitted prompt", candidates);
            var wire = JsonSerializer.SerializeToElement(request, PortableConversations.Json);
            var context = wire.GetProperty("state").GetProperty("conversation")[0];
            check(context.GetProperty("role").GetString() == "user"
                && context.GetProperty("parts")[0].GetProperty("text").GetString() == "The submitted prompt",
                type + " routing uses the submitted text as user evidence");
            var question = (DecisionChoiceQuestion)request.Questions["favorite"];
            check(question.Criteria.Keys.SequenceEqual(candidates.Select(m => m.Id)) && question.Instructions == "Choose carefully"
                && !wire.GetRawText().Contains("never-send"), type + " routing sends only offered candidate metadata and router instructions");
            using var response = JsonDocument.Parse("""{"answers":{"favorite":{"type":"choice","choice":"q/second"}}}""");
            check(DecisionRouting.Winner(response.RootElement, candidates.Select(c => c.Id).ToArray()) == "q/second",
                type + " routing accepts exactly one offered winner");
            using var invalid = JsonDocument.Parse("""{"answers":{"favorite":{"type":"choice","choice":"outside/selection"}}}""");
            try { DecisionRouting.Winner(invalid.RootElement, candidates.Select(c => c.Id).ToArray()); check(false, "unselected winner rejected"); }
            catch (GatewayException) { check(true, type + " routing rejects unselected winners without fallback"); }
            var selection = new ChatSelection(); selection.Replace(candidates.Select(c => c.Id));
            selection.UseManualSelection(candidates, false);
            check(selection.Ids.SequenceEqual(["p/first"]), type + " routing can reconcile to one manual candidate");
            selection.Toggle("p/first", true, false);
            check(selection.Ids.Count == 1, type + " selection retains its final checkbox");
            var store = new CatalogFavoritesStore(Path.Combine(root, "primitive-favorites-" + type));
            var key = ModelOverviewCatalog.FavoriteKey(candidates[0]);
            await store.SaveAsync("account-a", new HashSet<string> { key }, CancellationToken.None);
            check((await store.LoadAsync("account-a")).Contains(key) && !(await store.LoadAsync("account-b")).Contains(key),
                type + " favorite persistence is isolated between accounts");
        }
        var agent = new DesktopAgent(new JsonObject { ["name"] = "SelectModelAgent", ["model"] = new JsonObject { ["id"] = "openai/shared" } });
        ChatTarget language = new("openai/shared", "Language") { ModelType = "language" };
        ChatTarget decision = language with { ModelType = "decision" };
        check(DecisionRouting.Model(agent, [language, decision]) == decision.Id, "routing finds decision capability when language shares the same ID");
        check(DecisionRouting.Model(agent, [language]) is null, "routing never assumes language-only models support Decisions");
    }
}
