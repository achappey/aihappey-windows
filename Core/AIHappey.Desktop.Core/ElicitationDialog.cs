using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using ModelContextProtocol.Protocol;

namespace AIHappey.Desktop.Core;

/// <summary>One renderer for the complete MCP constrained schema, not a server-specific form.</summary>
public sealed class ElicitationDialog : ContentDialog, IResponsiveDialog
{
    private readonly DesktopElicitationForm form;
    private readonly StackPanel fields = new() { Spacing = 16 };
    private readonly Dictionary<string, (FrameworkElement Control, TextBlock Error)> controls = new(StringComparer.Ordinal);
    private readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Stretch };
    public ElicitResult Result { get; private set; } = new() { Action = "cancel" };

    public ElicitationDialog(string origin, ElicitRequestParams request)
    {
        Name = "ElicitationDialog"; form = new(request);
        Title = DesktopResources.Get("ElicitationTitle");
        PrimaryButtonText = DesktopResources.Get("ElicitationAccept");
        SecondaryButtonText = DesktopResources.Get("ElicitationDecline");
        CloseButtonText = DesktopResources.Get("Cancel");
        DefaultButton = ContentDialogButton.None;
        Resources["ContentDialogMaxWidth"] = 760d; Resources["ContentDialogMinWidth"] = 0d;
        fields.Children.Add(new TextBlock { Text = origin, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        fields.Children.Add(new TextBlock { Text = request.Message, TextWrapping = TextWrapping.Wrap });
        foreach (var field in form.Fields) AddField(field);
        scroll.Content = fields; Content = scroll;
        PrimaryButtonClick += (_, args) =>
        {
            var errors = form.Errors();
            foreach (var (name, control) in controls)
            {
                control.Error.Text = errors.GetValueOrDefault(name, "");
                control.Error.Visibility = errors.ContainsKey(name) ? Visibility.Visible : Visibility.Collapsed;
            }
            if (errors.Count > 0)
            {
                args.Cancel = true;
                var first = controls[errors.Keys.First()].Control;
                var invalid = first as Control ?? (first as Panel)?.Children.OfType<Control>().FirstOrDefault();
                invalid?.Focus(FocusState.Programmatic);
                first.StartBringIntoView();
                return;
            }
            Result = form.Accept();
        };
        SecondaryButtonClick += (_, _) => Result = new() { Action = "decline" };
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; };
        Closed += (_, _) => XamlRoot.Changed -= RootChanged;
    }

    private void AddField(ElicitationField field)
    {
        var panel = new StackPanel { Spacing = 6 };
        var label = field.Title + (field.Required ? " *" : "");
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (field.Description is { Length: > 0 } description)
            panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        var bounds = field.Type switch
        {
            "number" or "integer" => ("minimum", "maximum", "ElicitationNumberRange"),
            "array" => ("minItems", "maxItems", "ElicitationChoiceRange"),
            _ => ("minLength", "maxLength", "ElicitationLengthRange")
        };
        if (field.Bound(bounds.Item1) is not null || field.Bound(bounds.Item2) is not null)
            panel.Children.Add(new TextBlock { Text = DesktopResources.Format(bounds.Item3,
                field.Bound(bounds.Item1)?.ToString(CultureInfo.InvariantCulture) ?? "—",
                field.Bound(bounds.Item2)?.ToString(CultureInfo.InvariantCulture) ?? "—"), TextWrapping = TextWrapping.Wrap });
        form.Values.TryGetValue(field.Name, out var initial);
        void Set(object? value)
        {
            if (value is null) form.Values.Remove(field.Name);
            else form.Values[field.Name] = JsonSerializer.SerializeToElement(value);
        }
        FrameworkElement input;
        if (field.Type == "boolean")
        {
            var check = new CheckBox { IsThreeState = true, Content = DesktopResources.Get("ElicitationBoolean"),
                IsChecked = initial.ValueKind is JsonValueKind.True or JsonValueKind.False ? initial.GetBoolean() : null };
            check.Checked += (_, _) => Set(true); check.Unchecked += (_, _) => Set(false); check.Indeterminate += (_, _) => Set(null);
            input = check;
        }
        else if (field.Type == "array")
        {
            var choices = new StackPanel { Spacing = 4 };
            var selected = initial.ValueKind == JsonValueKind.Array ? initial.EnumerateArray().Select(v => v.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
            foreach (var option in field.Options)
            {
                var check = new CheckBox { Content = option.Title, IsChecked = selected.Contains(option.Value), Tag = option.Value };
                void Changed()
                {
                    if (check.IsChecked == true) selected.Add(option.Value); else selected.Remove(option.Value);
                    Set(selected.Count == 0 && !field.Required ? null : field.Options.Where(o => selected.Contains(o.Value)).Select(o => o.Value).ToArray());
                }
                check.Checked += (_, _) => Changed(); check.Unchecked += (_, _) => Changed();
                ToolbarControls.Label(check, label + ": " + option.Title); choices.Children.Add(check);
            }
            // A required multiselect can validly be an empty array (unless minItems forbids it).
            if (field.Required && initial.ValueKind == JsonValueKind.Undefined) Set(Array.Empty<string>());
            input = choices;
        }
        else if (field.IsEnum)
        {
            var select = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            select.Items.Add(new ComboBoxItem { Content = DesktopResources.Get("ElicitationChoose"), Tag = null });
            foreach (var option in field.Options) select.Items.Add(new ComboBoxItem { Content = option.Title, Tag = option.Value });
            select.SelectedIndex = initial.ValueKind == JsonValueKind.String
                ? field.Options.ToList().FindIndex(o => o.Value == initial.GetString()) + 1 : 0;
            select.SelectionChanged += (_, _) => Set((select.SelectedItem as ComboBoxItem)?.Tag);
            input = select;
        }
        else if (field.Type == "string" && field.Format == "date")
        {
            var date = new CalendarDatePicker
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, DateFormat = "{year.full}-{month.integer(2)}-{day.integer(2)}",
                // Do not inherit the control's default +/-100 year restriction: MCP dates have no such bound.
                MinDate = new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.Zero),
                MaxDate = new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero)
            };
            if (initial.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(initial.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)) date.Date = value;
            date.DateChanged += (_, _) => Set(date.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            input = date;
        }
        else
        {
            var text = new TextBox { Text = initial.ValueKind == JsonValueKind.String ? initial.GetString()! : initial.ValueKind == JsonValueKind.Number ? initial.GetRawText() : "",
                HorizontalAlignment = HorizontalAlignment.Stretch };
            if (field.Type is "number" or "integer")
            {
                text.PlaceholderText = DesktopResources.Get(field.Type == "integer" ? "ElicitationIntegerHint" : "ElicitationNumberHint");
                text.TextChanged += (_, _) =>
                {
                    if (text.Text.Length == 0) Set(null);
                    else if (decimal.TryParse(text.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact)) Set(exact);
                    else if (double.TryParse(text.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                    {
                        try { form.Values[field.Name] = JsonSerializer.Deserialize<JsonElement>(text.Text); }
                        catch (JsonException) { Set(number); }
                    }
                    else Set(text.Text); // Keep invalid input visible and reject it on submission.
                };
            }
            else
            {
                text.PlaceholderText = field.Format switch { "date-time" => "2026-10-09T12:30:00+02:00", "email" => "name@example.com", "uri" => "https://example.com/", _ => "" };
                text.AcceptsReturn = field.Format is null && (field.Bound("maxLength") > 255 || field.Bound("minLength") > 80);
                text.TextWrapping = text.AcceptsReturn ? TextWrapping.Wrap : TextWrapping.NoWrap;
                text.MinHeight = text.AcceptsReturn ? 120 : 0;
                text.TextChanged += (_, _) => Set(text.Text.Length == 0 && !field.Required ? null : text.Text);
            }
            input = text;
        }
        input.Name = "ElicitationField"; input.Tag = field.Name;
        AutomationProperties.SetName(input, label);
        AutomationProperties.SetHelpText(input, field.Description ?? "");
        AutomationProperties.SetIsRequiredForForm(input, field.Required);
        if (input is Control native) ControlAppearance.Native(native);
        panel.Children.Add(input);
        var error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        panel.Children.Add(error); controls.Add(field.Name, (input, error)); fields.Children.Add(panel);
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot()
    {
        scroll.Width = Math.Max(0, Math.Min(640, XamlRoot.Size.Width - 96));
        scroll.MaxHeight = Math.Max(0, Math.Min(560, XamlRoot.Size.Height - 240));
    }
}
