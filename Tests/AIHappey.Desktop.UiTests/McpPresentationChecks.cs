using System.Reflection;
using System.Text.Json;
using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckMcpPresentationAsync(ElementTheme theme)
    {
        var root = new Grid { RequestedTheme = theme };
        window!.Content = root; window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 800)); window.Activate();
        await Task.Delay(100);
        var context = "MCP presentation / " + theme;
        var reads = 0;
        var entry = new McpResourceEntry("server", "Example server", "https://mcp.example/", Json("""
            {"title":"Resource title","name":"resource","description":"Resource description","uri":"resource://example/secret","mimeType":"text/plain"}
            """), false, (_, _, _, _) => { reads++; return Task.FromResult(Json("""{"contents":[{"uri":"resource://example/secret","text":"Resource content","mimeType":"text/plain"}]}""")); }, null);
        var template = entry with { IsTemplate = true, Resource = Json("""
            {"title":"Template title","uriTemplate":"resource://example/{name}","description":"Template description","mimeType":"text/plain"}
            """) };
        var picker = new McpResourcesDialog([entry, template]) { XamlRoot = root.XamlRoot };
        var pickerTask = picker.ShowAsync();
        await Task.Delay(200); picker.UpdateLayout();
        var badges = Descendants(picker).OfType<Border>().Where(border => border.Name == "McpResourceMetadataBadge").ToArray();
        Check(badges.Length == 6 && badges.All(badge => badge.Child is TextBlock { FontSize: 12 })
            && Descendants(picker).OfType<TextBlock>().Any(text => text.Text == entry.Name)
            && Descendants(picker).OfType<TextBlock>().Any(text => text.Text == entry.Description), context + ": picker titles, separate metadata badges and descriptions");
        Check(!Descendants(picker).OfType<TextBlock>().Any(text => text.Text == entry.Uri || text.Text == template.Uri)
            && reads == 0, context + ": resource picker hides URIs without reading content");
        typeof(McpResourcesDialog).GetMethod("ShowArguments", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, [template]);
        await Task.Delay(100);
        var fields = (StackPanel)typeof(McpResourcesDialog).GetField("fields", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!;
        Check(Descendants(fields).OfType<Border>().Count(border => border.Name == "McpResourceMetadataBadge") == 3
            && Descendants(fields).OfType<TextBox>().Single().Header?.ToString() == "name"
            && !Descendants(fields).OfType<TextBlock>().Any(text => text.Text == template.Uri), context + ": template metadata and arguments without displayed URI");
        var arguments = (TextBox)Descendants(fields).OfType<TextBox>().Single();
        Check(arguments.Focus(FocusState.Keyboard), context + ": template argument is keyboard focusable");
        picker.Hide(); await pickerTask;

        var minimal = new McpResourceView { DataContext = entry with { Resource = Json("""{"name":"Minimal","uri":"resource://minimal"}""") } };
        root.Children.Add(minimal); await Task.Delay(80);
        Check(Descendants(minimal).OfType<Border>().Count(border => border.Name == "McpResourceMetadataBadge") == 2,
            context + ": missing MIME metadata omits its badge");
        minimal.DataContext = entry;
        Check(Descendants(minimal).OfType<Border>().Count(border => border.Name == "McpResourceMetadataBadge") == 3,
            context + ": recycled resource row refreshes metadata");
        root.Children.Clear();

        // Use native embedded images only; no fixture can make a network request.
        var iconType = typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.McpServerIcon")!;
        var svg = new McpIcon("data:image/svg+xml," + Uri.EscapeDataString("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"32\" height=\"32\"><rect width=\"32\" height=\"32\" fill=\"green\"/></svg>"));
        var icon = (UserControl)Activator.CreateInstance(iconType, new object[] { new McpIcon[] { svg }, 32d })!;
        root.Children.Add(icon); await Task.Delay(80);
        Check(!Descendants(icon).OfType<Image>().Any() && Descendants(icon).OfType<FontIcon>().Single().Visibility == Visibility.Visible,
            context + ": disabled-image test switch keeps generic MCP artwork");
        AppContext.SetSwitch("AIHappey.Desktop.DisableRemoteImages", false);
        try
        {
            root.Children.Clear(); root.Children.Add(icon); await Task.Delay(150);
            var image = Descendants(icon).OfType<Image>().Single();
            Check(image.Source is SvgImageSource && image.Visibility == Visibility.Visible && image.Stretch == Stretch.Uniform
                && Descendants(icon).OfType<FontIcon>().Single().Visibility == Visibility.Collapsed, context + ": embedded SVG replaces fallback with aspect ratio preserved");
            root.RequestedTheme = theme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
            await Task.Delay(150);
            Check(Descendants(icon).OfType<Image>().Single().Visibility == Visibility.Visible, context + ": native image survives runtime theme switch");
            root.Children.Clear();
            var broken = (UserControl)Activator.CreateInstance(iconType, new object[] { new McpIcon[] { new("data:image/png;base64,not-base64") }, 16d })!;
            root.Children.Add(broken); await Task.Delay(100);
            Check(Descendants(broken).OfType<FontIcon>().Single().Visibility == Visibility.Visible
                && Descendants(broken).OfType<Image>().Single().Visibility == Visibility.Collapsed, context + ": invalid image retains generic artwork");
        }
        finally { AppContext.SetSwitch("AIHappey.Desktop.DisableRemoteImages", true); root.Children.Clear(); root.RequestedTheme = theme; }

        var approval = new ToolApprovalDialog(new("approval", "call", "example", "Example tool", Json("""
            {"input":{"query":"Example input"},"output":{"structuredContent":{"answer":42},"content":[{"type":"text","text":"Example output"}]}}
            """))) { XamlRoot = root.XamlRoot };
        var approvalTask = approval.ShowAsync(); await Task.Delay(200);
        var navigation = Descendants(approval).OfType<NavigationView>().Single(view => view.Name == "ToolApprovalTabsNavigation");
        Check(navigation.PaneDisplayMode == NavigationViewPaneDisplayMode.Top
            && ((NavigationViewItem)navigation.SelectedItem).Name == "ToolApprovalOutput"
            && !Descendants(approval).OfType<TabView>().Any(), context + ": native top-navigation approval tabs initially show Output");
        var sections = Descendants(navigation).OfType<NavigationView>().Single(view => view.Name == "ToolApprovalOutputTabsNavigation");
        Check(sections.MenuItems.Count == 3 && sections.PaneDisplayMode == NavigationViewPaneDisplayMode.Top,
            context + ": structured, content and raw output use consistent native navigation");
        navigation.SelectedItem = navigation.MenuItems[0]; await Task.Delay(80);
        Check(Descendants(navigation).OfType<TextBlock>().Any(text => text.Text.Contains("Example input")), context + ": selecting Input shows read-only tool input");
        var inputTab = (NavigationViewItem)navigation.MenuItems[0];
        Check(inputTab.Focus(FocusState.Keyboard) && new NavigationViewItemAutomationPeer(inputTab).GetName() == "Input",
            context + ": review tabs expose native keyboard focus and accessible names");
        navigation.SelectedItem = navigation.MenuItems[1]; await Task.Delay(80);
        foreach (var width in new[] { 520, 1000 })
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 800)); await Task.Delay(120); approval.UpdateLayout();
            Check(Descendants(approval).OfType<ScrollViewer>().All(view => view.ScrollableWidth < 1)
                && ((FrameworkElement)approval.Content).ActualWidth <= root.ActualWidth, context + $": review navigation fits {width}px window without horizontal scrolling");
        }
        Check(approval.PrimaryButtonText == "Allow" && approval.SecondaryButtonText == "Deny" && approval.CloseButtonText == "Stop"
            && Descendants(approval).OfType<SplitButton>().Any(button => button.Name == "AutomaticToolApproval")
            && approval.Decision is null, context + ": approval actions remain available without implicit decision");
        approval.Hide(); await approvalTask;
        File.WriteAllLines(report, results); window.Content = null;
    }

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
}
