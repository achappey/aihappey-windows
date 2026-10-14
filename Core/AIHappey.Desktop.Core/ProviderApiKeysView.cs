using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Edits an in-memory draft only; credential protection and persistence belong to the host.</summary>
public sealed class ProviderApiKeysView : StackPanel
{
    private readonly StackPanel rows = new() { Spacing = 8 };
    private readonly List<(TextBox Name, PasswordBox Key, Grid Row)> entries = [];
    private readonly AutoSuggestBox search = new() { Name = "ApiKeyProviderSearch", DisplayMemberPath = "Name", TextMemberPath = "Name",
        PlaceholderText = DesktopResources.Get("ApiKeyProviderSearch"), Visibility = Visibility.Collapsed };
    private readonly CatalogProvider[] providers = ProviderCatalog.All.Values.Where(p => ProviderCatalog.ApiKeyHeader(p) is not null)
        .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();

    public ProviderApiKeysView(IReadOnlyDictionary<string, string> headers)
    {
        Name = "ProviderApiKeysView"; Spacing = 12; MinWidth = 420;
        Children.Add(new TextBlock { Text = DesktopResources.Get("ApiKeysHint"), TextWrapping = TextWrapping.Wrap, MaxWidth = 440 });
        foreach (var (name, key) in headers) AddEntry(name, key);
        if (entries.Count == 0) AddEntry("X-OpenAI-Key", "");
        Children.Add(new ScrollViewer { Content = rows, MaxHeight = 320,
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var select = new Button { Name = "SelectApiKeyProvider", Content = DesktopResources.Get("SelectApiKeyProvider") };
        var manual = new Button { Name = "AddManualProviderKey", Content = DesktopResources.Get("AddProviderKey") };
        foreach (var button in new[] { select, manual }) { ControlAppearance.Stock(button); actions.Children.Add(button); }
        Children.Add(actions); Children.Add(search);
        ControlAppearance.Stock(search); AutomationProperties.SetName(search, DesktopResources.Get("ApiKeyProviderSearch"));
        search.ItemsSource = providers;
        select.Click += (_, _) =>
        {
            search.Visibility = search.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (search.Visibility == Visibility.Visible) { search.Text = ""; search.ItemsSource = providers; search.Focus(FocusState.Programmatic); }
        };
        manual.Click += (_, _) => FocusKey(AddEntry("", ""));
        search.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            search.ItemsSource = Matches(search.Text);
        };
        search.SuggestionChosen += (_, args) => { if (args.SelectedItem is CatalogProvider provider) SelectProvider(provider); };
        search.QuerySubmitted += (_, args) =>
        {
            if (args.ChosenSuggestion is CatalogProvider provider) SelectProvider(provider);
            else if (Matches(args.QueryText).Where(p => p.Name.Equals(args.QueryText.Trim(), StringComparison.OrdinalIgnoreCase)
                || p.Id.Equals(args.QueryText.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray() is [var exact]) SelectProvider(exact);
        };
    }

    private CatalogProvider[] Matches(string query) => providers.Where(p => (p.Name + " " + p.Id)
        .Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();

    private void SelectProvider(CatalogProvider provider)
    {
        if (ProviderCatalog.ApiKeyHeader(provider) is not { } header) return;
        var existing = entries.FirstOrDefault(e => e.Name.Text.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
        var key = existing.Key ?? AddEntry(header, "");
        search.IsSuggestionListOpen = false; search.Visibility = Visibility.Collapsed;
        FocusKey(key);
    }

    private void FocusKey(PasswordBox key) => DispatcherQueue.TryEnqueue(() => key.Focus(FocusState.Programmatic));

    private PasswordBox AddEntry(string name, string key)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var field = new TextBox { Name = "ApiKeyHeader", Text = name, PlaceholderText = "X-OpenAI-Key" };
        var secret = new PasswordBox { Name = "ProviderApiKey", Password = key, PlaceholderText = DesktopResources.Get("ApiKey"), PasswordRevealMode = PasswordRevealMode.Hidden };
        var remove = new Button { Content = DesktopResources.Get("Remove") };
        foreach (var control in new Control[] { field, secret, remove }) ControlAppearance.Stock(control);
        AutomationProperties.SetName(field, DesktopResources.Get("McpHeaderName"));
        AutomationProperties.SetName(secret, DesktopResources.Get("ApiKey"));
        row.Children.Add(field); Grid.SetColumn(secret, 1); row.Children.Add(secret); Grid.SetColumn(remove, 2); row.Children.Add(remove);
        entries.Add((field, secret, row)); rows.Children.Add(row);
        remove.Click += (_, _) => { entries.RemoveAll(e => e.Row == row); rows.Children.Remove(row); };
        return secret;
    }

    public Dictionary<string, string> ReadHeaders()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key.Password)) continue;
            if (!result.TryAdd(entry.Name.Text.Trim(), entry.Key.Password.Trim())) throw new ArgumentException(DesktopResources.Get("UniqueProviderHeaders"));
        }
        return result;
    }
}
