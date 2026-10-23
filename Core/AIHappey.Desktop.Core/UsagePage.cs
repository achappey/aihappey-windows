using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using static AIHappey.Desktop.Core.UsageDashboardVisuals;
using static AIHappey.Desktop.Core.UsageFormatting;

namespace AIHappey.Desktop.Core;

/// <summary>One native read-only overview. Only the active view's charts are constructed.</summary>
internal sealed class UsagePage : UserControl
{
    private static readonly (string Key, string Title, (string Key, string Title)[] Views)[] Groups =
    [
        ("overview", "Overview", [("summary", "Summary"), ("conversations", "Conversations"), ("activity", "Activity"),
            ("parts", "Parts"), ("attachments", "Attachments"), ("rich", "RichParts"), ("balance", "Balance"), ("finish", "Finish")]),
        ("models", "ModelsAgents", [("models", "Models"), ("agents", "Agents"), ("providers", "Providers"), ("matrix", "Matrix")]),
        ("tokens", "TokensCost", [("tokens", "Tokens"), ("cost", "Cost"), ("outliers", "Outliers")]),
        ("performance", "Performance", [("summary", "Summary"), ("ttft", "Ttft"), ("tps", "Tps")]),
        ("tools", "ToolsMcp", [("tools", "Tools"), ("states", "States"), ("results", "Results")])
    ];
    private readonly StackPanel body = new() { Spacing = 16, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel view = new() { Name = "UsageView", Spacing = 16 };
    private readonly TextBlock description = Label("");
    private readonly TextBlock status = Label("");
    private readonly ProgressRing progress = new() { Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly NavigationView navigation = new() { Name = "UsageGroups", PaneDisplayMode = NavigationViewPaneDisplayMode.Top,
        IsSettingsVisible = false, IsPaneToggleButtonVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, AlwaysShowHeader = false, Height = 56 };
    private readonly ComboBox sections = new() { Name = "UsageSections", Header = Text("View"), HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 420 };
    private readonly Button retry = new() { Name = "UsageRetry", Content = DesktopResources.Get("Retry"), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button cancel = new() { Name = "UsageCancel", Content = DesktopResources.Get("Cancel"), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Dictionary<string, string> selectedViews = new(StringComparer.Ordinal);
    private readonly ScrollViewer viewer;
    private UsageAnalytics? analytics;
    private string group = "overview";
    private bool updating, working = true, actionsEnabled = true;
    public Action? RetryRequested { get; set; }
    public Action? CancelRequested { get; set; }

    public UsagePage()
    {
        Name = "UsagePage";
        var title = Label(Text("Title"), 36); title.Name = "UsageTitle"; title.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        title.TextAlignment = TextAlignment.Center; AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        description.Name = "UsageDescription"; description.TextAlignment = status.TextAlignment = TextAlignment.Center;
        status.Name = "UsageStatus"; AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        foreach (var item in Groups)
        {
            var tab = new NavigationViewItem { Name = "UsageGroup_" + item.Key, Tag = item.Key, Content = Text(item.Title) };
            AutomationProperties.SetAutomationId(tab, tab.Name); navigation.MenuItems.Add(tab);
            selectedViews[item.Key] = item.Views[0].Key;
        }
        navigation.SelectedItem = navigation.MenuItems[0];
        body.Children.Add(title); body.Children.Add(description); body.Children.Add(navigation); body.Children.Add(sections);
        body.Children.Add(progress); body.Children.Add(status); body.Children.Add(retry); body.Children.Add(cancel); body.Children.Add(view);
        viewer = new ScrollViewer { Content = body, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Content = viewer;
        foreach (var control in new Control[] { sections, retry, cancel }) ControlAppearance.Native(control);
        ToolbarControls.Label(sections, Text("View"));
        navigation.SelectionChanged += (_, _) =>
        {
            if (updating || navigation.SelectedItem is not NavigationViewItem { Tag: string key }) return;
            group = key; BuildSections(); Render();
        };
        sections.SelectionChanged += (_, _) =>
        {
            if (updating || sections.SelectedItem is not ComboBoxItem { Tag: string key }) return;
            selectedViews[group] = key; Render();
        };
        retry.Click += (_, _) => RetryRequested?.Invoke(); cancel.Click += (_, _) => CancelRequested?.Invoke();
        viewer.SizeChanged += (_, _) => SizeBody(); Loaded += (_, _) => SizeBody();
        BuildSections(); Loading();
    }

    private void SizeBody()
    {
        var width = viewer.ViewportWidth > 0 ? viewer.ViewportWidth : viewer.ActualWidth;
        if (width > 0) body.Width = Math.Max(0, Math.Min(1140, width - 48));
    }
    private void BuildSections()
    {
        updating = true; sections.Items.Clear();
        foreach (var item in Groups.Single(g => g.Key == group).Views)
        {
            var choice = new ComboBoxItem { Tag = item.Key, Content = Text(item.Title) }; sections.Items.Add(choice);
            if (item.Key == selectedViews[group]) sections.SelectedItem = choice;
        }
        updating = false;
    }
    public new void Loading()
    {
        working = true; analytics = null; view.Children.Clear(); description.Text = "";
        status.Text = Text("Loading"); status.Visibility = progress.Visibility = cancel.Visibility = Visibility.Visible;
        progress.IsActive = true; retry.Visibility = Visibility.Collapsed; SetControlsEnabled();
    }
    public void Error(string message)
    {
        working = true; analytics = null; view.Children.Clear(); description.Text = ""; status.Text = message;
        status.Visibility = retry.Visibility = Visibility.Visible; progress.Visibility = cancel.Visibility = Visibility.Collapsed;
        progress.IsActive = false; SetControlsEnabled();
    }
    public void Reset()
    {
        group = "overview";
        foreach (var item in Groups) selectedViews[item.Key] = item.Views[0].Key;
        updating = true; navigation.SelectedItem = navigation.MenuItems[0]; updating = false;
        BuildSections(); Loading();
    }
    public void SetItems(UsageAnalytics value)
    {
        analytics = value; working = false; progress.IsActive = false;
        progress.Visibility = retry.Visibility = cancel.Visibility = status.Visibility = Visibility.Collapsed;
        description.Text = DesktopResources.Format("UsageSubtitle", Number(value.Conversations), Number(value.Messages));
        SetControlsEnabled(); Render();
    }
    public void SetActionsEnabled(bool value) { actionsEnabled = value; SetControlsEnabled(); }
    private void SetControlsEnabled()
    { navigation.IsEnabled = sections.IsEnabled = actionsEnabled && !working; retry.IsEnabled = actionsEnabled; }

    private void Render()
    {
        if (working || analytics is not { } a) return;
        view.Children.Clear();
        void Add(params UIElement[] elements) { foreach (var element in elements) view.Children.Add(element); }
        switch (group + ":" + selectedViews[group])
        {
            case "overview:summary":
                Add(Grid(Kpi("Conversations", Number(a.Conversations)), Kpi("Messages", Number(a.Messages)), Kpi("Models", Number(a.ModelStats.Count)),
                    Kpi("Agents", Number(a.AgentStats.Count)), Kpi("TotalTokens", Compact(a.TotalTokens)), Kpi("EstimatedCost", Cost(a.TotalCost))),
                    Grid(Counts("RoleMix", a.RoleCounts, positiveOnly: true), Trend("ActivityOverTime", a.DailyMessages, "Messages"))); break;
            case "overview:conversations":
                var buckets = new Dictionary<string, double> { ["0-2"] = 0, ["3-5"] = 0, ["6-10"] = 0, ["11-20"] = 0, ["21+"] = 0 };
                foreach (var c in a.ConversationFacts) buckets[c.MessageCount <= 2 ? "0-2" : c.MessageCount <= 5 ? "3-5" : c.MessageCount <= 10 ? "6-10" : c.MessageCount <= 20 ? "11-20" : "21+"]++;
                Add(Grid(Counts("ConversationSizeDistribution", buckets, horizontal: false, sort: false), Conversations(a, "TopConversationsByMessages", c => c.MessageCount, "Messages"))); break;
            case "overview:activity": Add(Grid(Counts("HourOfDay", a.HourCounts.OrderBy(p => p.Key).ToDictionary(), horizontal: false, sort: false), Trend("DailyTokens", a.DailyTokens, "Tokens"))); break;
            case "overview:parts":
                Add(Grid(Kpi("Parts", Number(a.Parts)), Kpi("TextVolume", Compact(a.TextChars) + " " + Text("Characters"))),
                    Grid(Counts("PartTypes", a.ContentTypeCounts), Counts("Sources", a.SourceTypeCounts, positiveOnly: true))); break;
            case "overview:attachments": Add(Counts("AttachmentTypes", a.AttachmentTypeCounts)); break;
            case "overview:rich": Add(Grid(Kpi("Reasoning", Number(a.MessagesWithReasoning)), Kpi("DataParts", Number(a.MessagesWithDataParts)),
                    Kpi("Sources", Number(a.MessagesWithSources)), Kpi("StructuredOutput", Number(a.MessagesWithStructuredOutput)))); break;
            case "overview:balance": Add(Counts("TemperatureDistribution", a.TemperatureCounts, horizontal: false)); break;
            case "overview:finish": Add(Grid(Counts("FinishReasons", a.FinishReasonCounts), Counts("Errors", a.ErrorCounts))); break;
            case "models:models": Entity(a.ModelStats, false); break;
            case "models:agents": Entity(a.AgentStats, true); break;
            case "models:providers":
                var providers = Providers(a).Take(15).ToArray();
                Add(Grid(Bars("TopProviders", providers.Select(p => p.Label), providers.Select(p => (double)p.Count), "Messages"),
                    Bars("ProviderUniqueModels", providers.Select(p => p.Label), providers.Select(p => (double)p.Models.Count), "Models"))); break;
            case "models:matrix":
                var providerLabels = Providers(a).Take(8).Select(p => p.Label).ToArray();
                var models = UsageAnalytics.TopEntities(a.ModelStats, 8).ToArray();
                Add(ChartCard(Text("ProviderModelMatrix"), providerLabels, models.Select(m => new UsageChartSeries(m.Label,
                    providerLabels.Select(p => (double)a.MessageFacts.Count(f => f.EntityKind == "model" && f.ModelLabel == m.Label && f.ProviderLabel == p)).ToArray())).ToArray())); break;
            case "tokens:tokens":
                Add(Grid(Kpi("InputTokens", Compact(a.InputTokens)), Kpi("OutputTokens", Compact(a.OutputTokens)), Kpi("TotalTokens", Compact(a.TotalTokens))),
                    Grid(Bars("TokenBreakdown", [Text("InputTokens"), Text("OutputTokens")], [a.InputTokens, a.OutputTokens], "Tokens", positiveOnly: true), Trend("DailyTokens", a.DailyTokens, "Tokens"))); break;
            case "tokens:cost": Add(Kpi("EstimatedCost", Cost(a.TotalCost)), Grid(Trend("DailyCost", a.DailyCost, "Cost"), Conversations(a, "TopConversationsByCost", c => c.Cost, "Cost"))); break;
            case "tokens:outliers":
                Add(Table(Text("LargestTokenUsage"), [Text("Conversation"), Text("Tokens"), Text("Cost"), Text("ModelsAgents")],
                        a.MessageFacts.Where(f => f.Tokens > 0).OrderByDescending(f => f.Tokens).Take(12).Select(f => new[] { f.ConversationName, Compact(f.Tokens), Cost(f.Cost), f.EntityLabel ?? "—" }).ToArray()),
                    Table(Text("LargestCost"), [Text("Conversation"), Text("Cost"), Text("Tokens"), Text("ModelsAgents")],
                        a.MessageFacts.Where(f => f.Cost > 0).OrderByDescending(f => f.Cost).Take(12).Select(f => new[] { f.ConversationName, Cost(f.Cost), Compact(f.Tokens), f.EntityLabel ?? "—" }).ToArray())); break;
            case "performance:summary":
                var facts = a.MessageFacts.Where(f => f.Role == "assistant").ToArray();
                var ttft = UsageAnalytics.Summarize(facts.Select(f => f.Ttft)); var tps = UsageAnalytics.Summarize(facts.Select(f => f.Tps));
                Add(Grid(Kpi("AverageTtft", Metric(ttft?.Mean, "s")), Kpi("P95Ttft", Metric(ttft?.P95, "s")), Kpi("AverageTps", Metric(tps?.Mean, "tok/s")),
                    Kpi("MeasuredResponses", Number(facts.Count(f => f.Ttft is not null || f.Tps is not null)))),
                    Grid(MetricTrend(facts, true), MetricTrend(facts, false))); break;
            case "performance:ttft": Performance(a, true); break;
            case "performance:tps": Performance(a, false); break;
            case "tools:tools":
                var tools = Tools(a).Take(15).ToArray();
                Add(Kpi("MessagesWithTools", Number(a.MessagesWithTools)), Bars("TopTools", tools.Select(t => t.Label), tools.Select(t => (double)t.Count), "Invocations")); break;
            case "tools:states":
                var states = Tools(a).Take(15).ToArray();
                Add(Grid(Counts("ToolStates", a.ToolStateCounts, positiveOnly: true), ChartCard(Text("ToolSuccessByName"), states.Select(t => t.Label).ToArray(),
                    [new(Text("Success"), states.Select(t => (double)t.Success).ToArray()), new(Text("Error"), states.Select(t => (double)t.Error).ToArray()),
                     new(Text("Pending"), states.Select(t => (double)t.Pending).ToArray())], horizontal: true))); break;
            case "tools:results": Add(Counts("ToolResultTypes", a.ToolResultTypeCounts)); break;
        }
        void Entity(IReadOnlyDictionary<string, UsageEntityStat> stats, bool agent)
        {
            var all = UsageAnalytics.TopEntities(stats, stats.Count).ToArray(); var top = all.Take(12).ToArray();
            Add(Grid(Bars(agent ? "TopAgents" : "TopModels", top.Select(s => s.Label), top.Select(s => (double)s.Count), "Messages"),
                    Bars(agent ? "AgentTokens" : "ModelTokens", top.Select(s => s.Label), top.Select(s => s.Tokens), "Tokens")),
                Table(Text(agent ? "Agents" : "Models"), [Text(agent ? "Agent" : "Model"), Text("Messages"), Text("Tokens"), Text("Cost")],
                    all.Select(s => new[] { s.Label, Number(s.Count), Compact(s.Tokens), Cost(s.Cost) }).ToArray()));
        }
        void Performance(UsageAnalytics data, bool firstToken)
        {
            var rows = data.MessageFacts.Where(f => f.Role == "assistant" && f.EntityKind is not null && f.EntityKey is not null && f.EntityLabel is not null)
                .GroupBy(f => (f.EntityKind, f.EntityKey)).Select(g => new { Kind = g.Key.EntityKind!, Label = g.First().EntityLabel!, Summary = UsageAnalytics.Summarize(g.Select(f => firstToken ? f.Ttft : f.Tps)) })
                .Where(g => g.Summary is not null).OrderByDescending(g => g.Summary!.Count).ThenBy(g => g.Label, StringComparer.CurrentCulture).ToArray();
            var metricTitle = Text(firstToken ? "Ttft" : "Tps"); var unit = firstToken ? "s" : "tok/s";
            var charts = new List<UIElement>();
            foreach (var kind in new[] { "model", "agent" })
            {
                var top = rows.Where(r => r.Kind == kind).Take(12).ToArray();
                charts.Add(ChartCard(Text(kind == "agent" ? "Agents" : "Models") + " · " + metricTitle, top.Select(r => r.Label).ToArray(),
                    [new(Text("Average") + " (" + unit + ")", top.Select(r => r.Summary!.Mean).ToArray())], horizontal: true));
            }
            Add(Grid(charts.ToArray()), Table(metricTitle, [Text("ModelsAgents"), Text("Kind"), Text("Samples"), Text("Average") + " (" + unit + ")", Text("Median") + " (" + unit + ")", "P95 (" + unit + ")"],
                rows.Select(r => new[] { r.Label, Text(r.Kind == "agent" ? "Agent" : "Model"), Number(r.Summary!.Count), Metric(r.Summary.Mean), Metric(r.Summary.Median), Metric(r.Summary.P95) }).ToArray()));
        }
    }

    private static IEnumerable<UsageProviderStat> Providers(UsageAnalytics a) => a.ProviderStats.Values.OrderByDescending(p => p.Count).ThenBy(p => p.Label, StringComparer.CurrentCulture);
    private static IEnumerable<UsageToolStat> Tools(UsageAnalytics a) => a.ToolStats.Values.OrderByDescending(p => p.Count).ThenBy(p => p.Label, StringComparer.CurrentCulture);
    private static Border Counts(string title, IReadOnlyDictionary<string, double> values, bool horizontal = true, bool sort = true, bool positiveOnly = false)
    {
        var items = (sort ? UsageAnalytics.Top(values) : values).ToArray();
        return Bars(title, items.Select(p => p.Key), items.Select(p => p.Value), "Count", horizontal, positiveOnly);
    }
    private static Border Bars(string title, IEnumerable<string> labels, IEnumerable<double> values, string metric, bool horizontal = true, bool positiveOnly = false) =>
        ChartCard(Text(title), labels.ToArray(), [new(Text(metric), values.ToArray())], horizontal: horizontal, positiveOnly: positiveOnly);
    private static Border Trend(string title, IReadOnlyDictionary<string, double> values, string metric)
    { var items = values.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(); return ChartCard(Text(title), items.Select(p => p.Key).ToArray(), [new(Text(metric), items.Select(p => p.Value).ToArray())], line: true); }
    private static Border Conversations(UsageAnalytics a, string title, Func<UsageConversationFact, double> metric, string label)
    { var top = a.ConversationFacts.OrderByDescending(metric).Take(12).ToArray(); return Bars(title, top.Select(c => c.Name), top.Select(metric), label); }
    private static Border MetricTrend(UsageMessageFact[] facts, bool firstToken)
    {
        var days = facts.Where(f => f.Day is not null && (firstToken ? f.Ttft : f.Tps) is not null).GroupBy(f => f.Day!)
            .ToDictionary(g => g.Key, g => UsageAnalytics.Summarize(g.Select(f => firstToken ? f.Ttft : f.Tps))!.Mean);
        return Trend(firstToken ? "TtftOverTime" : "TpsOverTime", days, firstToken ? "Ttft" : "Tps");
    }
}
