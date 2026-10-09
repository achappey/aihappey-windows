using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>One native dialog for the server-grouped picker and argument page. No nested modal lifetimes.</summary>
public sealed class McpPromptsDialog : ContentDialog
{
    private readonly Pivot picker = new() { Name = "McpPromptServers" };
    private readonly StackPanel fields = new() { Name = "McpPromptArguments", Spacing = 12 };
    private readonly ScrollViewer form = new() { MaxHeight = 420, Visibility = Visibility.Collapsed,
        HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock empty = new() { Text = DesktopResources.Get("McpNoPrompts"), TextWrapping = TextWrapping.Wrap };
    private readonly ProgressRing progress = new() { Name = "McpPromptPending", Width = 24, Height = 24, Visibility = Visibility.Collapsed };
    private readonly InfoBar error = new() { Name = "McpPromptError", Severity = InfoBarSeverity.Error, IsClosable = false };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, ContentControl> holders = new(StringComparer.Ordinal);
    private IReadOnlyList<McpPromptEntry> catalog = [];
    private McpPromptEntry? selected;
    private McpPromptArgumentsState? state;
    private CancellationTokenSource? retrieval;
    private bool pending;
    private bool closed;
    private bool rendering;
    public McpSelectedPrompt? Selection { get; private set; }

    public McpPromptsDialog(IReadOnlyList<McpPromptEntry> entries)
    {
        Name = "McpPromptsDialog"; Title = DesktopResources.Get("McpPrompts");
        CloseButtonText = DesktopResources.Get("Close"); DefaultButton = ContentDialogButton.None;
        var panel = new StackPanel { Spacing = 12, MinWidth = 240, MaxWidth = 600 };
        form.Content = fields;
        panel.Children.Add(picker); panel.Children.Add(empty); panel.Children.Add(form);
        panel.Children.Add(progress); panel.Children.Add(error); Content = panel;
        ToolbarControls.Label(picker, DesktopResources.Get("McpPrompts"));
        AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (pending || closed || selected is null || state?.MissingRequired == true) return;
            var deferral = args.GetDeferral();
            try { await ResolveAsync(selected, state?.Values ?? new Dictionary<string, string>()); }
            finally { deferral.Complete(); }
        };
        CloseButtonClick += (_, args) =>
        {
            if (selected is null || pending) return;
            args.Cancel = true; ShowPicker(); picker.Focus(FocusState.Programmatic);
        };
        Closing += (_, _) =>
        {
            closed = true; lifetime.Cancel(); retrieval?.Cancel(); state?.Dispose(); state = null;
        };
        Opened += (_, _) => picker.Focus(FocusState.Programmatic);
        SetCatalog(entries);
    }

    public void SetCatalog(IReadOnlyList<McpPromptEntry> entries)
    {
        if (closed) return;
        catalog = entries;
        var activeServer = (picker.SelectedItem as PivotItem)?.Tag as string;
        picker.Items.Clear();
        foreach (var group in entries.GroupBy(e => e.ServerId))
        {
            var list = new StackPanel { Name = "McpPromptList", Spacing = 8 };
            foreach (var entry in group)
            {
                var button = new Button { Name = "SelectMcpPrompt", Content = Details(entry), HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12), Tag = entry };
                ControlAppearance.Native(button); ToolbarControls.Label(button, entry.Title + " · " + entry.ServerName);
                button.Click += async (_, _) => await SelectAsync(entry);
                list.Children.Add(button);
            }
            picker.Items.Add(new PivotItem { Header = group.First().ServerName, Tag = group.Key,
                Content = new ScrollViewer { Content = list, MaxHeight = 420, HorizontalScrollMode = ScrollMode.Disabled,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } });
        }
        picker.SelectedItem = picker.Items.OfType<PivotItem>().FirstOrDefault(item => item.Tag as string == activeServer)
            ?? picker.Items.FirstOrDefault();
        empty.Visibility = selected is null && catalog.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selected is { } previous && !previous.IsCurrent())
        {
            retrieval?.Cancel(); ShowPicker(); error.Message = DesktopResources.Get("McpDisconnected"); error.IsOpen = true;
        }
        picker.IsEnabled = !pending;
    }

    private static UIElement Details(McpPromptEntry entry)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new McpServerIcon(McpIcons.Read(entry.Prompt), 24));
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock { Text = entry.Title, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (entry.Description.Length > 0) text.Children.Add(new TextBlock { Text = entry.Description, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1); row.Children.Add(text); return row;
    }

    private async Task SelectAsync(McpPromptEntry entry)
    {
        if (closed || pending) return;
        error.IsOpen = false;
        try
        {
            if (!entry.IsCurrent()) throw new InvalidOperationException();
            if (entry.Arguments.Count == 0) { await ResolveAsync(entry, new Dictionary<string, string>()); return; }
            selected = entry; state?.Dispose(); state = new(entry); holders.Clear(); fields.Children.Clear();
            Title = entry.Title; picker.Visibility = empty.Visibility = Visibility.Collapsed; form.Visibility = Visibility.Visible;
            PrimaryButtonText = DesktopResources.Get("McpExecute"); CloseButtonText = DesktopResources.Get("Cancel");
            DefaultButton = ContentDialogButton.Primary;
            fields.Children.Add(Details(entry));
            foreach (var argument in state.Arguments)
            {
                var holder = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };
                holders.Add(argument.Name, holder); fields.Children.Add(holder);
                if (argument.Description.Length > 0) fields.Children.Add(new TextBlock { Text = argument.Description, TextWrapping = TextWrapping.Wrap });
            }
            state.Changed += RenderArguments;
            RenderArguments();
            (holders.Values.FirstOrDefault()?.Content as Control)?.Focus(FocusState.Programmatic);
            await state.InitializeAsync();
        }
        catch (Exception) { if (!closed) { error.Message = DesktopResources.Get("McpPromptFailed"); error.IsOpen = true; } }
    }

    private void ShowPicker()
    {
        state?.Dispose(); state = null; selected = null; holders.Clear(); fields.Children.Clear();
        form.Visibility = Visibility.Collapsed; picker.Visibility = Visibility.Visible;
        empty.Visibility = catalog.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Title = DesktopResources.Get("McpPrompts"); PrimaryButtonText = ""; CloseButtonText = DesktopResources.Get("Close");
        DefaultButton = ContentDialogButton.None; error.IsOpen = false;
    }

    private void RenderArguments()
    {
        if (closed || state is null || rendering) return;
        rendering = true;
        try
        {
            var current = state;
            foreach (var argument in current.Arguments)
            {
                var holder = holders[argument.Name]; var value = current.Values[argument.Name];
                var label = argument.Name + (argument.Required ? " *" : "");
                var hasSuggestions = current.Suggestions.TryGetValue(argument.Name, out var suggestions);
                var old = holder.Content as Control;
                if (hasSuggestions && old is not AutoSuggestBox)
                {
                    var input = new AutoSuggestBox { Name = "McpPromptArgumentCompletion", Header = label, Text = value,
                        QueryIcon = new FontIcon { Glyph = "\uE70D", FontSize = 12 } };
                    input.TextChanged += async (_, args) =>
                    {
                        if (!rendering && !pending && args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                            await current.ChangeAsync(argument.Name, input.Text);
                    };
                    input.SuggestionChosen += async (_, args) =>
                    {
                        if (pending) return;
                        input.Text = args.SelectedItem as string ?? ""; await current.ChangeAsync(argument.Name, input.Text);
                    };
                    input.QuerySubmitted += (_, args) =>
                    {
                        if (args.ChosenSuggestion is null) input.IsSuggestionListOpen = true;
                    };
                    input.GotFocus += (_, _) => input.IsSuggestionListOpen = true;
                    PrepareField(input, argument); holder.Content = input;
                    if (old?.FocusState != FocusState.Unfocused) input.Focus(FocusState.Programmatic);
                }
                else if (!hasSuggestions && old is null)
                {
                    var input = new TextBox { Name = "McpPromptArgument", Header = label, Text = value };
                    input.TextChanged += async (_, _) => { if (!rendering && !pending) await current.ChangeAsync(argument.Name, input.Text); };
                    PrepareField(input, argument); holder.Content = input;
                }
                if (holder.Content is AutoSuggestBox select)
                {
                    if (select.Text != value) select.Text = value;
                    select.ItemsSource = suggestions; select.IsEnabled = !pending;
                }
                else if (holder.Content is TextBox input) { if (input.Text != value) input.Text = value; input.IsEnabled = !pending; }
            }
            IsPrimaryButtonEnabled = !pending && !current.MissingRequired;
        }
        finally { rendering = false; }
    }

    private static void PrepareField(Control input, McpPromptArgument argument)
    {
        ControlAppearance.Native(input); ToolbarControls.Label(input, argument.Name);
        AutomationProperties.SetAutomationId(input, "McpPromptArgument:" + argument.Name);
        AutomationProperties.SetHelpText(input, argument.Description + (argument.Required ? " · " + DesktopResources.Get("McpPromptRequired") : ""));
    }

    private async Task ResolveAsync(McpPromptEntry entry, IReadOnlyDictionary<string, string> arguments)
    {
        if (closed || pending || DesktopMcpPrompts.MissingRequired(entry.Arguments, arguments)) return;
        selected = entry; pending = true; picker.IsEnabled = false; error.IsOpen = false;
        progress.IsActive = true; progress.Visibility = Visibility.Visible; IsPrimaryButtonEnabled = false; RenderArguments();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        retrieval = request;
        try
        {
            if (!entry.IsCurrent()) throw new InvalidOperationException();
            var result = await entry.Get(entry.Name, arguments.ToDictionary(p => p.Key, p => p.Value), request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (closed || !entry.IsCurrent() || selected != entry) return;
            var parts = DesktopMcpPrompts.MessageParts(result);
            if (parts.Count == 0) throw new InvalidOperationException();
            Selection = new(entry, parts); Hide();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception) { if (!closed) { error.Message = DesktopResources.Get("McpPromptFailed"); error.IsOpen = true; } }
        finally
        {
            retrieval = null; pending = false;
            if (!closed)
            {
                picker.IsEnabled = true; progress.IsActive = false; progress.Visibility = Visibility.Collapsed;
                if (state is null) selected = null;
                RenderArguments();
            }
        }
    }
}
