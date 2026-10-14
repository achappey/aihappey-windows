using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json.Nodes;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AIHappey.Desktop.Core;

public sealed partial class AgentEditDialog
{
    private StackPanel Plugins(IReadOnlyList<DesktopAgentPlugin> plugins)
    {
        var panel = Panel(); panel.Name = "AgentPluginsView";
        panel.Children.Add(new TextBlock { Text = DesktopResources.Get("PluginAgentSnapshotHint"), TextWrapping = TextWrapping.Wrap });
        var search = new TextBox { PlaceholderText = DesktopResources.Get("Search") }; ControlAppearance.Stock(search); panel.Children.Add(search);
        var rows = Panel(); panel.Children.Add(rows); var loaded = false;
        var associated = new Dictionary<string, JsonNode>(StringComparer.Ordinal); var labels = new Dictionary<JsonNode, string>();
        JsonArray Array() { if (draft.Definition["plugins"] is JsonArray array) return array; var next = new JsonArray(); draft.Definition["plugins"] = next; return next; }
        void Render()
        {
            rows.Children.Clear();
            foreach (var plugin in plugins.Where(p => (p.Id + " " + p.Description).Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
            {
                var toggle = new ToggleSwitch { Name = "AgentPluginEnabled", Header = plugin.Id, Tag = plugin.Id, IsOn = associated.ContainsKey(plugin.Id), OnContent = "", OffContent = "" }; ControlAppearance.Stock(toggle); rows.Children.Add(toggle);
                rows.Children.Add(new TextBlock { Text = plugin.Description, TextWrapping = TextWrapping.Wrap });
                toggle.Toggled += async (_, _) => await WorkAsync(_ =>
                {
                    if (associated.Remove(plugin.Id, out var old)) Array().Remove(old);
                    if (toggle.IsOn) { var embedded = DesktopAgentPluginSnapshots.Embed(plugin); Array().Add(embedded); associated[plugin.Id] = embedded; labels[embedded] = plugin.Id; }
                    if (Array().Count == 0) draft.Definition.Remove("plugins"); Render(); return Task.CompletedTask;
                });
            }
            foreach (var entry in (draft.Definition["plugins"] as JsonArray ?? new()).OfType<JsonNode>().ToArray())
            {
                var label = labels.TryGetValue(entry, out var name) ? name : DesktopResources.Get("PluginUnavailable");
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var remove = new Button { Content = DesktopResources.Get("Delete"), Tag = entry }; ControlAppearance.Stock(remove); actions.Children.Add(remove);
                remove.Click += (_, _) => { Array().Remove(entry); foreach (var id in associated.Where(p => p.Value == entry).Select(p => p.Key).ToArray()) associated.Remove(id); if (Array().Count == 0) draft.Definition.Remove("plugins"); Render(); };
                var download = new Button { Content = DesktopResources.Get("Download"), IsEnabled = DesktopAgent.Text(entry["type"]) == "base64" && DesktopAgent.Text(entry["media_type"]) == "application/zip" }; ControlAppearance.Stock(download); actions.Children.Add(download);
                download.Click += async (_, _) => await WorkAsync(async ct =>
                {
                    var picker = new FileSavePicker { SuggestedFileName = label }; picker.FileTypeChoices.Add(DesktopResources.Get("PluginArchive"), new List<string> { ".zip" }); WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
                    if (await picker.PickSaveFileAsync().AsTask(ct) is { } target) await FileIO.WriteBytesAsync(target, Convert.FromBase64String(DesktopAgent.Text(entry["data"])));
                });
                var body = Panel(); body.Children.Add(new TextBlock { Text = label + " · " + DesktopResources.Get("PluginEmbedded"), TextWrapping = TextWrapping.Wrap }); body.Children.Add(actions);
                var card = new Border { Child = body, Padding = new Thickness(16) }; NativeCardSurface.Card(card); rows.Children.Add(card);
            }
            UpdateSave();
        }
        search.TextChanged += (_, _) => Render();
        panel.Loaded += async (_, _) =>
        {
            if (loaded) return; loaded = true;
            await WorkAsync(async ct =>
            {
                foreach (var entry in (draft.Definition["plugins"] as JsonArray ?? new()).OfType<JsonNode>())
                {
                    try { var stored = await DesktopAgentPluginSnapshots.ReadAsync(entry, ct); if (stored is not null) { labels[entry] = stored.Id; if (plugins.Any(p => p.Id == stored.Id)) associated.TryAdd(stored.Id, entry); } }
                    catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or FormatException) { labels[entry] = DesktopResources.Get("PluginUnavailable"); }
                }
                Render();
            });
        };
        Render(); return panel;
    }
}
