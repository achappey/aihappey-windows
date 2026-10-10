using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AIHappey.Desktop.Core;

/// <summary>Native body-only skill editor. Optional frontmatter and binary resources survive editing.</summary>
public sealed class SkillEditDialog : ContentDialog, IResponsiveDialog
{
    private DesktopSkillDraft draft;
    private readonly Grid layout = new() { RowSpacing = 16 };
    private readonly NavigationView tabs = new() { Name = "SkillEditorTabs", PaneDisplayMode = NavigationViewPaneDisplayMode.Top,
        IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsPaneToggleButtonVisible = false, AlwaysShowHeader = false, Height = 56 };
    private readonly ScrollViewer page = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock feedback = new() { Name = "SkillEditorFeedback", TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, FrameworkElement> pages = [];
    private readonly StackPanel files = new() { Name = "SkillEditorFiles", Spacing = 12 };
    private readonly Button chooseFiles = new() { Name = "SkillChooseFiles", Content = DesktopResources.Get("SkillChooseFiles") };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<bool> isCurrent;
    private readonly bool editing;
    private bool working;
    private bool saving;
    private bool fileRenderQueued;
    public DesktopSkillDraft? Result { get; private set; }
    public DesktopSkillDraft Draft => draft.Clone();
    public Func<DesktopSkillDraft, CancellationToken, Task>? SaveAsync { get; set; }

    public SkillEditDialog(DesktopSkillDraft value, bool isEditing, Func<bool>? isCurrent = null)
    {
        draft = value.Clone(); editing = isEditing; this.isCurrent = isCurrent ?? (() => true);
        Name = "SkillEditDialog"; Title = DesktopResources.Get(editing ? "SkillEdit" : "SkillCreate");
        Resources["ContentDialogMaxWidth"] = 920d; Resources["ContentDialogMinWidth"] = 0d;
        PrimaryButtonText = DesktopResources.Get("Save"); CloseButtonText = DesktopResources.Get("Cancel"); DefaultButton = ContentDialogButton.Primary;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(tabs); Grid.SetRow(feedback, 1); layout.Children.Add(feedback); Grid.SetRow(page, 2); layout.Children.Add(page); Content = layout;
        AutomationProperties.SetLiveSetting(feedback, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var general = Panel(); NativeSettingsSurface.Card(general, "SkillGeneralCard", DesktopResources.Get("General"), out var body);
        var name = Text(body, "SkillName", draft.Name, false, "SkillNameHint"); name.IsEnabled = !editing;
        name.TextChanged += (_, _) => { draft.Name = editing ? value.Name : DesktopSkillPackages.NormalizeName(name.Text); UpdateSave(true); };
        name.LostFocus += (_, _) => { if (!editing) name.Text = draft.Name; };
        var description = Text(body, "SkillDescription", draft.Description, true, "SkillDescriptionHint");
        description.TextChanged += (_, _) => { draft.Description = description.Text.Trim(); UpdateSave(true); };
        var instructions = Panel(); NativeSettingsSurface.Card(instructions, "SkillInstructionsCard", "SKILL.md", out body);
        var content = Text(body, "SkillInstructions", draft.Instructions, true, "SkillInstructionsHint"); content.MinHeight = 320;
        content.TextChanged += (_, _) => { draft.Instructions = content.Text; UpdateSave(); };
        var resources = Panel(); resources.AllowDrop = true;
        resources.Children.Add(new TextBlock { Text = DesktopResources.Get("SkillFilesHint"), TextWrapping = TextWrapping.Wrap });
        var choose = chooseFiles;
        ControlAppearance.Stock(choose); resources.Children.Add(choose); resources.Children.Add(files);
        choose.Click += async (_, _) => await WorkAsync(async ct =>
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*"); Initialize(picker);
            var selected = await picker.PickMultipleFilesAsync(); await AddFilesAsync(selected, ct);
        });
        resources.DragOver += (_, args) =>
        {
            args.Handled = true; args.AcceptedOperation = !working && !saving && args.DataView.Contains(StandardDataFormats.StorageItems)
                && args.AllowedOperations.HasFlag(DataPackageOperation.Copy) ? DataPackageOperation.Copy : DataPackageOperation.None;
        };
        resources.Drop += async (_, args) =>
        {
            args.Handled = true;
            if (working || saving || !args.DataView.Contains(StandardDataFormats.StorageItems)
                || !args.AllowedOperations.HasFlag(DataPackageOperation.Copy)) { args.AcceptedOperation = DataPackageOperation.None; return; }
            var deferral = args.GetDeferral();
            try { await WorkAsync(async ct => await AddFilesAsync((await args.DataView.GetStorageItemsAsync().AsTask(ct)).OfType<StorageFile>(), ct)); }
            finally { deferral.Complete(); }
        };
        AddTab("general", DesktopResources.Get("General"), general);
        AddTab("content", "SKILL.md", instructions); AddTab("files", DesktopResources.Get("SkillFiles"), resources);
        tabs.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is NavigationViewItem { Tag: string key }) { page.Content = pages[key]; SizePage(); page.ChangeView(0, 0, null, true); }
        };
        tabs.SelectedItem = tabs.MenuItems[0]; page.Content = general; page.SizeChanged += (_, _) => SizePage();
        PrimaryButtonClick += Save;
        Closing += (_, args) => { if ((working || saving) && !lifetime.IsCancellationRequested) args.Cancel = true; };
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; name.Focus(FocusState.Programmatic); };
        Closed += (_, _) => { lifetime.Cancel(); if (XamlRoot is not null) XamlRoot.Changed -= RootChanged; };
        RenderFiles(); UpdateSave();
    }
    public void CancelAndHide() { lifetime.Cancel(); Hide(); }
    private static StackPanel Panel() => new() { Spacing = 16 };
    private static TextBox Text(Panel parent, string key, string value, bool multiline, string hint)
    {
        var box = new TextBox { Name = key, Header = DesktopResources.Get(key), Text = value, AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 120 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = DesktopResources.Get(hint) };
        ControlAppearance.Stock(box); ToolbarControls.Label(box, DesktopResources.Get(key)); parent.Children.Add(box); return box;
    }
    private void AddTab(string key, string title, FrameworkElement content)
    {
        pages[key] = content; var tab = new NavigationViewItem { Content = title, Tag = key };
        AutomationProperties.SetAutomationId(tab, "SkillTab_" + key); tabs.MenuItems.Add(tab);
    }
    private void Initialize(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker,
        Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
    private void CheckCurrent(CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (!isCurrent()) throw new OperationCanceledException(ct); }
    private void Message(string text) { feedback.Text = text; feedback.Visibility = Visibility.Visible; }
    private void UpdateSave(bool validate = false)
    {
        try { DesktopSkillPackages.Validate(draft); IsPrimaryButtonEnabled = !working && !saving; if (validate) feedback.Visibility = Visibility.Collapsed; }
        catch (InvalidDataException error) { IsPrimaryButtonEnabled = false; if (validate) Message(error.Message); }
    }
    private async Task WorkAsync(Func<CancellationToken, Task> action)
    {
        if (working || saving) return; working = true; UpdateSave(); page.IsEnabled = false;
        try { CheckCurrent(lifetime.Token); await action(lifetime.Token); CheckCurrent(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Message(error is InvalidDataException ? error.Message : DesktopResources.Get("ChatSettingsSaveFailed")); }
        finally { working = false; page.IsEnabled = true; RenderFiles(); UpdateSave(); }
    }
    private async Task AddFilesAsync(IEnumerable<StorageFile> selected, CancellationToken ct)
    {
        var next = draft.Clone();
        foreach (var file in selected)
        {
            CheckCurrent(ct); var path = DesktopSkillPackages.ResourcePath(file.Name);
            if ((await file.GetBasicPropertiesAsync()).Size > SkillFiles.MaxBytes) throw new InvalidDataException(DesktopResources.Get("SkillPackageTooLarge"));
            await using var input = await file.OpenStreamForReadAsync(); using var content = new StreamContent(input);
            var data = await DesktopCatalogClient.ReadBoundedAsync(content, SkillFiles.MaxBytes, ct);
            next.Files.RemoveAll(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)); next.Files.Add(new(path, data));
            // Validate resource bounds even before required General fields have been filled in.
            var validation = next.Clone(); validation.Name = "validation"; validation.Description = "validation"; DesktopSkillPackages.Validate(validation);
        }
        CheckCurrent(ct); draft.Files = next.Files;
    }
    private void RenderFiles()
    {
        // Do not remove the clicked/focused control while WinUI is dispatching its native event.
        if (fileRenderQueued || lifetime.IsCancellationRequested) return;
        fileRenderQueued = true;
        DispatcherQueue.TryEnqueue(() => { fileRenderQueued = false; if (!lifetime.IsCancellationRequested) RenderFilesCore(); });
    }
    private void RenderFilesCore()
    {
        if (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused
            && ControlAppearance.Descendants(files).Contains(focused)) chooseFiles.Focus(FocusState.Programmatic);
        files.Children.Clear();
        if (draft.Files.Count == 0) files.Children.Add(new TextBlock { Text = DesktopResources.Get("NoResults") });
        foreach (var file in draft.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            NativeSettingsSurface.Card(files, "SkillFileCard", file.Path, out var body);
            var path = new TextBox { Name = "SkillFilePath", Tag = file.Path, Header = DesktopResources.Get("SkillRelativePath"), Text = file.Path };
            ControlAppearance.Stock(path); ToolbarControls.Label(path, DesktopResources.Get("SkillRelativePath")); body.Children.Add(path);
            var size = new TextBlock { Text = $"{file.Data.Length:N0} B" }; NativeCardSurface.Secondary(size); body.Children.Add(size);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; body.Children.Add(actions);
            Button Action(string name, string key)
            {
                var button = new Button { Name = name, Tag = file.Path, Content = DesktopResources.Get(key) };
                ControlAppearance.Stock(button); ToolbarControls.Label(button, DesktopResources.Format("ActionForItem", DesktopResources.Get(key), file.Path)); actions.Children.Add(button); return button;
            }
            Action("SkillFileRename", "SkillApplyPath").Click += (_, _) =>
            {
                try
                {
                    var relative = DesktopSkillPackages.ResourcePath(path.Text);
                    var next = draft.Clone(); var index = next.Files.FindIndex(f => f.Path == file.Path); next.Files[index] = new(relative, file.Data);
                    var validation = next.Clone(); validation.Name = "validation"; validation.Description = "validation"; DesktopSkillPackages.Validate(validation);
                    draft.Files = next.Files; RenderFiles(); UpdateSave();
                }
                catch (InvalidDataException error) { Message(error.Message); }
            };
            var download = Action("SkillFileDownload", "Download"); download.Content = DesktopIcons.Create(Icon.ArrowDownload, 16);
            download.Width = 36; download.Padding = new Thickness(0);
            download.Click += async (_, _) => await WorkAsync(async ct =>
            {
                var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(file.Path) };
                var extension = Path.GetExtension(file.Path); picker.FileTypeChoices.Add(DesktopResources.Get("SkillFiles"), new List<string> { extension.Length > 0 ? extension : ".bin" }); Initialize(picker);
                var destination = await picker.PickSaveFileAsync(); if (destination is null) return; CheckCurrent(ct);
                await using var output = await destination.OpenStreamForWriteAsync(); output.SetLength(0); await output.WriteAsync(file.Data, ct);
            });
            var remove = Action("SkillFileRemove", "Delete"); remove.Content = DesktopIcons.Create(Icon.Delete, 16);
            remove.Width = 36; remove.Padding = new Thickness(0);
            remove.Click += (_, _) => { draft.Files.RemoveAll(f => f.Path == file.Path); RenderFiles(); UpdateSave(); };
        }
        SizePage();
    }
    private async void Save(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; if (working || saving) return;
        var deferral = args.GetDeferral(); saving = true; UpdateSave(); page.IsEnabled = tabs.IsEnabled = false;
        try
        {
            DesktopSkillPackages.Validate(draft); CheckCurrent(lifetime.Token);
            if (SaveAsync is not null) await SaveAsync(draft.Clone(), lifetime.Token);
            CheckCurrent(lifetime.Token); Result = draft.Clone(); args.Cancel = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Message(error is InvalidDataException ? error.Message : DesktopResources.Get("ChatSettingsSaveFailed")); }
        finally { saving = false; page.IsEnabled = tabs.IsEnabled = true; UpdateSave(); deferral.Complete(); }
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot()
    {
        if (XamlRoot is null) return;
        layout.Width = Math.Max(0, Math.Min(820, XamlRoot.Size.Width - 96));
        layout.Height = Math.Max(0, Math.Min(680, XamlRoot.Size.Height - 240)); SizePage();
    }
    private void SizePage()
    {
        if (page.Content is FrameworkElement content && page.ActualWidth > 0)
        { content.Width = Math.Max(0, page.ActualWidth - 16); content.HorizontalAlignment = HorizontalAlignment.Left; }
    }
}
