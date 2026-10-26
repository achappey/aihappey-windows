using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
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
        CheckIcons(check);
    }

    private static void CheckIcons(Action<bool, string> check)
    {
        var original = DesktopAgent.Parse("""
            {"name":"Artwork","description":"Fixture","instructions":"Help","model":{"id":"openai/model"},"tags":["Keep"],"future":7,
             "icons":[{"src":"https://example.com/old.svg","theme":"dark","mimeType":"image/svg+xml","sizes":["any"],"future":true}]}
            """);
        var before = original.Definition.ToJsonString(); var draft = original.Clone();
        var icon = (JsonObject)((JsonArray)draft.Definition["icons"]!)[0]!;
        icon["src"] = " https://example.com/edited.svg "; DesktopAgentIcons.SetTheme(icon, "");
        check(!icon.ContainsKey("theme") && DesktopAgent.Text(icon["mimeType"]) == "image/svg+xml"
            && icon["sizes"] is JsonArray && DesktopAgent.Boolean(icon["future"]), "agent icon editing omits an unset theme and preserves all other icon metadata");
        check(!DesktopAgentIcons.Add(draft, " ", "dark") && DesktopAgentIcons.Add(draft, " https://example.com/light.svg ", "light")
            && DesktopAgentIcons.Add(draft, "https://example.com/dark.svg", "dark"), "agent icon add rejects blank rows, trims URLs, and stores lowercase themes");
        DesktopAgentIcons.PrepareForSave(draft);
        var restored = DesktopAgent.Parse(draft.Definition.ToJsonString());
        check(DesktopAgent.Text(restored.Definition["icons"]?[0]?["src"]) == "https://example.com/edited.svg"
            && restored.Definition["future"]?.ToJsonString() == "7" && JsonNode.DeepEquals(restored.Definition["tags"], original.Definition["tags"])
            && original.Definition.ToJsonString() == before, "agent icon save/export/reload preserves unrelated fields and cancellation never mutates the original");
        var artwork = DesktopAgentIcons.Artwork(restored.CatalogItem());
        check(ProviderCatalog.IconSources(artwork, true).SequenceEqual(["https://example.com/dark.svg", "https://example.com/edited.svg", "https://example.com/light.svg"])
            && ProviderCatalog.IconSources(artwork, false).First() == "https://example.com/light.svg", "agent custom icons precede provider icons and use matching, neutral, then other-theme order without favicon requests");
        var onlyDark = artwork with { Icons = [new("dark.svg", "dark"), new("neutral.svg", null)] };
        check(ProviderCatalog.IconSources(onlyDark, false).First() == "neutral.svg"
            && ProviderCatalog.IconSources(onlyDark with { Icons = [new("dark.svg", "dark")] }, false).Single() == "dark.svg", "agent artwork falls back to neutral then another custom theme");
        foreach (var entry in ((JsonArray)draft.Definition["icons"]!).OfType<JsonObject>()) entry["src"] = " ";
        DesktopAgentIcons.PrepareForSave(draft);
        check(!draft.Definition.ContainsKey("icons") && DesktopAgentIcons.Artwork(draft.CatalogItem()).Icons.SequenceEqual(ProviderCatalog.Get("openai").Icons),
            "removing all agent URLs restores provider catalog artwork");
        using var collection = JsonDocument.Parse("""{"id":"backend/agent","agent":{"model":{"id":"retired/old"},"models":[{"id":"unknown/model"},{"id":"OpenAI/org/model"}]}}""");
        var remote = CatalogProjection.Agent(collection.RootElement)!;
        var remoteBefore = remote.Definition!.Value.GetRawText();
        check(DesktopAgentIcons.ModelIds(remote).SequenceEqual(["unknown/model", "OpenAI/org/model"])
            && DesktopAgentIcons.Artwork(remote).Icons.SequenceEqual(ProviderCatalog.Get("openai").Icons)
            && remote.Definition.Value.GetRawText() == remoteBefore, "backend collection-only/mixed-provider artwork resolves known model prefixes without rewriting model IDs");
        check(DesktopAgentIcons.Artwork(new(CatalogKind.Agent, "plain", "Plain", "") { Model = "unknown/model" }).Icons.Count == 0
            && DesktopAgentIcons.Artwork(new(CatalogKind.Agent, "plain", "Plain", "") { Model = "openai" }).Icons.Count == 0,
            "unknown and unprefixed models retain generic agent artwork");
        foreach (var language in new[] { "en", "nl" })
        {
            using var stream = typeof(AgentOverviewRegressionTests).Assembly.GetManifestResourceStream("Desktop.Resources." + language)!;
            var text = XDocument.Load(stream).Root!.Elements("data").ToDictionary(element => (string)element.Attribute("name")!, element => element.Element("value")!.Value);
            check(new[] { "AgentIcons", "AgentIconUrl", "AgentIconTheme", "AgentIconThemeAny", "AgentIconThemeLight", "AgentIconThemeDark", "AgentIconAdd", "AgentIconRemove" }
                .All(key => text.TryGetValue(key, out var value) && value.Length > 0), "agent icon labels localized in " + language);
            check(text["LocalPluginImages"] == (language == "en" ? "Images" : "Afbeeldingen"), "Images plugin display label localized without local prefix in " + language);
        }
    }
}
