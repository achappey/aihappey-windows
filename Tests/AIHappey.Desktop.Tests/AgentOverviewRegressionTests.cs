using System.Text.Json;
using AIHappey.Desktop.Core;

internal static class AgentOverviewRegressionTests
{
    public static void Run(Action<bool, string> check)
    {
        using var definition = JsonDocument.Parse("""{"tags":[" shared ","research","",3],"owned_by":"Nested"}""");
        var local = AgentOverviewProjection.Normalize(new(CatalogKind.Agent, "same", "Same", "Local")
        { Origin = CatalogOrigin.Local, Tags = [" writing ", "shared", "shared", " "], Owner = " Team " });
        var remote = AgentOverviewProjection.Normalize(new(CatalogKind.Agent, "same", "Same", "Remote")
        { Tags = ["shared"], Owner = "Team", Definition = definition.RootElement.Clone() });
        var other = AgentOverviewProjection.Normalize(new(CatalogKind.Agent, "other", "Other", "Other") { Tags = ["research"], Owner = "Other team" });
        var blank = AgentOverviewProjection.Normalize(new(CatalogKind.Agent, "plain", "Plain", "No metadata") { Tags = [" "], Owner = " " });
        var fallback = AgentOverviewProjection.Normalize(new(CatalogKind.Agent, "fallback", "Fallback", "Embedded metadata")
        { Owner = " ", Definition = definition.RootElement.Clone() });
        CatalogItem[] items = [local, remote, other, blank];
        HashSet<string> tags = [], owners = [];
        check(local.Tags.SequenceEqual(["writing", "shared"]) && remote.Tags.SequenceEqual(["shared", "research"])
            && local.Owner == "Team" && remote.Owner == "Team" && fallback.Owner == "Nested", "agent overview normalizes tags and effective local/remote ownership");
        check(AgentOverviewProjection.Search(items, "Team").Count == 3 && AgentOverviewProjection.Search(items, "writing").Single().Key == local.Key,
            "agent overview search includes effective owner and normalized tags");
        check(AgentOverviewProjection.Search(items, "Nested").Count == 0, "agent overview never searches overridden nested ownership");
        tags.UnionWith(["writing", "research"]); owners.Add("Team");
        var filtered = items.Where(item => AgentOverviewProjection.Matches(item, tags, owners)).ToArray();
        check(filtered.Length == 2 && filtered.Select(item => item.Key).Distinct().Count() == 2, "agent overview ORs tags, ANDs ownership and preserves same-name local/backend identities");
        owners.Add("Other team");
        check(items.Count(item => AgentOverviewProjection.Matches(item, tags, owners)) == 3, "agent overview owners support multi-select OR semantics");
        check(items.Count(item => AgentOverviewProjection.MatchesTab(item, "tag:shared")) == 2
            && items.Count(item => AgentOverviewProjection.MatchesTab(item, "tag:writing")) == 1
            && items.Count(item => AgentOverviewProjection.MatchesTab(item, "tag:research")) == 2,
            "agent per-value tag tabs include every matching agent without duplicate memberships");
        check(items.Count(item => AgentOverviewProjection.MatchesTab(item, "owner:Team")) == 2,
            "agent per-value owner tabs include tagged agents and exclude missing owners");
        check(items.Where(item => AgentOverviewProjection.Matches(item, tags, owners)).Count(item => AgentOverviewProjection.MatchesTab(item, "tag:shared")) == 2,
            "agent per-value tabs compose with shared filters");
        check(AgentOverviewProjection.TabKey(true, "all") == "tag:all" && AgentOverviewProjection.TabKey(false, "all") == "owner:all"
            && AgentOverviewProjection.TabKey(true, "owner:x") != AgentOverviewProjection.TabKey(false, "tag:x"),
            "agent tag, owner and built-in tabs have collision-free identities independent of labels");
        check(blank.Tags.Count == 0 && blank.Owner == "" && !AgentOverviewProjection.MatchesTab(blank, "tag:shared")
            && !AgentOverviewProjection.MatchesTab(blank, "owner:Team"), "blank metadata creates no phantom filter options or tab memberships");
    }
}
