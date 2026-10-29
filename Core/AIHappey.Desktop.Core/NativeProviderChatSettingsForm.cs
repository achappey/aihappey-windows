using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static AIHappey.Desktop.Core.ChatSettingsFields;

namespace AIHappey.Desktop.Core;

/// <summary>Native layout shared by the new provider editors, with no wire-format policy.</summary>
internal abstract class NativeProviderChatSettingsForm(string prefix) : IChatProviderForm
{
    protected readonly ChatSettingsFields fields = new();
    protected readonly StackPanel cards = new() { Spacing = 18 };
    protected readonly Dictionary<string, ChatSettingsFields> childFields = [];
    private readonly List<(SettingsExpander Expander, StackPanel Body)> sections = [];
    public FrameworkElement View => cards;
    public bool IsValid => fields.IsValid && childFields.Values.All(f => f.IsValid);
    protected Action? changed;
    public void Watch(Action update) { changed += update; fields.Watch(update); }
    public abstract void Commit(ChatPreferences preferences);
    public void FocusInvalid()
    {
        var control = fields.FirstInvalid ?? childFields.Values.Select(f => f.FirstInvalid).FirstOrDefault(c => c is not null);
        if (control is null) return;
        foreach (var (expander, body) in sections) if (NativeSettingsSurface.Contains(body, control)) expander.IsExpanded = true;
        cards.DispatcherQueue.TryEnqueue(() => { cards.UpdateLayout(); control.Focus(FocusState.Programmatic); control.StartBringIntoView(); });
    }
    protected StackPanel Section(string name, string label, Func<bool>? read = null, Action<bool>? write = null,
        bool expandable = true, Func<bool>? toggleEnabled = null)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        if (read is not null && write is not null)
        {
            var toggle = fields.HeaderSwitch(header, label, read, write);
            toggle.Name = prefix + "_" + name + "_Enabled";
            if (toggleEnabled is not null) fields.Watch(() => toggle.IsEnabled = toggleEnabled());
        }
        if (!expandable)
        {
            NativeSettingsSurface.ToggleCard(cards, prefix + "_" + name, L(label), header);
            return new StackPanel();
        }
        var expander = NativeSettingsSurface.Expander(cards, prefix + "_" + name, L(label), read is null ? null : header, out var body);
        sections.Add((expander, body)); return body;
    }
}
