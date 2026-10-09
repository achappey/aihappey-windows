using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace AIHappey.Desktop.Core;

/// <summary>Native equivalent of the combined resource picker and template arguments modal.
/// A single ContentDialog avoids nested WinUI dialog lifetimes. Cancel on the argument page returns to the picker.</summary>
public sealed class McpResourcesDialog : ContentDialog
{
    private readonly StackPanel panel = new() { Spacing = 12, MinWidth = 240, MaxWidth = 600 };
    private readonly ListView list = new() { Name = "McpResourceList", IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None,
        MaxHeight = 420, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel fields = new() { Name = "McpResourceArguments", Spacing = 12, Visibility = Visibility.Collapsed };
    private readonly TextBlock empty = new() { Text = DesktopResources.Get("McpNoResources"), TextWrapping = TextWrapping.Wrap };
    private readonly ProgressRing progress = new() { Name = "McpResourceReading", Width = 24, Height = 24, Visibility = Visibility.Collapsed };
    private readonly InfoBar error = new() { Name = "McpResourceError", Severity = InfoBarSeverity.Error, IsClosable = false };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> completions = new(StringComparer.Ordinal);
    private IReadOnlyList<McpResourceEntry> catalog = [];
    private McpResourceEntry? template;
    private bool pending;
    private bool closed;
    private int pageVersion;
    public McpSelectedResource? Selection { get; private set; }

    public McpResourcesDialog(IReadOnlyList<McpResourceEntry> entries)
    {
        Name = "McpResourcesDialog";
        Title = DesktopResources.Get("McpResources"); CloseButtonText = DesktopResources.Get("Close");
        DefaultButton = ContentDialogButton.None;
        // Fixed native markup, never constructed from server-provided strings.
        list.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <ContentControl Margin="4,8" HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch" IsTabStop="False" />
            </DataTemplate>
            """);
        list.ContainerContentChanging += (_, args) =>
        {
            if (args.ItemContainer.ContentTemplateRoot is not ContentControl row) return;
            row.Content = !args.InRecycleQueue && args.Item is McpResourceEntry entry ? McpResourceView.Details(entry, true) : null;
            args.Handled = true;
        };
        ControlAppearance.Native(list);
        ToolbarControls.Label(list, DesktopResources.Get("McpResources"));
        panel.Children.Add(list); panel.Children.Add(empty);
        panel.Children.Add(new ScrollViewer { Content = fields, MaxHeight = 420,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled });
        panel.Children.Add(progress); panel.Children.Add(error); Content = panel;
        AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        list.ItemClick += async (_, args) =>
        {
            if (pending || closed || args.ClickedItem is not McpResourceEntry entry) return;
            if (entry.IsTemplate && DesktopMcpResources.TemplateArguments(entry.Uri).Count > 0) ShowArguments(entry);
            else await ReadAsync(entry, entry.IsTemplate ? DesktopMcpResources.ExpandTemplate(entry.Uri, new Dictionary<string, string>()) : entry.Uri);
        };
        PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (pending || closed || template is not { } entry) return;
            var deferral = args.GetDeferral();
            try { await ReadAsync(entry, DesktopMcpResources.ExpandTemplate(entry.Uri, values)); }
            finally { deferral.Complete(); }
        };
        CloseButtonClick += (_, args) =>
        {
            if (template is null || pending) return;
            args.Cancel = true; ShowPicker(); list.Focus(FocusState.Programmatic);
        };
        Closing += (_, _) => { closed = true; pageVersion++; lifetime.Cancel(); CancelCompletions(); };
        Opened += (_, _) => list.Focus(FocusState.Programmatic);
        SetCatalog(entries);
    }

    public void SetCatalog(IReadOnlyList<McpResourceEntry> entries)
    {
        if (closed) return;
        catalog = entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.ServerName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        list.ItemsSource = catalog;
        empty.Visibility = template is null && catalog.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (template is { } selected && !catalog.Any(e => e.ServerId == selected.ServerId && e.Uri == selected.Uri && e.IsTemplate))
        {
            ShowPicker(); error.Message = DesktopResources.Get("McpDisconnected"); error.IsOpen = true;
        }
    }

    private void ShowPicker()
    {
        pageVersion++; CancelCompletions(); template = null; values.Clear(); fields.Children.Clear();
        fields.Visibility = Visibility.Collapsed; list.Visibility = Visibility.Visible;
        empty.Visibility = catalog.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Title = DesktopResources.Get("McpResources"); PrimaryButtonText = "";
        CloseButtonText = DesktopResources.Get("Close"); DefaultButton = ContentDialogButton.None; error.IsOpen = false;
    }

    private void ShowArguments(McpResourceEntry entry)
    {
        pageVersion++; CancelCompletions(); template = entry; values.Clear(); fields.Children.Clear(); error.IsOpen = false;
        list.Visibility = empty.Visibility = Visibility.Collapsed; fields.Visibility = Visibility.Visible;
        Title = entry.Name; PrimaryButtonText = DesktopResources.Get("McpExecute"); IsPrimaryButtonEnabled = true;
        CloseButtonText = DesktopResources.Get("Cancel"); DefaultButton = ContentDialogButton.Primary;
        fields.Children.Add(McpResourceView.Details(entry, false));
        foreach (var name in DesktopMcpResources.TemplateArguments(entry.Uri))
        {
            values[name] = "";
            if (entry.Complete is null)
            {
                var input = new TextBox { Name = "McpTemplateArgument", Header = name };
                input.TextChanged += (_, _) => values[name] = input.Text;
                ControlAppearance.Native(input); ToolbarControls.Label(input, name); fields.Children.Add(input);
            }
            else
            {
                var input = new AutoSuggestBox { Name = "McpTemplateArgumentCompletion", Header = name };
                input.TextChanged += async (_, args) =>
                {
                    if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
                    values[name] = input.Text;
                    await CompleteAsync(entry, name, input.Text, input, delay: true);
                };
                input.SuggestionChosen += (_, args) => { input.Text = args.SelectedItem as string ?? ""; values[name] = input.Text; };
                ControlAppearance.Native(input); ToolbarControls.Label(input, name); fields.Children.Add(input);
                _ = CompleteAsync(entry, name, "", input, delay: false);
            }
        }
        DispatcherQueue.TryEnqueue(() => fields.Children.OfType<Control>().FirstOrDefault()?.Focus(FocusState.Programmatic));
    }

    private async Task CompleteAsync(McpResourceEntry entry, string name, string value, AutoSuggestBox input, bool delay)
    {
        if (closed || entry.Complete is null) return;
        if (completions.Remove(name, out var previous)) previous.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        completions[name] = request;
        var version = pageVersion;
        try
        {
            if (delay) await Task.Delay(500, request.Token);
            var context = values.Where(p => p.Key != name && !string.IsNullOrWhiteSpace(p.Value)).ToDictionary(p => p.Key, p => p.Value);
            var suggestions = await entry.Complete(entry.Uri, name, value, context, request.Token);
            if (closed || request.IsCancellationRequested || version != pageVersion || input.Text != value) return;
            input.ItemsSource = suggestions.Take(100).ToArray();
        }
        catch (Exception) { /* Completion failure must not prevent ordinary text entry or leak server errors. */ }
        finally { if (completions.TryGetValue(name, out var current) && current == request) completions.Remove(name); }
    }

    private void CancelCompletions()
    {
        foreach (var request in completions.Values) request.Cancel();
        completions.Clear();
    }

    private async Task ReadAsync(McpResourceEntry entry, string uri)
    {
        if (closed || pending) return;
        pending = true; error.IsOpen = false; progress.IsActive = true; progress.Visibility = Visibility.Visible;
        list.IsEnabled = IsPrimaryButtonEnabled = false;
        foreach (var control in fields.Children.OfType<Control>()) control.IsEnabled = false;
        var version = pageVersion;
        try
        {
            DesktopMcpResources.ValidateUri(uri);
            var result = await entry.Read(uri, null, DesktopMcpResources.DefaultLimit, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            if (closed || version != pageVersion) return;
            var selected = new McpSelectedResource(entry.ServerId, uri, entry.Name, result.Clone());
            if (selected.Parts().Count == 0) throw new InvalidOperationException(DesktopResources.Get("McpEmptyResource"));
            Selection = selected; Hide();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!closed && version == pageVersion) { error.Message = DesktopResources.Get("McpResourceReadFailed"); error.IsOpen = true; }
        }
        finally
        {
            pending = false;
            if (!closed)
            {
                list.IsEnabled = true; IsPrimaryButtonEnabled = template is not null;
                foreach (var control in fields.Children.OfType<Control>()) control.IsEnabled = true;
                progress.IsActive = false; progress.Visibility = Visibility.Collapsed;
            }
        }
    }
}
