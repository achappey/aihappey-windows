using System.Reflection;
using System.Text.Json;
using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Charts;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckUsageAsync(ElementTheme theme)
    {
        var chart = new Chart { MinHeight = 320, ShowLegend = true };
        var categories = new CategoryAxis();
        var values = new LinearAxis { Minimum = 0, GridLines = GridLines.Major };
        chart.Axes.Add(categories); chart.Axes.Add(values);
        var bars = new BarSeries { Title = "Messages", Orientation = BarOrientation.Vertical,
            XAxis = categories, YAxis = values,
            XValues = new Samples { ItemsSource = new List<string> { "Mon", "Tue" } },
            YValues = new Samples { ItemsSource = new List<double> { 2, 4 } } };
        var line = new LineSeries { Title = "Tokens", XAxis = categories, YAxis = values,
            XValues = new Samples { ItemsSource = new List<string> { "Mon", "Tue" } },
            YValues = new Samples { ItemsSource = new List<double> { 3, 5 } } };
        chart.Series.Add(bars); chart.Series.Add(line);
        var root = new Grid { RequestedTheme = theme }; root.Children.Add(chart);
        window!.Content = root; window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 800)); window.Activate();
        await Task.Delay(200); chart.UpdateLayout();
        Check(chart.IsLoaded && chart.ActualWidth > 0 && chart.ActualHeight >= 320, "Usage charts load and arrange / " + theme);
        using var stream = typeof(App).Assembly.GetManifestResourceStream("Desktop.UsageFixture")!;
        var conversations = JsonSerializer.Deserialize<Conversation[]>(stream, PortableConversations.Json)!;
        var analytics = UsageAnalytics.Build(conversations, ProviderCatalog.All.ToDictionary(p => p.Key, p => p.Value.Name));
        var page = (UserControl)Activator.CreateInstance(typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.UsagePage")!)!;
        root.Children.Clear(); root.Children.Add(page);
        InvokeOverview(page, "SetItems", analytics); await Task.Delay(100); page.UpdateLayout();
        var tabs = Descendants(page).OfType<NavigationView>().Single(v => v.Name == "UsageGroups");
        var sections = Descendants(page).OfType<ComboBox>().Single(v => v.Name == "UsageSections");
        var context = "Usage / " + theme;
        CheckNativeTabs(tabs, context);
        Check(tabs.MenuItems.Count == 5 && Descendants(page).OfType<Border>().Count(b => b.Name.StartsWith("UsageKpi")) == 6,
            context + ": five native groups and six summary KPIs");
        Check(Descendants(page).OfType<Chart>().Count() == 2 && Descendants(page).OfType<Chart>().SelectMany(c => c.Series).OfType<LineSeries>().Count() == 1,
            context + ": summary uses native bar and line series, not a browser");
        var count = Descendants(page).OfType<Border>().Single(b => b.Name == "UsageKpiConversations");
        Check(Descendants(count).OfType<TextBlock>().Any(t => t.Name == "UsageKpiValue" && t.Text == "2"), context + ": portable fixture counts rendered");
        foreach (var tab in tabs.MenuItems.OfType<NavigationViewItem>())
        {
            SelectNativeTab(tabs, (string)tab.Tag);
            foreach (var section in sections.Items.OfType<ComboBoxItem>().ToArray())
            {
                sections.SelectedItem = section; await Task.Delay(50); page.UpdateLayout();
                var charts = Descendants(page).OfType<Chart>().ToArray();
                Check(charts.All(c => c.Axes.Count == 2 && c.Series.Count > 0 && !string.IsNullOrEmpty(AutomationProperties.GetName(c))),
                    context + ": labeled chart axes and series / " + tab.Tag + "/" + section.Tag);
                Check(Descendants(page).OfType<StackPanel>().Single(p => p.Name == "UsageView").Children.Count > 0,
                    context + ": populated native view / " + tab.Tag + "/" + section.Tag);
                foreach (var expander in Descendants(page).OfType<Expander>().Where(e => e.Name == "UsageChartValues").ToArray())
                {
                    expander.IsExpanded = true;
                    Check(expander.Content is StackPanel && Descendants(expander).OfType<TextBlock>().Any(), context + ": exact values are available without hover");
                }
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(500, 800)); await Task.Delay(30); page.UpdateLayout();
                Check(charts.All(c => c.ActualWidth <= root.ActualWidth) && Descendants(page).OfType<ScrollViewer>().All(v => v.ScrollableWidth < 1),
                    context + ": narrow view fits without horizontal scrolling / " + tab.Tag + "/" + section.Tag);
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 800)); await Task.Delay(30);
            }
        }
        SelectNativeTab(tabs, "models"); sections.SelectedItem = sections.Items[0];
        // Large model table keeps chart top-12 and paginates all rows independently.
        var many = Enumerable.Range(0, 32).Select(i => PortableConversations.Read("""{"id":"cID","metadata":{"name":"Long model fixture"},"messages":[{"id":"mID","role":"assistant","metadata":{"model":"openai/model-ID"},"parts":[]}]}""".Replace("ID", i.ToString()))).ToArray();
        InvokeOverview(page, "SetItems", UsageAnalytics.Build(many)); await Task.Delay(80); page.UpdateLayout();
        var table = Descendants(page).OfType<Border>().Single(b => b.Name == "UsageTable");
        Check(Descendants(table).OfType<Grid>().Count(g => g.Name == "UsageTableRow") == 15, context + ": table starts with fifteen rows");
        InvokeButton(Descendants(table).OfType<Button>().Single(b => b.Name == "UsageNext"));
        Check(Descendants(table).OfType<Grid>().Count(g => g.Name == "UsageTableRow") == 15 && Descendants(table).OfType<TextBlock>().Any(t => t.Text == "2 / 3"), context + ": table advances to the second page");
        InvokeButton(Descendants(table).OfType<Button>().Single(b => b.Name == "UsageNext"));
        Check(Descendants(table).OfType<Grid>().Count(g => g.Name == "UsageTableRow") == 2 && !Descendants(table).OfType<Button>().Single(b => b.Name == "UsageNext").IsEnabled, context + ": final table page is bounded");
        var selected = tabs.SelectedItem;
        Check(((NavigationViewItem)selected).Focus(FocusState.Keyboard), context + ": native group supports keyboard focus");
        var oldChart = Descendants(page).OfType<Chart>().First();
        root.RequestedTheme = theme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark; await Task.Delay(80);
        Check(ReferenceEquals(tabs.SelectedItem, selected) && ReferenceEquals(oldChart, Descendants(page).OfType<Chart>().First()), context + ": runtime theme updates do not rebuild navigation or charts");
        InvokeOverview(page, "Loading");
        Check(!tabs.IsEnabled && !sections.IsEnabled && !Descendants(page).OfType<Chart>().Any()
            && Descendants(page).OfType<Button>().Single(b => b.Name == "UsageCancel").Visibility == Visibility.Visible, context + ": loading clears results and leaves Cancel available");
        var canceled = false; page.GetType().GetProperty("CancelRequested")!.SetValue(page, (Action)(() => canceled = true));
        InvokeButton(Descendants(page).OfType<Button>().Single(b => b.Name == "UsageCancel")); Check(canceled, context + ": cancellation callback");
        InvokeOverview(page, "Error", "Usage fixture failure");
        var retried = false; page.GetType().GetProperty("RetryRequested")!.SetValue(page, (Action)(() => retried = true));
        InvokeButton(Descendants(page).OfType<Button>().Single(b => b.Name == "UsageRetry"));
        Check(retried && Descendants(page).OfType<TextBlock>().Any(t => t.Text == "Usage fixture failure"), context + ": inline error and retry callback");
        InvokeOverview(page, "SetItems", analytics);
        Check(ReferenceEquals(tabs.SelectedItem, selected) && !ReferenceEquals(oldChart, Descendants(page).OfType<Chart>().First()), context + ": refresh replaces chart data but retains navigation identity");
        InvokeOverview(page, "SetActionsEnabled", false); InvokeOverview(page, "SetItems", analytics);
        Check(!tabs.IsEnabled && !sections.IsEnabled, context + ": refresh respects shell busy state");
        InvokeOverview(page, "SetActionsEnabled", true); InvokeOverview(page, "Reset"); InvokeOverview(page, "SetItems", UsageAnalytics.Build([]));
        Check(!Descendants(page).OfType<Chart>().Any() && Descendants(page).OfType<TextBlock>().Any(t => t.Text == DesktopResources.Get("UsageNoData")), context + ": empty history uses explicit no-data cards");
        root.Children.Clear();
        await CheckUsageShellAsync(theme, conversations);
        window.Content = null;
    }

    private async Task CheckUsageShellAsync(ElementTheme theme, Conversation[] conversations)
    {
        var session = new DesktopSession(new UiHost(true), new UiRuntime(), new());
        var shell = new ChatShell(session) { RequestedTheme = theme };
        try
        {
            var store = Field<HistoryStore>(shell, "history");
            foreach (var c in conversations) await store.SaveAsync(session.HistoryPartition, c);
            var ai = Field<StackPanel>(shell, "aiNavigation");
            Check(ai.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>().Select(b => b.Name).SequenceEqual(["NavigateModels", "NavigateProviders", "NavigateUsage"]), "Usage shell / " + theme + ": AI navigation registration");
            var enumType = typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.DesktopPage")!;
            typeof(ChatShell).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, [Enum.Parse(enumType, "Usage")]);
            Task Load(CancellationToken token) => (Task)typeof(ChatShell).GetMethod("LoadUsageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, [token])!;
            await Load(CancellationToken.None);
            var page = Field<UserControl>(shell, "usagePage");
            Check(Field<UsageAnalytics>(page, "analytics").Messages == 5, "Usage shell / " + theme + ": reads all saved history without discovery or sidebar filtering");
            var refresh = Field<Button>(shell, "refresh");
            Check(AutomationProperties.GetHelpText(refresh) == DesktopResources.Get("UsageRefresh"), "Usage shell / " + theme + ": page-specific refresh help");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await Load(cancellation.Token); Check(false, "Usage canceled load"); }
            catch (OperationCanceledException) { Check(Field<UsageAnalytics?>(page, "analytics") is null, "Usage shell / " + theme + ": cancellation cannot publish results"); }
            var pending = Load(CancellationToken.None);
            session.Settings.Ai.Location = RuntimeLocation.Remote; session.Settings.Ai.RemoteUrl = "https://usage-other.invalid/";
            typeof(ChatShell).GetMethod("InvalidateCatalogs", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, null);
            await pending;
            Check(Field<UsageAnalytics?>(page, "analytics") is null, "Usage shell / " + theme + ": endpoint transition rejects stale background results");
            await Load(CancellationToken.None);
            Check(Field<UsageAnalytics>(page, "analytics").Conversations == 0, "Usage shell / " + theme + ": new endpoint never inherits previous history");
        }
        finally
        {
            await shell.ShutdownAsync();
            if (Directory.Exists(session.DataDirectory)) Directory.Delete(session.DataDirectory, true);
        }
    }
}
