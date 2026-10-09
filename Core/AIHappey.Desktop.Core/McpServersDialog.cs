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
    private readonly StackPanel headerRows = new() { Spacing = 8 };
    private readonly List<(TextBox Key, PasswordBox Value)> headers = [];
    private DesktopMcpServer? draft;
    private bool working, closed;
    public McpServersDialog(DesktopMcpManager manager, bool add = false)
    {
        this.manager = manager;
        Name = "McpServersDialog"; Title = DesktopResources.Get("McpInstalledServers"); CloseButtonText = DesktopResources.Get("Close");
        Resources["ContentDialogMaxWidth"] = 1000d; Resources["ContentDialogMinWidth"] = 0d;
        viewer = new ScrollViewer { Content = body, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = viewer;
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        foreach (var control in new Control[] { name, url }) ControlAppearance.Native(control);
        manager.Changed += ManagerChanged;
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; };
        Closing += (_, args) => { if (working && !closed) args.Cancel = true; };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); manager.Changed -= ManagerChanged; XamlRoot.Changed -= RootChanged; };
        if (add) Edit(new()); else Render();
    }
    public void Shutdown() { closed = true; lifetime.Cancel(); Hide(); }
    private void ManagerChanged() => DispatcherQueue.TryEnqueue(() => { if (!closed && draft is null && !working) Render(); });
    private Button Button(string key, Action action)
    {
        var button = new Button { Name = key, Content = DesktopResources.Get(key) }; ControlAppearance.Native(button);
        button.Click += (_, _) => action(); return button;
    }
    private void Render()
    {
        body.Children.Clear(); body.Children.Add(status);
        body.Children.Add(new TextBlock { Text = DesktopResources.Get("McpEnableHint"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Button("McpAddServer", () => Edit(new())));
        var servers = manager.Servers;
        if (servers.Count == 0) body.Children.Add(new TextBlock { Text = DesktopResources.Get("McpNoInstalledServers"), TextWrapping = TextWrapping.Wrap });
        foreach (var view in servers)
        {
            var server = view.Server;
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
            ControlAppearance.Stock(toggle); AutomationProperties.SetAutomationId(toggle, server.Id + ":Enabled");
            AutomationProperties.SetName(toggle, server.Name);
            toggle.Toggled += async (_, _) => await ActAsync(ct => manager.SetEnabledAsync(server.Id, toggle.IsOn, ct));
            Grid.SetColumn(toggle, 1); header.Children.Add(toggle); panel.Children.Add(header);
            var address = new TextBlock { Text = server.Url, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            NativeCardSurface.Secondary(address); panel.Children.Add(address);
            var state = new TextBlock { Name = "McpConnectionStatus", Text = DesktopResources.Get("McpState" + view.State), TextWrapping = TextWrapping.Wrap };
            NativeCardSurface.Secondary(state, true); panel.Children.Add(state);
            if (view.Error is not null) panel.Children.Add(new TextBlock { Text = view.Error, TextWrapping = TextWrapping.Wrap });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(Button("Edit", () => Edit(server.Clone())));
            actions.Children.Add(Button("McpRemove", async () => await ActAsync(ct => manager.RemoveAsync(server.Id, ct))));
            if (server.Enabled) actions.Children.Add(Button("Retry", async () => await ActAsync(ct => manager.SetEnabledAsync(server.Id, true, ct))));
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
        body.Children.Add(name); body.Children.Add(url); name.Text = server.Name; url.Text = server.Url;
        body.Children.Add(new TextBlock { Text = DesktopResources.Get("McpHeadersHint"), TextWrapping = TextWrapping.Wrap });
        headerRows.Children.Clear(); headers.Clear(); body.Children.Add(headerRows);
        foreach (var header in server.Headers) AddHeader(header.Key, header.Value);
        body.Children.Add(Button("McpAddHeader", () => { if (headers.Count < 32) AddHeader("", ""); }));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(Button("Save", async () =>
        {
            try
            {
                server.Name = name.Text.Trim(); server.Url = url.Text.Trim();
                server.Headers = McpValidation.Headers(headers.Where(h => !string.IsNullOrWhiteSpace(h.Key.Text))
                    .Select(h => new KeyValuePair<string, string>(h.Key.Text.Trim(), h.Value.Password)));
                server.Validate();
            }
            catch (InvalidOperationException e) { status.Text = e.Message; return; }
            await ActAsync(ct => manager.InstallAsync(server, ct), saved: true);
        }));
        actions.Children.Add(Button("Cancel", () => { draft = null; status.Text = ""; Render(); })); body.Children.Add(actions);
    }
    private void AddHeader(string key, string value)
    {
        var keyBox = new TextBox { Header = DesktopResources.Get("McpHeaderName"), Text = key, MaxLength = 256 };
        var valueBox = new PasswordBox { Header = DesktopResources.Get("McpHeaderValue"), Password = value, PasswordRevealMode = PasswordRevealMode.Hidden, MaxLength = 16384 };
        ControlAppearance.Native(keyBox); ControlAppearance.Native(valueBox);
        var row = new StackPanel { Spacing = 6 }; row.Children.Add(keyBox); row.Children.Add(valueBox);
        row.Children.Add(Button("McpRemoveHeader", () => { headers.Remove((keyBox, valueBox)); headerRows.Children.Remove(row); }));
        headers.Add((keyBox, valueBox)); headerRows.Children.Add(row);
    }
    private async Task ActAsync(Func<CancellationToken, Task> action, bool saved = false)
    {
        if (working || closed) return;
        working = true; CloseButtonText = ""; status.Text = DesktopResources.Get("Working");
        foreach (var control in ControlAppearance.Descendants(body).OfType<Control>()) control.IsEnabled = false;
        try { await action(lifetime.Token); if (saved) draft = null; status.Text = ""; }
        catch (OperationCanceledException) { status.Text = DesktopResources.Get("OperationCanceled"); }
        catch (Exception e) { status.Text = DesktopMcpManager.SafeError(e); }
        finally
        {
            working = false; CloseButtonText = DesktopResources.Get("Close");
            if (!closed)
            {
                if (draft is null) Render();
                else foreach (var control in ControlAppearance.Descendants(body).OfType<Control>()) control.IsEnabled = true;
            }
        }
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot() { viewer.Width = Math.Max(0, Math.Min(800, XamlRoot.Size.Width - 96)); viewer.Height = Math.Max(0, Math.Min(650, XamlRoot.Size.Height - 240)); }
}
