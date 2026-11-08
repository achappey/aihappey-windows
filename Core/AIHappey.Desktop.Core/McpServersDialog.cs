using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

internal sealed class McpServersDialog : ContentDialog, IResponsiveDialog
{
    private readonly DesktopMcpManager manager;
    private readonly CancellationTokenSource lifetime = new();
    private readonly StackPanel body = new() { Spacing = 12 };
    private readonly ScrollViewer viewer;
    private readonly TextBlock status = new() { Name = "McpManagementStatus", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox name = new() { Header = DesktopResources.Get("Name"), Name = "McpServerName", MaxLength = 256 };
    private readonly TextBox url = new() { Header = DesktopResources.Get("McpServerUrl"), Name = "McpServerUrl" };
    private readonly bool editorOnly;
    private DesktopMcpServer? draft;
    private McpHeadersEditor? headersEditor;
    private bool working, closed;
    private CancellationTokenSource? operation;
    private McpDialogState state = new();
    public bool CatalogRequested { get; private set; }
    public McpServersDialog(DesktopMcpManager manager, bool add = false, DesktopMcpServer? edit = null)
    {
        this.manager = manager; editorOnly = add || edit is not null;
        Name = "McpServersDialog";
        Resources["ContentDialogMaxWidth"] = 1000d; Resources["ContentDialogMinWidth"] = 0d;
        viewer = new ScrollViewer { Content = body, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = viewer;
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        foreach (var control in new Control[] { name, url }) ControlAppearance.Native(control);
        manager.Changed += ManagerChanged;
        PrimaryButtonClick += async (_, args) => { args.Cancel = true; await SaveAsync(); };
        SecondaryButtonClick += (_, args) =>
        {
            if (working || draft is not null) { args.Cancel = true; return; }
            CatalogRequested = true; state.FocusId = "SecondaryButton";
        };
        CloseButtonClick += (_, args) =>
        {
            if (working) { operation?.Cancel(); args.Cancel = true; }
            else if (draft is not null && !editorOnly)
            { args.Cancel = true; draft = null; status.Text = ""; Render(); DispatcherQueue.TryEnqueue(() => RestorePresentation()); }
        };
        Opened += (_, _) =>
        {
            SizeToRoot(); XamlRoot.Changed += RootChanged;
            DispatcherQueue.TryEnqueue(() => RestorePresentation());
        };
        Closing += (_, args) => { if (working && !closed) { operation?.Cancel(); args.Cancel = true; } };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); manager.Changed -= ManagerChanged; XamlRoot.Changed -= RootChanged; };
        if (edit is not null) Edit(edit.Clone()); else if (add) Edit(new()); else Render();
    }
    public void Shutdown() { closed = true; lifetime.Cancel(); Hide(); }
    public void RestoreState(McpDialogState value) { state = value; if (draft is null) Render(); }
    public McpDialogState CaptureState() { state.Offset = viewer.VerticalOffset; return state; }
    private void RestorePresentation()
    {
        if (closed) return;
        UpdateLayout(); viewer.ChangeView(null, state.Offset, null, true);
        if (state.FocusId is { } id)
            ControlAppearance.Descendants(this).OfType<Control>().FirstOrDefault(control =>
                AutomationProperties.GetAutomationId(control) == id || control.Name == id)?.Focus(FocusState.Programmatic);
    }
    private void ManagerChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (closed) return;
        if (working && manager.Servers.Any(s => s.State == McpConnectionState.SigningIn)) status.Text = DesktopResources.Get("McpSigningInHint");
        else if (draft is null && !working) Render();
    });
    private Button Button(string key, Action action)
    {
        var button = new Button { Name = key, Content = DesktopResources.Get(key) }; ControlAppearance.Native(button);
        button.Click += (_, _) => action(); return button;
    }
    private void Render()
    {
        Title = DesktopResources.Get("McpInstalledServers"); headersEditor = null; UpdateFooter();
        var offset = viewer.VerticalOffset;
        var focusedId = XamlRoot is null ? null : (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is Control focused
            ? AutomationProperties.GetAutomationId(focused) : null);
        body.Children.Clear(); status.Visibility = string.IsNullOrEmpty(status.Text) ? Visibility.Collapsed : Visibility.Visible; body.Children.Add(status);
        var servers = manager.Servers;
        if (servers.Count == 0) body.Children.Add(new TextBlock { Text = DesktopResources.Get("McpNoInstalledServers"), TextWrapping = TextWrapping.Wrap });
        foreach (var view in servers)
        {
            var server = view.Server;
            var canConnect = McpHeaderConfiguration.CanConnect(server);
            var toggle = new ToggleSwitch { Name = "McpServerEnabled", IsOn = server.Enabled,
                OnContent = "", OffContent = "", MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            toggle.IsEnabled = server.Enabled || canConnect;
            ControlAppearance.Stock(toggle); AutomationProperties.SetAutomationId(toggle, server.Id + ":Enabled");
            AutomationProperties.SetName(toggle, server.Name);
            toggle.Toggled += async (_, _) => await ActAsync(ct => manager.SetEnabledAsync(server.Id, toggle.IsOn, ct, interactive: true));
            var row = new McpServerRow(body, "McpInstalledServer", server.Id, server.Name, McpIcons.ForServer(view), toggle, state.Expanded);
            var panel = row.Details;
            row.Text(server.Description); row.Text(server.Version, small: true); row.Text(server.Url, selectable: true);
            var connectionState = new TextBlock { Name = "McpConnectionStatus", Text = DesktopResources.Get("McpState" + view.State), TextWrapping = TextWrapping.Wrap };
            NativeCardSurface.Secondary(connectionState, true); panel.Children.Add(connectionState);
            if (view.Error is not null) panel.Children.Add(new TextBlock { Text = view.Error, TextWrapping = TextWrapping.Wrap });
            var actions = row.Actions;
            row.Action("Edit", server.Id + ":Edit", DesktopResources.Format("ActionForItem", DesktopResources.Get("Edit"), server.Name),
                FluentIcons.Common.Icon.Edit, () => Edit(server.Clone()), server.PluginId is null);
            row.Action("McpRemove", server.Id + ":Remove", DesktopResources.Format("ActionForItem", DesktopResources.Get("McpRemove"), server.Name),
                FluentIcons.Common.Icon.Delete, async () => await ActAsync(ct => manager.RemoveAsync(server.Id, ct)), server.PluginId is null);
            if (server.Enabled && canConnect && view.State == McpConnectionState.AuthenticationRequired)
                actions.Children.Add(Button("McpSignIn", async () => await ActAsync(ct => manager.SignInAsync(server.Id, ct))));
            else if (view.State == McpConnectionState.SigningIn)
                actions.Children.Add(Button("Cancel", () => manager.CancelSignIn(server.Id)));
            else if (server.Enabled)
            {
                var retry = Button("Retry", async () => await ActAsync(ct => manager.SetEnabledAsync(server.Id, true, ct)));
                retry.IsEnabled = canConnect; actions.Children.Add(retry);
            }
            if (view.Discovery is { } discovery)
            {
                var tools = new StackPanel { Spacing = 12 };
                foreach (var tool in discovery.Tools)
                {
                    var details = new StackPanel { Spacing = 6 };
                    details.Children.Add(new TextBlock { Text = CatalogProjection.Text(tool, "description") ?? "", TextWrapping = TextWrapping.Wrap });
                    details.Children.Add(new TextBlock { Name = "McpToolSchema", Text = JsonSerializer.Serialize(tool, new JsonSerializerOptions { WriteIndented = true }),
                        TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") });
                    var toolView = new Expander { Header = CatalogProjection.Text(tool, "title") ?? CatalogProjection.Text(tool, "name"), Content = details,
                        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    ControlAppearance.Native(toolView); tools.Children.Add(toolView);
                }
                var toolsView = new Expander { Name = "McpDiscoveredTools", Header = DesktopResources.Format("McpToolsCount", discovery.Tools.Count), Content = tools,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                ControlAppearance.Native(toolsView); panel.Children.Add(toolsView);
            }
        }
        if (XamlRoot is not null) DispatcherQueue.TryEnqueue(() =>
        {
            if (closed || draft is not null) return;
            UpdateLayout(); viewer.ChangeView(null, offset, null, true);
            if (!string.IsNullOrEmpty(focusedId)) ControlAppearance.Descendants(body).OfType<Control>()
                .FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == focusedId)?.Focus(FocusState.Programmatic);
        });
    }
    private void Edit(DesktopMcpServer server)
    {
        state.Offset = viewer.VerticalOffset;
        state.FocusId = server.Id + ":Edit";
        draft = server; status.Text = ""; status.Visibility = Visibility.Collapsed; body.Children.Clear(); body.Children.Add(status);
        Title = DesktopResources.Get(manager.Servers.Any(view => view.Server.Id == server.Id) ? "McpEditServer" : "McpAddServer");
        UpdateFooter();
        body.Children.Add(name); body.Children.Add(url); name.Text = server.Name; url.Text = server.Url;
        headersEditor = new McpHeadersEditor(server); body.Children.Add(headersEditor);
    }
    private void UpdateFooter()
    {
        PrimaryButtonText = draft is null ? "" : DesktopResources.Get("Save");
        SecondaryButtonText = draft is null && !editorOnly ? DesktopResources.Get("McpCatalogs") : "";
        IsPrimaryButtonEnabled = !working;
        IsSecondaryButtonEnabled = !working;
        CloseButtonText = DesktopResources.Get(working || draft is not null ? "Cancel" : "Close");
        DefaultButton = draft is null ? ContentDialogButton.None : ContentDialogButton.Primary;
    }
    private async Task SaveAsync()
    {
        if (working || closed || draft is not { } server || headersEditor is null) return;
        try
        {
            server.Name = name.Text.Trim(); server.Url = url.Text.Trim();
            server.Headers = headersEditor.ReadHeaders();
            server.Validate();
        }
        catch (InvalidOperationException e) { status.Text = e.Message; status.Visibility = Visibility.Visible; return; }
        if (await ActAsync(ct => manager.InstallAsync(server, ct)) && !closed)
        {
            draft = null;
            if (editorOnly) Hide(); else { Render(); DispatcherQueue.TryEnqueue(() => RestorePresentation()); }
        }
    }
    private async Task<bool> ActAsync(Func<CancellationToken, Task> action)
    {
        if (working || closed) return false;
        using var currentOperation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation = currentOperation;
        var controls = ControlAppearance.Descendants(body).OfType<Control>().Select(control => (Control: control, control.IsEnabled)).ToArray();
        working = true; UpdateFooter(); status.Text = DesktopResources.Get("Working"); status.Visibility = Visibility.Visible;
        foreach (var (control, _) in controls) control.IsEnabled = false;
        try { await action(currentOperation.Token); status.Text = ""; return true; }
        catch (OperationCanceledException) { status.Text = DesktopResources.Get("OperationCanceled"); return false; }
        catch (Exception e) { status.Text = DesktopMcpManager.SafeError(e); return false; }
        finally
        {
            operation = null; working = false; UpdateFooter();
            if (!closed)
            {
                if (draft is null) Render();
                else
                {
                    status.Visibility = string.IsNullOrEmpty(status.Text) ? Visibility.Collapsed : Visibility.Visible;
                    foreach (var (control, enabled) in controls) control.IsEnabled = enabled;
                }
            }
        }
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot() { viewer.Width = Math.Max(0, Math.Min(800, XamlRoot.Size.Width - 96)); viewer.MaxHeight = Math.Max(0, Math.Min(650, XamlRoot.Size.Height - 240)); }
}
