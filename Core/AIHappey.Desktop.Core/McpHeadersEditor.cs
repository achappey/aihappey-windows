using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

internal sealed class McpHeadersEditor : UserControl
{
    private readonly IReadOnlyList<McpRegistryHeader> definitions;
    private readonly bool oauth;
    private readonly StackPanel body = new() { Spacing = 12 };
    private readonly StackPanel form = new() { Spacing = 12 };
    private readonly StackPanel headers = new() { Spacing = 8 };
    private readonly TextBox json = new() { Name = "McpHeadersJson", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160,
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") };
    private readonly TextBlock status = new() { Name = "McpHeaderError", TextWrapping = TextWrapping.Wrap };
    private readonly TabView tabs = new() { Name = "McpHeaderTabs", IsAddTabButtonVisible = false, CanDragTabs = false,
        CanReorderTabs = false, TabWidthMode = TabViewWidthMode.SizeToContent };
    private readonly Dictionary<string, TabViewItem> tabItems = [];
    private readonly List<(TextBox Name, PasswordBox Value)> headerRows = [];
    private readonly List<(TextBox Name, PasswordBox Value)> extraRows = [];
    private List<McpHeaderField> fields = [];
    private string? formSnapshot;
    private string active;
    private bool switching;

    public McpHeadersEditor(DesktopMcpServer server)
    {
        definitions = server.HeaderDefinitions; oauth = server.OAuthManaged;
        Name = "McpHeadersEditor"; Content = body;
        if (oauth) body.Children.Add(new TextBlock { Text = DesktopResources.Get("McpOAuthManaged"), TextWrapping = TextWrapping.Wrap });
        var hasForm = definitions.Any(header => !McpHeaderConfiguration.Managed(header, oauth));
        active = hasForm ? "form" : "headers";
        if (hasForm) AddTab("form", "McpHeadersForm", form);
        AddTab("headers", "McpHeaders", headers); AddTab("raw", "McpHeadersRaw", json);
        ControlAppearance.Native(tabs); ControlAppearance.Native(json); ToolbarControls.Label(json, DesktopResources.Get("McpHeadersRaw"));
        body.Children.Add(tabs); body.Children.Add(status);
        RenderForm(server.Headers); RenderHeaders(server.Headers);
        tabs.SelectedItem = tabItems[active];
        tabs.SelectionChanged += (_, _) => { if (!switching && tabs.SelectedItem is TabViewItem { Tag: string key }) Switch(key); };
        json.TextChanged += (_, _) => UpdateStatus(); UpdateStatus();
    }
    private void AddTab(string key, string label, FrameworkElement content)
    {
        var item = new TabViewItem { Name = "McpHeadersTab_" + key, Header = DesktopResources.Get(label), Tag = key, IsClosable = false, Content = content };
        ToolbarControls.Label(item, DesktopResources.Get(label)); tabItems.Add(key, item); tabs.TabItems.Add(item);
    }
    private void Switch(string next)
    {
        if (next == active) return;
        try
        {
            var values = ReadHeaders();
            var serialized = JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true });
            if (active == "form") formSnapshot = serialized;
            if (next == "form" && serialized != formSnapshot) RenderForm(values);
            if (next == "headers") RenderHeaders(values);
            if (next == "raw") json.Text = serialized;
            active = next;
        }
        catch (InvalidOperationException e) { status.Text = e.Message; }
        switching = true; tabs.SelectedItem = tabItems[active]; switching = false; UpdateStatus();
    }
    private void RenderForm(IReadOnlyDictionary<string, string> values)
    {
        form.Children.Clear(); extraRows.Clear();
        fields = McpHeaderConfiguration.Fields(definitions, oauth, values);
        foreach (var header in definitions.Where(header => !McpHeaderConfiguration.Managed(header, oauth)))
        {
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = header.Name, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (header.Description is { Length: > 0 } description)
            { var hint = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap }; NativeCardSurface.Secondary(hint); panel.Children.Add(hint); }
            foreach (var field in fields.Where(field => field.Header == header.Name))
            {
                var label = (field.Variable ?? DesktopResources.Get("McpHeaderValue")) + (field.Input.IsRequired ? " *" : "");
                var choices = field.Input.Choices?.Length > 0 ? field.Input.Choices : field.Input.Format == "boolean" ? ["true", "false"] : null;
                Control control;
                if (field.Input.IsSecret)
                {
                    var password = new PasswordBox { Name = "McpRegistrySecret", Header = label, Password = field.Value, MaxLength = 16384,
                        PasswordRevealMode = PasswordRevealMode.Hidden, PlaceholderText = field.Input.Placeholder ?? "", IsEnabled = !field.Fixed };
                    password.PasswordChanged += (_, _) => { field.Value = password.Password; UpdateStatus(); }; control = password;
                }
                else if (choices is not null)
                {
                    var select = new ComboBox { Header = label, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !field.Fixed };
                    select.Items.Add(""); foreach (var choice in choices) select.Items.Add(choice);
                    if (!choices.Contains(field.Value) && field.Value.Length > 0) select.Items.Add(field.Value);
                    select.SelectedItem = field.Value; select.SelectionChanged += (_, _) => { field.Value = select.SelectedItem as string ?? ""; UpdateStatus(); }; control = select;
                }
                else
                {
                    var text = new TextBox { Header = label, Text = field.Value, MaxLength = 16384, IsReadOnly = field.Fixed, PlaceholderText = field.Input.Placeholder ?? "" };
                    text.TextChanged += (_, _) => { field.Value = text.Text; UpdateStatus(); }; control = text;
                }
                ControlAppearance.Native(control); ToolbarControls.Label(control, label); panel.Children.Add(control);
                if (field.Input.Description is { Length: > 0 } hintText && hintText != header.Description)
                { var hint = new TextBlock { Text = hintText, TextWrapping = TextWrapping.Wrap }; NativeCardSurface.Secondary(hint, true); panel.Children.Add(hint); }
            }
            var card = new Border { Name = "McpRegistryHeaderCard", Child = panel };
            NativeCardSurface.Card(card, true); form.Children.Add(card);
        }
        var extras = new StackPanel { Spacing = 8 };
        foreach (var header in values.Where(pair => !fields.Any(field => field.Header.Equals(pair.Key, StringComparison.OrdinalIgnoreCase))))
            AddRow(extras, extraRows, header.Key, header.Value);
        form.Children.Add(extras);
    }
    private void RenderHeaders(IReadOnlyDictionary<string, string> values)
    {
        headers.Children.Clear(); headerRows.Clear();
        var rows = new StackPanel { Spacing = 8 }; headers.Children.Add(rows);
        foreach (var (name, value) in values) AddRow(rows, headerRows, name, value);
        var add = new Button { Name = "McpAddHeader", Content = DesktopResources.Get("McpAddHeader") };
        ControlAppearance.Native(add); add.Click += (_, _) => { if (headerRows.Count < 32) AddRow(rows, headerRows, "", ""); }; headers.Children.Add(add);
    }
    private void AddRow(StackPanel panel, List<(TextBox Name, PasswordBox Value)> rows, string name, string value)
    {
        var key = new TextBox { Name = "McpHeaderName", Header = DesktopResources.Get("McpHeaderName"), Text = name, MaxLength = 256 };
        var secret = new PasswordBox { Name = "McpHeaderValue", Header = DesktopResources.Get("McpHeaderValue"), Password = value, MaxLength = 16384, PasswordRevealMode = PasswordRevealMode.Hidden };
        var remove = new Button { Content = DesktopResources.Get("McpRemoveHeader") };
        foreach (var control in new Control[] { key, secret, remove }) ControlAppearance.Native(control);
        var row = new StackPanel { Spacing = 6 }; row.Children.Add(key); row.Children.Add(secret); row.Children.Add(remove);
        rows.Add((key, secret)); panel.Children.Add(row);
        key.TextChanged += (_, _) => UpdateStatus(); secret.PasswordChanged += (_, _) => UpdateStatus();
        remove.Click += (_, _) => { rows.Remove((key, secret)); panel.Children.Remove(row); UpdateStatus(); };
    }
    private static IEnumerable<KeyValuePair<string, string>> Values(List<(TextBox Name, PasswordBox Value)> rows) =>
        rows.Where(row => row.Name.Text.Length > 0 || row.Value.Password.Length > 0)
            .Select(row => new KeyValuePair<string, string>(row.Name.Text.Trim(), row.Value.Password));
    public Dictionary<string, string> ReadHeaders() => active switch
    {
        "raw" => McpHeaderConfiguration.Parse(json.Text),
        "headers" => McpValidation.Headers(Values(headerRows)),
        _ => McpHeaderConfiguration.Resolve(definitions, fields, Values(extraRows), validate: false)
    };
    private void UpdateStatus()
    {
        try { McpHeaderConfiguration.Validate(definitions, oauth, ReadHeaders()); status.Text = ""; }
        catch (InvalidOperationException e) { status.Text = e.Message; }
    }
}
