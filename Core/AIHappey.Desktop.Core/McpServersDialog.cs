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
        CloseButtonClick += (_, args) =>
        {
            if (working) { operation?.Cancel(); args.Cancel = true; }
            else if (draft is not null && !editorOnly)
            { args.Cancel = true; draft = null; status.Text = ""; Render(); }
        };
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; };
        Closing += (_, args) => { if (working && !closed) { operation?.Cancel(); args.Cancel = true; } };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); manager.Changed -= ManagerChanged; XamlRoot.Changed -= RootChanged; };
        if (edit is not null) Edit(edit.Clone()); else if (add) Edit(new()); else Render();
    }
    public void Shutdown() { closed = true; lifetime.Cancel(); Hide(); }
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
        body.Children.Clear(); body.Children.Add(status);
        body.Children.Add(new TextBlock { Text = DesktopResources.Get("McpEnableHint"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Button("McpAddServer", () => Edit(new())));
        var servers = manager.Servers;
        if (servers.Count == 0) body.Children.Add(new TextBlock { Text = DesktopResources.Get("McpNoInstalledServers"), TextWrapping = TextWrapping.Wrap });
        foreach (var view in servers)
        {
            var server = view.Server;
            var canConnect = McpHeaderConfiguration.CanConnect(server);
            var panel = new StackPanel { Spacing = 8 };
            var header = new Grid { ColumnSpacing = 12 };
            header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = server.Name, FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center });
            var toggle = new ToggleSwitch { Name = "McpServerEnabled", IsOn = server.Enabled,
                OnContent = "", OffContent = "", MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            toggle.IsEnabled = server.Enabled || canConnect;
            ControlAppearance.Stock(toggle); AutomationProperties.SetAutomationId(toggle, server.Id + ":Enabled");
            AutomationProperties.SetName(toggle, server.Name);
            toggle.Toggled += async (_, _) => await ActAsync(ct => manager.SetEnabledAsync(server.Id, toggle.IsOn, ct, interactive: true));
            Grid.SetColumn(toggle, 1); header.Children.Add(toggle); panel.Children.Add(header);
            var address = new TextBlock { Text = server.Url, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            NativeCardSurface.Secondary(address); panel.Children.Add(address);
            var state = new TextBlock { Name = "McpConnectionStatus", Text = DesktopResources.Get("McpState" + view.State), TextWrapping = TextWrapping.Wrap };
            NativeCardSurface.Secondary(state, true); panel.Children.Add(state);
            if (view.Error is not null) panel.Children.Add(new TextBlock { Text = view.Error, TextWrapping = TextWrapping.Wrap });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(Button("Edit", () => Edit(server.Clone())));
            actions.Children.Add(Button("McpRemove", async () => await ActAsync(ct => manager.RemoveAsync(server.Id, ct))));
            if (server.Enabled && canConnect && view.State == McpConnectionState.AuthenticationRequired)
                actions.Children.Add(Button("McpSignIn", async () => await ActAsync(ct => manager.SignInAsync(server.Id, ct))));
            else if (view.State == McpConnectionState.SigningIn)
                actions.Children.Add(Button("Cancel", () => manager.CancelSignIn(server.Id)));
            else if (server.Enabled)
            {
                var retry = Button("Retry", async () => await ActAsync(ct => manager.SetEnabledAsync(server.Id, true, ct)));
                retry.IsEnabled = canConnect; actions.Children.Add(retry);
            }
            panel.Children.Add(actions);
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
            var card = new Border { Name = "McpInstalledServer", Child = panel };
            NativeCardSurface.Card(card, true); body.Children.Add(card);
        }
    }
    private void Edit(DesktopMcpServer server)
    {
        draft = server; status.Text = ""; body.Children.Clear(); body.Children.Add(status);
        Title = DesktopResources.Get(manager.Servers.Any(view => view.Server.Id == server.Id) ? "McpEditServer" : "McpAddServer");
        UpdateFooter();
        body.Children.Add(name); body.Children.Add(url); name.Text = server.Name; url.Text = server.Url;
        headersEditor = new McpHeadersEditor(server); body.Children.Add(headersEditor);
    }
    private void UpdateFooter()
    {
        PrimaryButtonText = draft is null ? "" : DesktopResources.Get("Save");
        IsPrimaryButtonEnabled = !working;
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
        catch (InvalidOperationException e) { status.Text = e.Message; return; }
        if (await ActAsync(ct => manager.InstallAsync(server, ct)) && !closed)
        {
            draft = null;
            if (editorOnly) Hide(); else Render();
        }
    }
    private async Task<bool> ActAsync(Func<CancellationToken, Task> action)
    {
        if (working || closed) return false;
        using var currentOperation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation = currentOperation;
        var controls = ControlAppearance.Descendants(body).OfType<Control>().Select(control => (Control: control, control.IsEnabled)).ToArray();
        working = true; UpdateFooter(); status.Text = DesktopResources.Get("Working");
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
                else foreach (var (control, enabled) in controls) control.IsEnabled = enabled;
            }
        }
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot() { viewer.Width = Math.Max(0, Math.Min(800, XamlRoot.Size.Width - 96)); viewer.Height = Math.Max(0, Math.Min(650, XamlRoot.Size.Height - 240)); }
}
