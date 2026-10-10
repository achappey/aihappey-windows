using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed record DesktopSkillSelection(IReadOnlyList<DesktopSkill> Items, IReadOnlySet<string> Favorites, string RemoteTitle, string? Warning = null);

/// <summary>Native draft editor; prefetch does not publish settings or prevent closing the dialog.</summary>
public sealed class SkillsSettingsView : StackPanel, IDisposable
{
    private readonly ChatPreferences draft;
    private readonly Func<CancellationToken, Task<DesktopSkillSelection>>? load;
    private readonly Func<string, CancellationToken, Task>? prefetch;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBox search = new() { Name = "ChatSkillSearch", PlaceholderText = DesktopResources.Get("SearchSkills") };
    private readonly StackPanel cards = new() { Spacing = 12 };
    private readonly TextBlock feedback = new() { Name = "ChatSkillFeedback", TextWrapping = TextWrapping.Wrap };
    private readonly Button retry = new() { Name = "ChatSkillsRetry", Content = DesktopResources.Get("Retry"), Visibility = Visibility.Collapsed };
    private DesktopSkillSelection selection = new([], new HashSet<string>(), DesktopResources.Get("Skills"));
    private bool started;
    private bool disposed;
    public async Task RefreshAsync() { if (started && !disposed) await LoadAsync(); }
    public SkillsSettingsView(ChatPreferences draft, Func<CancellationToken, Task<DesktopSkillSelection>>? load,
        Func<string, CancellationToken, Task>? prefetch)
    {
        this.draft = draft; this.load = load; this.prefetch = prefetch;
        Name = "ChatSkillsView"; Spacing = 16;
        Children.Add(new TextBlock { Text = DesktopResources.Get("SkillsContextHint"), TextWrapping = TextWrapping.Wrap });
        ControlAppearance.Stock(search); ToolbarControls.Label(search, DesktopResources.Get("SearchSkills")); Children.Add(search);
        AutomationProperties.SetLiveSetting(feedback, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        Children.Add(feedback); ControlAppearance.Stock(retry); Children.Add(retry); Children.Add(cards);
        search.TextChanged += (_, _) => Render(); retry.Click += async (_, _) => await LoadAsync();
        Loaded += async (_, _) => { if (!started) { started = true; await LoadAsync(); } };
    }
    private async Task LoadAsync()
    {
        if (disposed) return;
        retry.Visibility = Visibility.Collapsed; feedback.Text = DesktopResources.Get("Working");
        try
        {
            if (load is not null) selection = await load(lifetime.Token);
            if (disposed) return;
            feedback.Text = selection.Warning ?? ""; retry.Visibility = selection.Warning is null ? Visibility.Collapsed : Visibility.Visible; Render();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch
        {
            if (disposed) return;
            feedback.Text = DesktopResources.Get("SkillsCatalogFailed"); retry.Visibility = Visibility.Visible; Render();
        }
    }
    private void Render()
    {
        cards.Children.Clear();
        var query = search.Text.Trim();
        var items = selection.Items.Where(s => string.Join(" ", s.Id, s.Name, s.Description, s.Server)
            .Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var favorite = items.Where(s => selection.Favorites.Contains(s.Id)).ToArray();
        Section(DesktopResources.Format("FavoritesCount", favorite.Length), favorite);
        Section(DesktopResources.Get("McpTitle"), items.Where(s => s.Origin == "mcp" && !selection.Favorites.Contains(s.Id)).ToArray());
        Section(DesktopResources.Get("Local"), items.Where(s => s.Origin == "local" && !selection.Favorites.Contains(s.Id)).ToArray());
        Section(selection.RemoteTitle, items.Where(s => s.Origin == "remote" && !selection.Favorites.Contains(s.Id)).ToArray());
        var missing = draft.EnabledSkillIds.Where(id => !selection.Items.Any(s => s.Id == id))
            .Where(id => id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (missing.Length > 0)
        {
            cards.Children.Add(new TextBlock { Text = DesktopResources.Get("SkillsUnavailable"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            foreach (var id in missing) Card(new(id, id, DesktopResources.Get("SkillUnavailableHint"), "unavailable"));
        }
        if (cards.Children.Count == 0) cards.Children.Add(new TextBlock { Text = DesktopResources.Get("NoResults") });
    }
    private void Section(string title, IReadOnlyList<DesktopSkill> items)
    {
        if (items.Count == 0) return;
        cards.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var skill in items) Card(skill);
    }
    private void Card(DesktopSkill skill)
    {
        var toggle = new ToggleSwitch { Name = "ChatSkillToggle", IsOn = draft.EnabledSkillIds.Contains(skill.Id), Tag = skill.Id,
            OnContent = "", OffContent = "", MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        ControlAppearance.Stock(toggle); ToolbarControls.Label(toggle, skill.Label);
        var expander = NativeSettingsSurface.Expander(cards, "ChatSkillCard", skill.Label, toggle, out var body);
        expander.Tag = skill.Id;
        toggle.Toggled += async (_, _) =>
        {
            draft.EnabledSkillIds.RemoveAll(id => id == skill.Id);
            if (toggle.IsOn) draft.EnabledSkillIds.Add(skill.Id);
            if (!toggle.IsOn || skill.Origin != "remote" || prefetch is null) return;
            try { await prefetch(skill.Id, lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch { if (!disposed) feedback.Text = DesktopResources.Get("SkillPrefetchFailed"); }
        };
        if (skill.Version is not null)
        { var version = new TextBlock { Text = skill.Version }; NativeCardSurface.Secondary(version, true); body.Children.Add(version); }
        var description = new TextBlock { Text = skill.Description, TextWrapping = TextWrapping.Wrap };
        NativeCardSurface.Secondary(description); body.Children.Add(description);
    }
    public void Dispose() { disposed = true; lifetime.Cancel(); }
}
