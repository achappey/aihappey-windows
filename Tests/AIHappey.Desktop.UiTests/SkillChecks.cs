using System.Reflection;
using AIHappey.Desktop.Core;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckChatSkillsSettingsAsync(ElementTheme theme)
    {
        var root = new Grid { RequestedTheme = theme }; window!.Content = root;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 900)); window.Activate(); await Task.Delay(100);
        var context = "Skills / " + theme; var original = new ChatPreferences(); var prefetches = 0;
        var svg = "data:image/svg+xml," + Uri.EscapeDataString("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"32\" height=\"32\"><rect width=\"32\" height=\"32\" fill=\"green\"/></svg>");
        var items = new DesktopSkill[] { new("provider/sample", "Sample", "Remote description", "remote", "2")
                { Icons = [new(svg, "light"), new(svg, "dark")] },
            new("mcp:server:sample", "MCP sample", "Connected description", "mcp", Server: "Fixture server"),
            new("provider/other", "Other", "Other description", "remote") { Icons = [new("data:image/png;base64,not-base64")] } };
        var dialog = new ChatSettingsDialog(original, "openai", _ => Task.FromResult(new DesktopSkillSelection(items,
            new HashSet<string> { "provider/sample" }, "api.example")), (_, _) => { prefetches++; throw new IOException("offline"); })
            { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        ChatPreferences? saved = null; dialog.SaveAsync = next => { saved = next; return Task.CompletedTask; };
        SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync(); await Task.Delay(120);
        Check(Descendants(dialog).OfType<TextBox>().Any(t => t.Name == "MaxOutputTokens")
            && !Descendants(dialog).OfType<TextBox>().Any(t => t.Name == "SystemInstructions"), context + ": General retains token limit but has no custom-instructions field");
        var navigation = Field<NavigationView>(dialog, "tabs"); CheckNativeTabs(navigation, context);
        var tabs = navigation.MenuItems.OfType<NavigationViewItem>().ToArray();
        Check(tabs.Select(t => t.Name).SequenceEqual(["ChatGeneralTab", "ChatToolsTab", "ChatToolConfigurationTab", "ChatSkillsTab", "ChatPluginsTab", "ChatProviderTab"])
            && ReferenceEquals(navigation.SelectedItem, tabs[0]),
            context + ": chat tabs retain General, Tools, Skills, Plugins and provider with General selected by default");
        dialog.ShowToolsTab(); await Task.Delay(80);
        Check(ReferenceEquals(navigation.SelectedItem, tabs.Single(t => t.Name == "ChatToolsTab")),
            context + ": reordered Tools tab retains exclusive native navigation");
        dialog.ShowSkillsTab(); await Task.Delay(120); dialog.UpdateLayout();
        Check(ReferenceEquals(navigation.SelectedItem, tabs.Single(t => t.Name == "ChatSkillsTab")),
            context + ": native Skills tab selected alongside General and provider");
        var skillsView = Descendants(dialog).OfType<SkillsSettingsView>().Single();
        Check(skillsView.Children[0] is TextBox { Name: "ChatSkillSearch" }
            && skillsView.Children.OfType<TextBlock>().All(t => t.Name == "ChatSkillFeedback"),
            context + ": Skills starts with search and has no introductory text above it");
        var toggles = Descendants(dialog).OfType<ToggleSwitch>().Where(t => t.Name == "ChatSkillToggle").ToArray();
        Check(toggles.Length == 3 && toggles.All(t => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(t))), context + ": remote and MCP toggles have accessible labels");
        var rows = Descendants(dialog).OfType<SettingsExpander>().Where(e => e.Name == "ChatSkillCard").ToArray();
        Check(rows.Length == 3 && rows.All(e => !e.IsExpanded && e.Content is ToggleSwitch { Header: null, OnContent: "", OffContent: "" }),
            context + ": skills start as compact native expanders with header switches and no switch captions");
        Check(rows.All(row => row.Header is Grid { ColumnSpacing: 12 } heading
            && Descendants(heading).OfType<UserControl>().Any(icon => icon.Name == "McpServerIcon" && icon.Width == 32 && icon.Height == 32)),
            context + ": skill headers match MCP catalog logo size and title spacing");
        var sample = rows.Single(e => e.Tag as string == "provider/sample");
        var title = Descendants((DependencyObject)sample.Header).OfType<TextBlock>().Single(text => text.Text == "Sample");
        var sampleToggle = (ToggleSwitch)sample.Content;
        AppContext.SetSwitch("AIHappey.Desktop.DisableRemoteImages", false);
        try
        {
            // Re-load the header controls with only embedded, credential-free image fixtures.
            dialog.ShowToolsTab(); await Task.Delay(60); dialog.ShowSkillsTab(); await Task.Delay(120);
            var logo = Descendants((DependencyObject)sample.Header).OfType<UserControl>().Single(icon => icon.Name == "McpServerIcon");
            await WaitForSkillUiAsync(() => Descendants(logo).OfType<Image>().Any(image => image.Visibility == Visibility.Visible));
            Check(Descendants(logo).OfType<Image>().Single().Source is Microsoft.UI.Xaml.Media.Imaging.SvgImageSource
                && Descendants(logo).OfType<Image>().Single().Stretch == Microsoft.UI.Xaml.Media.Stretch.Uniform,
                context + ": an available skill logo renders through the same native SVG slot as MCP");
            Check(rows.Where(row => !ReferenceEquals(row, sample)).All(row =>
                Descendants((DependencyObject)row.Header).OfType<FluentIcons.WinUI.FluentIcon>().Single().Visibility == Visibility.Visible)
                && prefetches == 0, context + ": missing or broken skill logos retain generic artwork without fetching skill instructions");
        }
        finally { AppContext.SetSwitch("AIHappey.Desktop.DisableRemoteImages", true); }
        dialog.UpdateLayout();
        var titlePoint = title.TransformToVisual(sample).TransformPoint(new());
        var togglePoint = sampleToggle.TransformToVisual(sample).TransformPoint(new());
        Check(titlePoint.X + title.ActualWidth <= togglePoint.X
            && Math.Abs(titlePoint.Y + title.ActualHeight / 2 - togglePoint.Y - sampleToggle.ActualHeight / 2) < 2
            && Within(sampleToggle, dialog), context + ": skill title and right-side switch occupy one visible header row");
        var collapsedHeight = sample.ActualHeight;
        sample.IsExpanded = true; await Task.Delay(350); dialog.UpdateLayout();
        Check(sample.ActualHeight > collapsedHeight && Descendants(sample).OfType<TextBlock>().Any(t => t.Text == "Remote description")
            && Descendants(sample).OfType<TextBlock>().Any(t => t.Text == "2"), context + ": expanding reveals skill description and version");
        sample.IsExpanded = false; await Task.Delay(350); dialog.UpdateLayout();
        Check(!sample.IsExpanded && sample.ActualHeight <= collapsedHeight + 1,
            context + ": collapsing restores a compact skill row");
        Check(Descendants(dialog).OfType<TextBlock>().Any(t => t.Text == "Favorites (1)") && Descendants(dialog).OfType<TextBlock>().Any(t => t.Text == "api.example"),
            context + ": favorites and API source groups shown");
        toggles.Single(t => (string)t.Tag == "provider/sample").IsOn = true; await Task.Delay(60);
        Check(!sample.IsExpanded && sampleToggle.IsOn, context + ": enabling a skill leaves its details collapsed");
        Check(original.EnabledSkillIds.Count == 0 && prefetches == 1 && Descendants(dialog).OfType<TextBlock>().Single(t => t.Name == "ChatSkillFeedback").Text.Contains("first use"),
            context + ": toggle changes isolated draft, failed prefetch retains selection with feedback");
        toggles.Single(t => (string)t.Tag == "mcp:server:sample").IsOn = true;
        var search = Descendants(dialog).OfType<TextBox>().Single(t => t.Name == "ChatSkillSearch"); search.Text = "Connected"; await Task.Delay(80); dialog.UpdateLayout();
        Check(Descendants(dialog).OfType<ToggleSwitch>().Count(t => t.Name == "ChatSkillToggle") == 1, context + ": skill description search filters list without losing selections");
        var connected = Descendants(dialog).OfType<SettingsExpander>().Single(e => e.Name == "ChatSkillCard");
        connected.IsExpanded = true; await Task.Delay(80);
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(500, 900)); await Task.Delay(350); dialog.UpdateLayout();
        var contentScrollers = Descendants(dialog).OfType<ScrollViewer>().Where(s => s.HorizontalScrollMode == ScrollMode.Disabled).ToArray();
        Check(contentScrollers.All(s => s.ScrollableWidth < 1), context + ": Skills content fits narrow native dialog without horizontal overflow ("
            + string.Join(", ", contentScrollers.Select(s => $"{s.Name}: viewport={s.ViewportWidth}, overflow={s.ScrollableWidth}")) + ")");
        Check(Within((ToggleSwitch)connected.Content, dialog)
            && Descendants(connected).OfType<TextBlock>().Any(t => t.Text == "Connected description" && t.ActualWidth > 0),
            context + ": narrow expanded skill retains a visible header switch and description");
        InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
        Check(saved is not null && saved.EnabledSkillIds.SequenceEqual(["provider/sample", "mcp:server:sample"]) && dialog.Result is not null,
            context + ": close commits all skill selections through existing save boundary");
        var reset = new ChatSettingsDialog(saved!, null, _ => Task.FromResult(new DesktopSkillSelection(items, new HashSet<string>(), "api.example")))
            { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        shown = reset.ShowAsync(); await Task.Delay(100); InvokeButton(Descendants(reset).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await Task.Delay(80);
        var resetNavigation = Field<NavigationView>(reset, "tabs");
        var resetTabs = resetNavigation.MenuItems.OfType<NavigationViewItem>().ToArray();
        Check(resetTabs.Select(t => t.Name).SequenceEqual(["ChatGeneralTab", "ChatToolsTab", "ChatToolConfigurationTab", "ChatSkillsTab", "ChatPluginsTab"])
            && ReferenceEquals(resetNavigation.SelectedItem, resetTabs[0]),
            context + ": restore defaults preserves General, Tools, Skills order without a provider form");
        InvokeButton(Descendants(reset).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
        Check(reset.Result?.EnabledSkillIds.Count == 0, context + ": restore defaults clears enabled skills");
        var failedSaveAttempted = false;
        var failedSave = new ChatSettingsDialog(saved!, null) { XamlRoot = root.XamlRoot, RequestedTheme = theme, SaveAsync = _ => { failedSaveAttempted = true; throw new IOException("disk"); } };
        var failedSaveOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        failedSave.Opened += (_, _) => failedSaveOpened.TrySetResult();
        shown = failedSave.ShowAsync(); await failedSaveOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForProviderUiAsync(() => Descendants(failedSave).OfType<Button>().Any(button => button.Name == "CloseButton"), "Save-failure dialog close action did not render");
        InvokeButton(Descendants(failedSave).OfType<Button>().Single(b => b.Name == "CloseButton"));
        await WaitForProviderUiAsync(() => failedSaveAttempted && Field<TextBlock>(failedSave, "validation").Visibility == Visibility.Visible, "Save-failure validation did not arrive");
        Check(shown.Status == Windows.Foundation.AsyncStatus.Started && failedSave.Result is null, context + ": disk failure keeps dialog and previous state intact");
        failedSave.DiscardOnShutdown = true; failedSave.Hide(); await shown;
        await CheckChatSkillProviderLogosAsync(root, theme);
    }

    private async Task CheckChatSkillProviderLogosAsync(Grid root, ElementTheme theme)
    {
        var context = "Skill provider logos / " + theme;
        var explicitIcon = new McpIcon("data:image/png;base64,AA==");
        var items = new DesktopSkill[] {
            new("clawhub/1password", "1password", "Provider logo without skill icons", "remote"),
            new("clawhub/explicit", "Explicit skill icon", "Explicit artwork takes priority", "remote") { Icons = [explicitIcon] },
            new("clawhub/unsupported", "Unsupported skill icon", "Unsafe artwork cannot hide the provider logo", "remote") { Icons = [new("file:///C:/private.png")] },
            new("clawhub/local", "Local skill", "Never infer a provider for local skills", "local"),
            new("clawhub/connected", "Connected skill", "Never infer a provider for MCP skills", "mcp"),
            new("unknown-provider/skill", "Unknown provider", "Generic fallback", "remote"),
            new("clawhub", "Unqualified skill", "Generic fallback", "remote")
        };
        var dialog = new ChatSettingsDialog(new(), null,
            _ => Task.FromResult(new DesktopSkillSelection(items, new HashSet<string> { "clawhub/1password" }, "localhost")))
            { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync(); await Task.Delay(100);
        dialog.ShowSkillsTab(); await Task.Delay(100); dialog.UpdateLayout();
        var rows = Descendants(dialog).OfType<SettingsExpander>().Where(row => row.Name == "ChatSkillCard").ToArray();
        DependencyObject Header(string id) => (DependencyObject)rows.Single(row => row.Tag as string == id).Header;
        FrameworkElement? ProviderLogo(string id) => Descendants(Header(id)).OfType<FrameworkElement>().SingleOrDefault(control => control.Name == "SkillProviderLogo");
        var artwork = ProviderLogo("clawhub/1password");
        Check(artwork is not null && artwork.Width == 32 && artwork.Height == 32
            && !Descendants(Header("clawhub/1password")).OfType<UserControl>().Any(control => control.Name == "McpServerIcon"),
            context + ": a provider-owned skill with no explicit icons uses the provider-logo renderer, including favorites");
        var resolved = Field<CatalogProvider>(artwork!, "provider");
        var expected = ProviderCatalog.SkillProvider(new(CatalogKind.Skill, "clawhub/1password", "1password", ""))!;
        Check(resolved.Id == expected.Id && resolved.Icons.SequenceEqual(expected.Icons) && resolved.Urls == new ProviderUrls(),
            context + ": settings use the same provider and logo sources as the Skills page, without external favicon fallback");
        var explicitSlot = Descendants(Header("clawhub/explicit")).OfType<UserControl>().Single(control => control.Name == "McpServerIcon");
        Check(ProviderLogo("clawhub/explicit") is null && Field<IReadOnlyList<McpIcon>>(explicitSlot, "icons").SequenceEqual([explicitIcon]),
            context + ": explicit skill icons retain priority over provider branding");
        Check(ProviderLogo("clawhub/unsupported") is not null,
            context + ": unsupported skill artwork falls back to the available provider logo");
        Check(new[] { "clawhub/local", "clawhub/connected", "unknown-provider/skill", "clawhub" }.All(id => ProviderLogo(id) is null
            && Descendants(Header(id)).OfType<UserControl>().Any(control => control.Name == "McpServerIcon")),
            context + ": local, MCP, unknown-provider and unqualified skills keep generic artwork rather than misleading branding");
        var providerRow = rows.Single(row => row.Tag as string == "clawhub/1password");
        Check(!providerRow.IsExpanded && providerRow.Content is ToggleSwitch toggle && Within(toggle, dialog)
            && Field<ScrollViewer>(dialog, "page").ScrollableWidth < 1,
            context + ": provider logo preserves the compact row, visible toggle, and narrow-dialog layout");
        InvokeButton(Descendants(dialog).OfType<Button>().Single(button => button.Name == "CloseButton")); await shown;
    }

    private async Task CheckSkillsAsync(ElementTheme theme)
    {
        await CheckSkillEditorAsync(theme);
        await CheckChatSkillsSettingsAsync(theme);
        var context = "Skills / " + theme;
        var session = new DesktopSession(new UiHost(true), new UiRuntime(), new());
        session.Settings.Chat.EnabledSkillIds = ["provider/sample", "mcp:server:missing"];
        var shell = new ChatShell(session) { RequestedTheme = theme }; window.Content = shell; await Task.Delay(200); await HistoryIdleAsync(shell);
        try
        {
            typeof(ChatShell).GetField("runtimeSkillCatalog", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell,
                new CatalogItem[] { new(CatalogKind.Skill, "provider/sample", "Sample", "Remote description")
                    { Icons = [new("data:image/png;base64,AA==", "dark", "image/png")] } });
            typeof(ChatShell).GetField("runtimeSkillPartition", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, session.HistoryPartition);
            var descriptors = (IReadOnlyList<DesktopSkill>)Call(shell, "AvailableSkillDescriptors")!;
            Check(descriptors.Single().Icons.Single() == new McpIcon("data:image/png;base64,AA==", "dark", "image/png"),
                context + ": shell settings selection retains catalog skill icon metadata");
            Call(shell, "RenderContextTags"); shell.UpdateLayout();
            Check(Descendants(shell).OfType<Border>().Count(b => b.Name == "EnabledSkillBadge") == 2, context + ": composer shows enabled and disconnected selections with removable badges");
            var runtime = (McpTurnSnapshot)Call(shell, "CaptureSkillRuntime", session.Settings.Chat)!;
            Check(runtime.Tools.Count == 2 && runtime.Context.Single().GetRawText().Contains("provider/sample") && !runtime.Context.Single().GetRawText().Contains("mcp:server:missing"),
                context + ": request context/tools include resolved enabled skills but not disconnected ones");
            Call(shell, "ResetContext");
            Check(Descendants(shell).OfType<Border>().Count(b => b.Name == "EnabledSkillBadge") == 2, context + ": attachment reset preserves chat skill preferences");
            InvokeButton(Descendants(shell).OfType<Button>().Single(b => b.Name == "DisableSkill" && (string)b.Tag == "provider/sample")); await HistoryIdleAsync(shell);
            var persisted = await SettingsStore.LoadAsync(session.DataDirectory, new());
            Check(!session.Settings.Chat.EnabledSkillIds.Contains("provider/sample") && !persisted.Chat.EnabledSkillIds.Contains("provider/sample")
                && Descendants(shell).OfType<Border>().Count(b => b.Name == "EnabledSkillBadge") == 1, context + ": badge removal persists disablement and rerenders composer");
            runtime = (McpTurnSnapshot)Call(shell, "CaptureSkillRuntime", session.Settings.Chat)!;
            Check(runtime.Tools.Count == 0 && runtime.Context.Count == 0, context + ": removing last available skill removes request tools and system catalog");
            Call(shell, "UpdateMode", ServiceKind.Agents);
            Check(!Descendants(shell).OfType<Border>().Any(b => b.Name == "EnabledSkillBadge"), context + ": model-only badges are hidden for Agents");
            Call(shell, "UpdateMode", ServiceKind.Ai); shell.RequestedTheme = theme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light; await Task.Delay(50);
            Check(Descendants(shell).OfType<Border>().Single(b => b.Name == "EnabledSkillBadge").Tag is "mcp:server:missing", context + ": unavailable badge survives native theme switch");
        }
        finally { await shell.ShutdownAsync(); window.Content = null; if (Directory.Exists(session.DataDirectory)) Directory.Delete(session.DataDirectory, true); }
    }
}
