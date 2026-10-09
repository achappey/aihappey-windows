using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Native controls for provider forms. Labels are localized; wire values never are.</summary>
internal sealed class ChatSettingsFields
{
    private readonly List<Action> refresh = [];
    private readonly HashSet<Control> invalid = [];
    private bool syncing;
    public bool IsValid => invalid.Count == 0;
    public Control? FirstInvalid => invalid.FirstOrDefault(control => control.IsEnabled);
    public static string L(string key) => DesktopResources.Get("ChatForm_" + key.Replace(':', '_').Replace('.', '_'));
    public void Refresh()
    {
        if (syncing) return;
        syncing = true;
        try { foreach (var update in refresh.ToArray()) update(); }
        finally { syncing = false; }
    }
    public void Changed(Action change) { if (syncing) return; change(); Refresh(); }
    public void Watch(Action update) { refresh.Add(update); update(); }
    private void Enabled(Control control, Func<bool>? enabled)
    {
        if (enabled is null) return;
        refresh.Add(() => { control.IsEnabled = enabled(); if (!control.IsEnabled) invalid.Remove(control); });
        control.IsEnabled = enabled();
    }
    public void Visible(FrameworkElement element, Func<bool> visible) => Watch(() => element.Visibility = visible() ? Visibility.Visible : Visibility.Collapsed);

    public TextBox Text(Panel parent, string key, Func<string> read, Action<string> write, Func<bool>? enabled = null, bool multiline = false, Func<string, bool>? validate = null)
    {
        var box = new TextBox { Header = L(key), Text = read(), TextWrapping = TextWrapping.Wrap, AcceptsReturn = multiline, MinHeight = multiline ? 72 : 0, MaxHeight = multiline ? 220 : double.PositiveInfinity };
        ControlAppearance.Stock(box); AutomationProperties.SetName(box, L(key));
        var field = new StackPanel { Spacing = 4 };
        field.Children.Add(box);
        var error = new TextBlock { Text = DesktopResources.Get("ChatFieldInvalid"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        field.Children.Add(error); parent.Children.Add(field);
        Enabled(box, enabled);
        refresh.Add(() => { if (!invalid.Contains(box) && box.FocusState == FocusState.Unfocused && box.Text != read()) box.Text = read(); if (!box.IsEnabled) error.Visibility = Visibility.Collapsed; });
        box.TextChanged += (_, _) =>
        {
            if (syncing) return;
            var valid = validate?.Invoke(box.Text) ?? true;
            if (valid) invalid.Remove(box); else invalid.Add(box);
            error.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
            if (valid) Changed(() => write(box.Text));
        };
        return box;
    }

    public ComboBox Select(Panel parent, string key, Func<string> read, IEnumerable<string> options, Action<string> write, Func<bool>? enabled = null, bool inherit = false, Func<string, string>? label = null)
    {
        var values = options.ToList();
        if (inherit && !values.Contains("")) values.Insert(0, "");
        var selected = read();
        if (selected.Length > 0 && !values.Contains(selected)) values.Add(selected); // Future provider values survive round trips.
        var box = new ComboBox { Header = L(key), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var value in values) box.Items.Add(new ComboBoxItem { Tag = value, Content = value.Length == 0 ? L("inherit") : label?.Invoke(value) ?? value });
        void Update() => box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == read());
        Update(); ControlAppearance.Stock(box); AutomationProperties.SetName(box, L(key)); parent.Children.Add(box);
        Enabled(box, enabled); refresh.Add(Update);
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem item) Changed(() => write((string)item.Tag)); };
        return box;
    }

    public ToggleSwitch Switch(Panel parent, string key, Func<bool> read, Action<bool> write, Func<bool>? enabled = null)
    {
        var row = new Grid { ColumnSpacing = 12, MinHeight = 32 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = L(key), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        var control = new ToggleSwitch { IsOn = read(), OnContent = "", OffContent = "", MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        ControlAppearance.Stock(control); AutomationProperties.SetName(control, L(key)); Grid.SetColumn(control, 1); row.Children.Add(control); parent.Children.Add(row);
        Enabled(control, enabled); refresh.Add(() => control.IsOn = read());
        control.Toggled += (_, _) => Changed(() => write(control.IsOn));
        return control;
    }

    public ToggleSwitch HeaderSwitch(Panel parent, string titleKey, Func<bool> read, Action<bool> write)
    {
        var control = new ToggleSwitch { Name = "CardEnabledSwitch", IsOn = read(), OnContent = "", OffContent = "", MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        ControlAppearance.Stock(control);
        AutomationProperties.SetName(control, L(titleKey) + " — " + L("enabled"));
        parent.Children.Add(control); refresh.Add(() => control.IsOn = read());
        control.Toggled += (_, _) => Changed(() => write(control.IsOn));
        return control;
    }

    public Slider Slider(Panel parent, string key, Func<double> read, double min, double max, Action<double> write, Func<bool>? enabled = null, Func<double, string>? valueLabel = null)
    {
        var panel = new StackPanel { Spacing = 4 };
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = 1, Value = read() };
        ControlAppearance.Stock(slider); AutomationProperties.SetName(slider, L(key));
        panel.Children.Add(label); panel.Children.Add(slider); parent.Children.Add(panel);
        void Update() { slider.Value = read(); label.Text = L(key) + " (" + (valueLabel?.Invoke(read()) ?? read().ToString(CultureInfo.InvariantCulture)) + ")"; }
        Update(); refresh.Add(Update); Enabled(slider, enabled);
        slider.ValueChanged += (_, _) => Changed(() => write(slider.Value));
        return slider;
    }
    public TextBox Integer(Panel parent, string key, Func<string> read, Action<int?> write, Func<bool>? enabled = null, int min = 1, int max = int.MaxValue, bool optional = true)
    {
        bool Valid(string value) => optional && string.IsNullOrWhiteSpace(value) || int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= min && number <= max;
        return Text(parent, key, read, value => write(string.IsNullOrWhiteSpace(value) ? null : int.Parse(value, CultureInfo.InvariantCulture)), enabled, validate: Valid);
    }
    public TextBox Json(Panel parent, string key, Func<JsonNode?> read, Action<JsonNode> write, Func<bool>? enabled = null, bool array = false)
    {
        bool Valid(string value)
        {
            try { var node = JsonNode.Parse(value); return array ? node is JsonArray : node is JsonObject; }
            catch (JsonException) { return false; }
        }
        return Text(parent, key, () => (read() ?? (array ? new JsonArray() : new JsonObject())).ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            value => write(JsonNode.Parse(value)!), enabled, true, Valid);
    }
    public static StackPanel Column(Panel parent)
    {
        var result = new StackPanel { Spacing = 12 }; parent.Children.Add(result); return result;
    }
    public static (StackPanel Left, StackPanel Right) Pair(Panel parent)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var left = new StackPanel { Spacing = 12 }; var right = new StackPanel { Spacing = 12 };
        grid.Children.Add(left); grid.Children.Add(right); Grid.SetColumn(right, 1); parent.Children.Add(grid);
        grid.SizeChanged += (_, _) =>
        {
            var narrow = grid.ActualWidth < 460;
            grid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(right, narrow ? 0 : 1); Grid.SetRow(right, narrow ? 1 : 0);
        };
        return (left, right);
    }
    public static Button Button(Panel parent, string key, Action action)
    {
        var button = new Button { Content = L(key) }; ControlAppearance.Stock(button); AutomationProperties.SetName(button, L(key));
        button.Click += (_, _) => action(); parent.Children.Add(button); return button;
    }
}

internal interface IChatProviderForm
{
    FrameworkElement View { get; }
    bool IsValid { get; }
    void FocusInvalid();
    void Commit(ChatPreferences preferences);
}

/// <summary>Adding a provider only requires registering its native form; the modal and transport stay generic.</summary>
internal static class ChatProviderForms
{
    private static readonly Dictionary<string, Func<ChatPreferences, IChatProviderForm>> factories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] = preferences => new OpenAIChatSettingsForm(preferences)
    };
    public static IChatProviderForm? Create(string? provider, ChatPreferences preferences) => provider is not null && factories.TryGetValue(provider, out var factory) ? factory(preferences) : null;
}
