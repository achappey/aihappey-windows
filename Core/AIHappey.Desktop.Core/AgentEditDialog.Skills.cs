using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace AIHappey.Desktop.Core;

public sealed partial class AgentEditDialog
{
    private sealed class SkillRow(CatalogItem item, JsonObject? entry, string version)
    {
        public CatalogItem Item = item;
        public JsonObject? Entry = entry;
        public JsonObject? Snapshot = entry;
        public string Mode = DesktopAgent.Text(entry?["type"]) == "inline" ? "inline" : "reference";
        public string Version = version;
        public bool Working;
        public bool Expanded;
        public SettingsExpander? View;
        public Action? Refresh;
    }
    private readonly Dictionary<string, SkillRow> skillRows = [];
    private JsonArray SkillArray()
    {
        if (draft.Definition["skills"] is JsonArray array) return array;
        var next = new JsonArray(); draft.Definition["skills"] = next; return next;
    }
    private StackPanel Skills()
    {
        var panel = Panel(); panel.Name = "AgentSkillsView";
        var search = new TextBox { Name = "AgentSkillSearch", PlaceholderText = DesktopResources.Get("SearchSkills") };
        ControlAppearance.Stock(search); ToolbarControls.Label(search, DesktopResources.Get("SearchSkills")); panel.Children.Add(search);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(status);
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; panel.Children.Add(actions);
        var retry = new Button { Content = DesktopResources.Get("Retry"), Visibility = Visibility.Collapsed };
        var import = new Button { Content = DesktopResources.Get("AgentSkillImport") };
        ControlAppearance.Stock(retry); ControlAppearance.Stock(import); actions.Children.Add(retry); actions.Children.Add(import);
        var rows = Panel(); panel.Children.Add(rows); var more = new Button { Content = DesktopResources.Get("ShowMore"), Visibility = Visibility.Collapsed };
        ControlAppearance.Stock(more); panel.Children.Add(more); var visible = 50;
        var started = false;
        var renderQueued = false;
        void RequestRender()
        {
            if (renderQueued || lifetime.IsCancellationRequested) return;
            renderQueued = true;
            panel.DispatcherQueue.TryEnqueue(() => { renderQueued = false; if (!lifetime.IsCancellationRequested) Render(); });
        }
        void Hydrate(IEnumerable<CatalogItem> catalogItems)
        {
            var entries = (draft.Definition["skills"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
            var associated = skillRows.Values.Where(r => r.Entry is not null).Select(r => r.Entry!).ToHashSet();
            foreach (var item in catalogItems)
            {
                if (skillRows.ContainsKey(item.Id)) continue;
                var entry = entries.FirstOrDefault(s => !associated.Contains(s) && DesktopAgent.Text(s["type"]) == "skill_reference" && DesktopAgent.Text(s["skill_id"]) == item.Id)
                    ?? entries.FirstOrDefault(s => !associated.Contains(s) && DesktopAgent.Text(s["type"]) == "inline" && DesktopAgent.Text(s["name"]) == item.Name && DesktopAgent.Text(s["description"]) == item.Description);
                if (entry is not null) associated.Add(entry);
                var version = DesktopAgent.Text(entry?["version"]);
                skillRows[item.Id] = new(item, entry, version.Length > 0 ? version : entry is null ? "latest" : "__default__");
            }
            foreach (var entry in entries.Where(e => !associated.Contains(e)))
            {
                var id = DesktopAgent.Text(entry["skill_id"]); var name = DesktopAgent.Text(entry["name"]);
                var key = id.Length > 0 ? id : "inline:" + Guid.NewGuid().ToString("N");
                if (skillRows.ContainsKey(key)) continue;
                var version = DesktopAgent.Text(entry["version"]);
                var item = new CatalogItem(CatalogKind.Skill, key, name.Length > 0 ? name : id, DesktopAgent.Text(entry["description"])) { Origin = CatalogOrigin.Local };
                skillRows[key] = new(item, entry, version.Length > 0 ? version : "__default__");
            }
        }
        void Render()
        {
            var matches = skillRows.Values.Where(r => (r.Item.Id + " " + r.Item.Name + " " + r.Item.Description).Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Entry is not null).ThenBy(r => r.Item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            var shown = matches.Take(visible).ToHashSet();
            foreach (var row in skillRows.Values)
            {
                if (row.View is null && shown.Contains(row)) BuildSkillRow(rows, row, RequestRender);
                if (row.View is not null)
                {
                    row.Refresh?.Invoke();
                    row.View.Visibility = shown.Contains(row) ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            more.Visibility = matches.Length > visible ? Visibility.Visible : Visibility.Collapsed;
            SizePage(); UpdateSave();
        }
        async Task Load()
        {
            status.Text = DesktopResources.Get("Loading"); retry.Visibility = Visibility.Collapsed;
            try
            {
                var catalogItems = await catalog.ListAsync(CatalogKind.Skill, lifetime.Token); lifetime.Token.ThrowIfCancellationRequested();
                // Initial unknown rows can be associated with a now-available catalog item without
                // replacing its stored payload, and no catalog load writes to the agent draft.
                foreach (var row in skillRows.Values.Where(r => r.Item.Origin == CatalogOrigin.Local && r.Entry is not null).ToArray())
                {
                    var matching = catalogItems.FirstOrDefault(i => i.Id == DesktopAgent.Text(row.Entry!["skill_id"])
                        || DesktopAgent.Text(row.Entry!["type"]) == "inline" && i.Name == DesktopAgent.Text(row.Entry!["name"]) && i.Description == DesktopAgent.Text(row.Entry!["description"]));
                    if (matching is not null) { skillRows.Remove(row.Item.Id); row.Item = matching; skillRows[matching.Id] = row; }
                }
                Hydrate(catalogItems); status.Text = ""; RequestRender();
            }
            catch (OperationCanceledException) { }
            catch { if (!lifetime.IsCancellationRequested) { status.Text = DesktopResources.Get("SkillsCatalogFailed"); retry.Visibility = Visibility.Visible; RequestRender(); } }
        }
        Hydrate([]); Render(); search.TextChanged += (_, _) => { visible = 50; RequestRender(); };
        more.Click += (_, _) => { visible += 50; RequestRender(); }; retry.Click += async (_, _) => await Load();
        panel.Loaded += async (_, _) => { if (!started) { started = true; await Load(); } };
        import.Click += async (_, _) => await WorkAsync(async ct =>
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".zip");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
            var file = await picker.PickSingleFileAsync(); if (file is null) return;
            if ((await file.GetBasicPropertiesAsync()).Size > DesktopCatalogClient.MaxDownloadBytes) throw new InvalidDataException(DesktopResources.Get("ResponseSizeLimit"));
            await using var input = await file.OpenStreamForReadAsync(); using var content = new StreamContent(input);
            var bytes = await DesktopCatalogClient.ReadBoundedAsync(content, DesktopCatalogClient.MaxDownloadBytes, ct);
            var parsed = await SkillFiles.ArchiveAsync(new("import", file.DisplayName, "", "local"), bytes, ct); ct.ThrowIfCancellationRequested();
            SkillArray().Add(InlineSkill(parsed.Skill, bytes)); Hydrate([]); RequestRender();
        });
        return panel;
    }
    private void BuildSkillRow(StackPanel parent, SkillRow row, Action render)
    {
        var toggle = new ToggleSwitch { Name = "AgentSkillEnabled", Tag = row.Item.Id, IsOn = row.Entry is not null,
            OnContent = "", OffContent = "", MinWidth = 0, IsEnabled = !row.Working, HorizontalAlignment = HorizontalAlignment.Right };
        ControlAppearance.Stock(toggle); ToolbarControls.Label(toggle, row.Item.Name);
        var expander = NativeSettingsSurface.Expander(parent, "AgentSkillRow", row.Item.Name, toggle, out var body);
        row.View = expander;
        expander.Tag = row.Item.Id; expander.IsExpanded = row.Expanded;
        body.Children.Add(new TextBlock { Text = row.Item.Description, TextWrapping = TextWrapping.Wrap });
        var mode = new ComboBox { Name = "AgentSkillMode", Header = ChatSettingsFields.L("agent.skillMode"), HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = row.Entry is not null && !row.Working && CatalogRoutes.SupportsSkill(row.Item.Id) };
        mode.Items.Add(new ComboBoxItem { Content = DesktopResources.Get("AgentSkillReference"), Tag = "reference" });
        mode.Items.Add(new ComboBoxItem { Content = DesktopResources.Get("AgentSkillInline"), Tag = "inline" });
        mode.SelectedIndex = row.Mode == "inline" ? 1 : 0; ControlAppearance.Stock(mode); body.Children.Add(mode);
        var version = new ComboBox { Name = "AgentSkillVersion", Header = ChatSettingsFields.L("agent.skillVersion"), HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = row.Entry is not null && !row.Working && CatalogRoutes.SupportsSkill(row.Item.Id) };
        void AddVersion(string value, string label)
        { if (!version.Items.OfType<ComboBoxItem>().Any(i => i.Tag as string == value)) version.Items.Add(new ComboBoxItem { Content = label, Tag = value }); }
        AddVersion("__default__", DesktopResources.Get("AgentSkillDefault")); AddVersion("latest", DesktopResources.Get("AgentSkillLatest"));
        foreach (var value in new[] { row.Version, row.Item.Version, row.Item.LatestVersion }.OfType<string>().Where(v => v.Length > 0)) AddVersion(value, value);
        version.SelectedItem = version.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == row.Version);
        ControlAppearance.Stock(version); body.Children.Add(version);
        var loading = new ProgressRing { IsActive = row.Working, Width = 20, Height = 20, Visibility = row.Working ? Visibility.Visible : Visibility.Collapsed }; body.Children.Add(loading);
        var loadedVersions = false; var syncing = false;
        row.Refresh = () =>
        {
            syncing = true;
            try
            {
                toggle.IsOn = row.Entry is not null; toggle.IsEnabled = !row.Working;
                mode.IsEnabled = version.IsEnabled = row.Entry is not null && !row.Working && CatalogRoutes.SupportsSkill(row.Item.Id);
                mode.SelectedIndex = row.Mode == "inline" ? 1 : 0;
                version.SelectedItem = version.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == row.Version);
                loading.IsActive = row.Working; loading.Visibility = row.Working ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { syncing = false; }
        };
        async Task Update(bool enabled, string nextMode, string nextVersion)
        {
            if (row.Working) return;
            row.Working = true; toggle.IsEnabled = mode.IsEnabled = version.IsEnabled = false; loading.IsActive = true; loading.Visibility = Visibility.Visible;
            await WorkAsync(async ct =>
            {
                JsonObject? next = null;
                if (enabled)
                {
                    if (nextMode == "inline")
                    {
                        if (nextMode == row.Mode && nextVersion == row.Version && row.Snapshot is not null) next = (JsonObject)row.Snapshot.DeepClone();
                        else
                        {
                            var concrete = nextVersion == "__default__" ? row.Item.Version : nextVersion == "latest" ? row.Item.LatestVersion ?? row.Item.Version : nextVersion;
                            var bytes = await catalog.DownloadSkillAsync(row.Item.Id, concrete, ct);
                            var parsed = await SkillFiles.ArchiveAsync(new(row.Item.Id, row.Item.Name, row.Item.Description, "remote", concrete), bytes, ct);
                            next = InlineSkill(parsed.Skill, bytes);
                        }
                    }
                    else
                    {
                        next = row.Mode == "reference" && row.Snapshot is not null ? (JsonObject)row.Snapshot.DeepClone()
                            : new JsonObject { ["type"] = "skill_reference", ["skill_id"] = row.Item.Id };
                        if (nextVersion == "__default__") next.Remove("version"); else next["version"] = nextVersion;
                    }
                }
                ct.ThrowIfCancellationRequested();
                if (row.Entry is not null) SkillArray().Remove(row.Entry);
                if (next is not null) { SkillArray().Add(next); row.Snapshot = next; }
                if (SkillArray().Count == 0) draft.Definition.Remove("skills");
                row.Entry = next; row.Mode = nextMode; row.Version = nextVersion;
            });
            row.Working = false; render();
        }
        toggle.Toggled += async (_, _) => { if (!syncing) await Update(toggle.IsOn, row.Mode, row.Version); };
        mode.SelectionChanged += async (_, _) => { if (!syncing && mode.SelectedItem is ComboBoxItem { Tag: string value } && value != row.Mode) await Update(true, value, row.Version); };
        version.SelectionChanged += async (_, _) => { if (!syncing && version.SelectedItem is ComboBoxItem { Tag: string value } && value != row.Version) await Update(true, row.Mode, value); };
        expander.Expanded += async (_, _) =>
        {
            row.Expanded = true;
            if (loadedVersions || !CatalogRoutes.SupportsSkill(row.Item.Id)) return; loadedVersions = true;
            try
            {
                var versions = await catalog.VersionsAsync(row.Item.Id, lifetime.Token); lifetime.Token.ThrowIfCancellationRequested();
                if (!expander.IsLoaded) return;
                syncing = true; foreach (var item in versions) AddVersion(item.Version, item.Version);
                version.SelectedItem = version.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == row.Version);
            }
            catch (OperationCanceledException) { }
            catch { loadedVersions = false; if (!lifetime.IsCancellationRequested) Message(DesktopResources.Get("VersionsFailed")); }
            finally { syncing = false; }
        };
        expander.Collapsed += (_, _) => row.Expanded = false;
    }
    private static JsonObject InlineSkill(DesktopSkill skill, byte[] bytes) => new()
    {
        ["type"] = "inline", ["name"] = skill.Name, ["description"] = skill.Description,
        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "application/zip", ["data"] = Convert.ToBase64String(bytes) }
    };
}
