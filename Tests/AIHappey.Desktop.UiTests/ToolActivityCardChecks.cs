using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using FluentIcons.Common;
using FluentIcons.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckToolActivityCardsAsync(ChatShell shell, string context)
    {
        var body = Field<Grid>(shell, "conversationActivityBody");
        var original = PortableConversations.Write(Field<Conversation>(shell, "current"));
        var snapshot = McpTurnSnapshot.Create([
            (new McpCatalogItem("activity-fixture", "Server", "", "https://mcp.example/", null, "fixture"),
                new McpDiscovery(Json("""{"icons":[{"src":"https://icons.example/server.png"}]}"""), Json("{}"), null,
                    [Json("""{"name":"wire-name","inputSchema":{"type":"object"},"icons":[{"src":"https://icons.example/tool.png","theme":"light"},{"src":"https://icons.example/tool-dark.png","theme":"dark"}]}""")]),
                (string name, System.Text.Json.JsonElement input, string id, string locale, CancellationToken ct) => throw new InvalidOperationException("Artwork must not execute tools"))
        ]);
        Border Card(string json)
        {
            var part = PortableConversations.Part((JsonObject)JsonNode.Parse(json)!);
            return (Border)Call(shell, "ToolActivityCard", new ConversationActivityItem(part, "activity-fixture", DateTimeOffset.UtcNow, 0, 0), snapshot)!;
        }
        var local = Card("""{"type":"dynamic-tool","toolName":"wire-name","title":"Friendly display title","toolCallId":"local","state":"output-available","input":{"query":"keep details"},"output":{"content":[{"type":"text","text":"Result fixture"}]}}""");
        var provider = Card("""{"type":"tool-wire-name","toolCallId":"provider","providerExecuted":true,"providerId":" OpenAI/model ","state":"input-available","input":{}}""");
        var unknown = Card("""{"type":"tool-wire-name","toolCallId":"unknown","providerExecuted":true,"providerId":"missing-provider","state":"output-error","errorText":"Fixture error"}""");
        var client = Card("""{"type":"tool-missing-tool","toolCallId":"client","providerExecuted":false,"providerId":"openai","state":"input-streaming","inputText":"partial"}""");
        var content = new StackPanel { Spacing = 12 };
        foreach (var card in new[] { local, provider, unknown, client }) content.Children.Add(card);
        body.Children.Clear(); body.Children.Add(new ScrollViewer { Content = content });
        try
        {
            await Task.Delay(150); shell.UpdateLayout();
            var artwork = Descendants(local).OfType<UserControl>().Single(c => c.Name == "McpServerIcon");
            var icons = Field<IReadOnlyList<McpIcon>>(artwork, "icons");
            Check(icons.Count == 2 && icons[0].Source.EndsWith("tool.png")
                && AutomationProperties.GetName(local) == "Friendly display title",
                context + ": dynamic tools resolve artwork by canonical name without losing their display title");
            Check(Descendants(provider).OfType<Grid>().Any(g => g.Name == "ProviderLogo"
                && AutomationProperties.GetName(g).Contains("OpenAI"))
                && !Descendants(provider).OfType<UserControl>().Any(c => c.Name == "McpServerIcon"),
                context + ": explicit provider-executed identity selects its logo rather than a colliding MCP name");
            Check(new[] { unknown, client }.All(c => Descendants(c).OfType<FluentIcon>().Any(i => i.Icon == Icon.Wrench)
                && !Descendants(c).OfType<Grid>().Any(g => g.Name == "ProviderLogo")),
                context + ": unknown providers and client calls without artwork keep the generic wrench");
            var output = Descendants(local).OfType<Button>().Single(b => b.Name == "ConversationToolOutput");
            Check(output.Content is FluentIcon { Icon: Icon.Info } && output.Width == 32 && output.Height == 32
                && output.Padding == new Thickness(0) && output.IsEnabled
                && AutomationProperties.GetName(output) == DesktopResources.Format("ShowToolOutput", "Friendly display title")
                && ToolTipService.GetToolTip(output) is ToolTip,
                context + ": tool output is a compact accessible info action with its existing tooltip");
            Check(!Descendants(provider).OfType<Button>().Single(b => b.Name == "ConversationToolOutput").IsEnabled
                && Descendants(unknown).OfType<Button>().Single(b => b.Name == "ConversationToolOutput").IsEnabled,
                context + ": output actions preserve input-only disabled and error-output enabled states");
            foreach (var width in new[] { 440d, 300d })
            {
                content.Width = width; shell.UpdateLayout();
                var inset = Descendants(local).OfType<StackPanel>().Single(p => p.Name == "ConversationToolContent");
                var footer = Descendants(local).OfType<Border>().Single(b => b.Name == "ConversationToolFooter");
                Check(inset.Padding == new Thickness(16) && footer.Padding == new Thickness(12, 8, 12, 8)
                    && footer.BorderThickness == new Thickness(0, 1, 0, 0) && footer.BorderBrush is SolidColorBrush
                    && footer.Child is StackPanel { Spacing: 8 }
                    && Math.Abs(footer.ActualWidth - local.ActualWidth + 2) < 2,
                    context + $": tool cards keep inset content and a full-width divided action footer at {width}px");
            }
            var originalTheme = shell.RequestedTheme;
            shell.RequestedTheme = shell.ActualTheme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
            await Task.Delay(100);
            Check(artwork.ActualTheme == shell.ActualTheme
                && McpIcons.Select(icons, shell.ActualTheme == ElementTheme.Dark ? "dark" : "light") is not null,
                context + ": activity artwork inherits live theme changes");
            shell.RequestedTheme = originalTheme;
            InvokeButton(output); await Task.Delay(150);
            var dialog = Field<ToolOutputDialog>(shell, "toolOutputDialog");
            Check(dialog.XamlRoot == shell.XamlRoot, context + ": info action still opens the existing tool output dialog");
            dialog.Hide(); await Task.Delay(80);
            Check(PortableConversations.Write(Field<Conversation>(shell, "current")) == original,
                context + ": activity card presentation and output viewing do not mutate portable history");
        }
        finally { body.Children.Clear(); Call(shell, "RefreshConversationActivity"); }
    }
}
