using AIHappey.Desktop.Core;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Text;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckPluginEditorAsync(ElementTheme theme)
    {
        var root = new Grid { RequestedTheme = theme }; window!.Content = root;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 1000)); window.Activate(); await Task.Delay(100);
        await WaitForProviderUiAsync(() => root.XamlRoot is not null, "Plugin fixture visual root did not load");
        var context = "Plugin editor / " + theme;
        DesktopSkillFile SkillFile(string name) => new($"skills/{name}/SKILL.md", Encoding.UTF8.GetBytes($"---\nname: {name}\ndescription: Embedded {name}\n---\nInstructions."));
        var original = new DesktopAgentPlugin
        {
            Manifest = new JsonObject
            {
                ["$schema"] = AgentPluginPackages.Schema, ["name"] = "editor.fixture", ["version"] = "internal release", ["description"] = "Original description", ["license"] = "Custom company terms",
                ["author"] = new JsonObject { ["name"] = "Team", ["email"] = "internal directory", ["url"] = "internal profile" },
                ["keywords"] = new JsonArray("existing", "one, whole label"),
                ["extensions"] = new JsonObject { ["com.example.opaque"] = new JsonObject { ["custom"] = 42 } }
            },
            Mcp = new JsonObject { ["$schema"] = AgentPluginPackages.McpSchema, ["mcpServers"] = new JsonObject
                { ["local"] = new JsonObject { ["type"] = "stdio", ["command"] = "node", ["args"] = new JsonArray("literal") } } },
            Files = [SkillFile("embedded"), new("references/info.txt", Encoding.UTF8.GetBytes("package data"))]
        };
        var before = original.Clone(); var snapshots = 0; var failSnapshot = false;
        TaskCompletionSource<IReadOnlyList<DesktopSkillFile>>? pending = null;
        async Task<IReadOnlyList<DesktopSkillFile>> Snapshot(string id, CancellationToken ct)
        {
            snapshots++; await Task.Delay(1, ct);
            if (failSnapshot) throw new IOException("offline");
            return pending is null ? [SkillFile("remote")] : await pending.Task.WaitAsync(ct);
        }
        PluginEditDialog Dialog(DesktopAgentPlugin? value, bool readOnly = false, string? identity = null) => new(value, readOnly, AgentPluginPackages.DefaultNamespace,
            [new("provider/remote", "remote", "Remote description", "remote", "2.0")], Snapshot, [], identity, "identity@example.org")
            { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        var dialog = Dialog(original); SystemAppearance.PrepareDialog(dialog);
        var shown = dialog.ShowAsync(); await Task.Delay(150);
        var navigation = Field<NavigationView>(dialog, "tabs");
        async Task Tab(string key)
        {
            navigation.SelectedItem = navigation.MenuItems.OfType<NavigationViewItem>().Single(item => item.Tag as string == key);
            await Task.Delay(70); dialog.UpdateLayout();
        }
        TextBox Input(string name) => Descendants(dialog).OfType<TextBox>().Single(input => input.Name == name);
        Button Button(string name) => Descendants(dialog).OfType<Button>().Single(button => button.Name == name);
        Grid[] Labels() => Descendants(dialog).OfType<Grid>().Where(row => row.Name == "PluginLabelRow").ToArray();
        SettingsExpander[] Skills() => Descendants(dialog).OfType<SettingsExpander>().Where(row => row.Name == "PluginSkillCard").ToArray();
        async Task Idle()
        {
            await Task.Delay(50);
            await WaitForProviderUiAsync(() => !Field<bool>(dialog, "working"), "Plugin work did not finish");
        }
        try
        {
            var general = Field<Dictionary<string, FrameworkElement>>(dialog, "pages")["general"];
            var fields = ((StackPanel)general).Children.OfType<TextBox>().ToArray();
            Check(fields.Select(input => input.Name).SequenceEqual(["Plugin_name", "Plugin_version", "Plugin_description", "Plugin_homepage", "Plugin_repository", "Plugin_license"])
                && fields.All(input => !string.IsNullOrWhiteSpace(input.PlaceholderText)), context + ": General field order and all placeholders");
            Check(Input("Plugin_name").IsReadOnly && Input("Plugin_license").Text == "Custom company terms", context + ": edit preserves name immutability and free-text License");
            var description = Input("Plugin_description");
            Check(description.AcceptsReturn && description.TextWrapping == TextWrapping.Wrap && description.ActualHeight >= 140, context + ": Description starts as a visible multiline field");
            description.Text = "First line\nSecond line"; Input("Plugin_license").Text = "Arbitrary non-SPDX terms";
            await Tab("author");
            var authors = new[] { Input("PluginAuthor_name"), Input("PluginAuthor_email"), Input("PluginAuthor_url") };
            Check(authors.Select(input => input.Header).SequenceEqual(new object[] { "Name", "Email", "URL" })
                && authors.All(input => !input.IsReadOnly && !string.IsNullOrWhiteSpace(input.PlaceholderText)), context + ": short Author captions with placeholders");
            await Tab("keywords");
            Check(navigation.MenuItems.OfType<NavigationViewItem>().Single(item => item.Tag as string == "keywords").Content as string == "Labels"
                && Labels().Select(row => (string)row.Tag).SequenceEqual(["existing", "one, whole label"]), context + ": Labels retain complete imported strings");
            var label = Input("PluginLabelInput");
            Check(label.PlaceholderText == DesktopResources.Get("PluginAddLabel") && !Button("PluginLabelAdd").IsEnabled, context + ": localized label prompt and blank Add disabled");
            label.Text = "  added with button  ";
            await WaitForProviderUiAsync(() => Button("PluginLabelAdd").IsEnabled, "Label Add did not enable"); InvokeButton(Button("PluginLabelAdd"));
            await WaitForProviderUiAsync(() => label.Text == "", "Label Add did not finish");
            label.Text = "added with button";
            await WaitForProviderUiAsync(() => Button("PluginLabelAdd").IsEnabled, "Duplicate label Add did not enable"); InvokeButton(Button("PluginLabelAdd"));
            await WaitForProviderUiAsync(() => label.Text == "", "Duplicate label Add did not finish");
            Check(Labels().Length == 3 && label.Text == "", context + ": Add trims items, clears entry, and prevents exact duplicates");
            label.Text = "second label";
            await WaitForProviderUiAsync(() => Button("PluginLabelAdd").IsEnabled, "Second label Add did not enable"); InvokeButton(Button("PluginLabelAdd"));
            await WaitForProviderUiAsync(() => label.Text == "", "Second label Add did not finish");
            Check(Labels().Length == 4 && label.Text == "" && shown.Status == Windows.Foundation.AsyncStatus.Started && dialog.Result is null,
                context + ": adding multiple labels leaves the dialog open without saving");
            InvokeButton(Descendants(dialog).OfType<Button>().Single(button => button.Name == "PluginLabelRemove" && button.Tag as string == "existing"));
            await WaitForProviderUiAsync(() => Labels().Length == 3, "Label removal did not finish");
            Check(Labels().Select(row => (string)row.Tag).SequenceEqual(["one, whole label", "added with button", "second label"])
                && Descendants(dialog).OfType<Button>().Where(button => button.Name == "PluginLabelRemove").All(button => AutomationProperties.GetName(button).Contains((string)button.Tag)),
                context + ": individual removal keeps the other labels and has accessible names");
            await Tab("skills");
            Check(Skills().Length == 2 && Skills().All(row => !row.IsExpanded && row.Content is ToggleSwitch { Header: null, OnContent: "", OffContent: "" }),
                context + ": available and embedded skills use compact collapsed expanders with header toggles");
            var remote = Skills().Single(row => row.Tag as string == "remote"); remote.IsExpanded = true; await Task.Delay(350); dialog.UpdateLayout();
            Check(Descendants(remote).OfType<TextBlock>().Any(text => text.Text == "Remote description") && Descendants(remote).OfType<TextBlock>().Any(text => text.Text == "2.0"),
                context + ": expansion reveals skill description and version");
            failSnapshot = true; ((ToggleSwitch)remote.Content).IsOn = true; await Idle();
            remote = Skills().Single(row => row.Tag as string == "remote");
            Check(remote.Content is ToggleSwitch { IsOn: false, IsEnabled: true } && Field<TextBlock>(dialog, "error").Visibility == Visibility.Visible,
                context + ": failed snapshot restores actual selection and an enabled retry toggle");
            failSnapshot = false; pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ((ToggleSwitch)remote.Content).IsOn = true;
            await WaitForProviderUiAsync(() => Field<bool>(dialog, "working"), "Plugin snapshot did not start");
            Check(!dialog.IsPrimaryButtonEnabled && !navigation.IsEnabled && Skills().All(row => row.Content is ToggleSwitch { IsEnabled: false }),
                context + ": snapshot disables saving, navigation and skill toggles until completion");
            pending.SetResult([SkillFile("remote")]); await Idle(); pending = null;
            remote = Skills().Single(row => row.Tag as string == "remote");
            Check(remote.Content is ToggleSwitch { IsOn: true, IsEnabled: true } && !remote.IsExpanded && snapshots == 2 && Field<TextBlock>(dialog, "error").Visibility == Visibility.Collapsed,
                context + ": successful snapshot retains selection and restores compact interactive rows");
            var search = Descendants(dialog).OfType<AutoSuggestBox>().Single(input => input.Name == "PluginSkillSearch"); search.Text = "Remote description";
            await WaitForProviderUiAsync(() => Skills().Length == 1, "Plugin skill description search did not filter rows");
            Check(Skills().Length == 1 && Skills()[0].Content is ToggleSwitch { IsOn: true }, context + ": description search preserves selection");
            search.Text = ""; await WaitForProviderUiAsync(() => Skills().Length == 2, "Clearing plugin skill search did not restore rows");
            ((ToggleSwitch)Skills().Single(row => row.Tag as string == "embedded").Content).IsOn = false; await Idle();
            remote = Skills().Single(row => row.Tag as string == "remote"); remote.IsExpanded = true;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(520, 900)); await Task.Delay(350); dialog.UpdateLayout();
            Check(Field<ScrollViewer>(dialog, "page").ScrollableWidth < 1 && Within((ToggleSwitch)remote.Content, dialog), context + ": narrow skill rows keep visible toggles without horizontal overflow");
            await Tab("files");
            var files = (StackPanel)Field<Dictionary<string, FrameworkElement>>(dialog, "pages")["files"];
            Check(files.Children[0] is Button add && add.Content as string == DesktopResources.Get("PluginAddFiles") && files.AllowDrop
                && !files.Children.OfType<TextBlock>().Any(), context + ": Files starts with Add Files, without the explanatory paragraph");
            Check(Descendants(files).OfType<TextBox>().Any(input => input.Text == "package data" && !input.IsReadOnly)
                && Descendants(files).OfType<Button>().Any(button => button.Content as string == DesktopResources.Get("Download")), context + ": file editing and download actions remain");
            Check(JsonNode.DeepEquals(original.Manifest, before.Manifest) && original.Files.Count == before.Files.Count, context + ": all changes remain isolated until Save");
            InvokeButton(Button("PrimaryButton")); await shown;
            Check(dialog.Result is { } saved && saved.Manifest["license"]!.GetValue<string>() == "Arbitrary non-SPDX terms"
                && NormalizeComposerLines(saved.Description) == "First line\nSecond line" && saved.Keywords.SequenceEqual(["one, whole label", "added with button", "second label"])
                && !saved.Manifest.ContainsKey("labels") && saved.Skills().Single().Directory == "remote"
                && JsonNode.DeepEquals(saved.Manifest["extensions"], before.Manifest["extensions"]) && JsonNode.DeepEquals(saved.Mcp, before.Mcp),
                context + ": Save commits complete metadata and skill draft while leaving Model Context unchanged");
        }
        finally { if (shown.Status == Windows.Foundation.AsyncStatus.Started) { dialog.CancelAndHide(); await shown; } }

        var readOnly = Dialog(dialog.Result ?? original, true); shown = readOnly.ShowAsync(); await Task.Delay(100);
        try
        {
            Check(Descendants(readOnly).OfType<TextBox>().Where(input => input.Name.StartsWith("Plugin_")).All(input => input.IsReadOnly && input.PlaceholderText.Length > 0), context + ": details remain read-only with placeholders");
            var tabs = Field<NavigationView>(readOnly, "tabs");
            tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().Single(item => item.Tag as string == "keywords"); await Task.Delay(60);
            Check(!Descendants(readOnly).OfType<Control>().Any(control => control.Name is "PluginLabelInput" or "PluginLabelAdd" or "PluginLabelRemove")
                && Descendants(readOnly).OfType<Grid>().Any(row => row.Name == "PluginLabelRow"), context + ": details show labels without editing controls");
            tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().Single(item => item.Tag as string == "skills"); await Task.Delay(60);
            Check(Descendants(readOnly).OfType<SettingsExpander>().Where(row => row.Name == "PluginSkillCard").All(row => row.Content is ToggleSwitch { IsEnabled: false }),
                context + ": skill details expand without allowing selection changes");
        }
        finally { readOnly.Hide(); await shown; }

        var create = Dialog(null, identity: "Signed-in author"); shown = create.ShowAsync(); await Task.Delay(100);
        try
        {
            var pages = Field<Dictionary<string, FrameworkElement>>(create, "pages");
            Check(((StackPanel)pages["general"]).Children.OfType<TextBox>().Single(input => input.Name == "Plugin_name").IsReadOnly == false,
                context + ": create permits entering a plugin name");
            Check(((StackPanel)pages["author"]).Children.OfType<TextBox>().All(input => input.IsReadOnly && input.PlaceholderText.Length > 0),
                context + ": identity-prefilled Author fields keep their read-only rules");
        }
        finally { create.Hide(); await shown; }
        Check(create.Result is null && JsonNode.DeepEquals(original.Manifest, before.Manifest), context + ": Cancel leaves the original package unchanged");

        var reopened = Dialog(dialog.Result!); shown = reopened.ShowAsync(); await Task.Delay(100);
        try
        {
            var license = Descendants(reopened).OfType<TextBox>().Single(input => input.Name == "Plugin_license");
            Check(license.Text == "Arbitrary non-SPDX terms", context + ": reopening restores the saved License");
            license.Text = "";
            await WaitForProviderUiAsync(() => DesktopAgent.Text(Field<DesktopAgentPlugin>(reopened, "draft").Manifest["license"]) == "", "Clearing License did not update the native draft");
            var failSave = true;
            reopened.SaveAsync = (_, _) => failSave ? Task.FromException(new IOException("disk")) : Task.CompletedTask;
            InvokeButton(Descendants(reopened).OfType<Button>().Single(button => button.Name == "PrimaryButton"));
            await WaitForProviderUiAsync(() => !Field<bool>(reopened, "working") && Field<TextBlock>(reopened, "error").Visibility == Visibility.Visible, "Failed plugin save did not finish");
            Check(reopened.Result is null && shown.Status == Windows.Foundation.AsyncStatus.Started && license.Text == ""
                && dialog.Result!.Manifest["license"]!.GetValue<string>() == "Arbitrary non-SPDX terms", context + ": failed save retains the clear-License draft and previous package");
            failSave = false; InvokeButton(Descendants(reopened).OfType<Button>().Single(button => button.Name == "PrimaryButton")); await shown;
            Check(reopened.Result is { } cleared && DesktopAgent.Text(cleared.Manifest["license"]) == "", context + ": retry saves a cleared optional License (saved=" + (reopened.Result?.Manifest["license"]?.ToJsonString() ?? "missing result/value") + ")");
        }
        finally { if (shown.Status == Windows.Foundation.AsyncStatus.Started) { reopened.CancelAndHide(); await shown; } }

        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = Dialog(original); shown = canceled.ShowAsync(); await Task.Delay(100);
        try
        {
            var tabs = Field<NavigationView>(canceled, "tabs");
            tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().Single(item => item.Tag as string == "skills"); await Task.Delay(60);
            var toggle = Descendants(canceled).OfType<SettingsExpander>().Single(row => row.Name == "PluginSkillCard" && row.Tag as string == "remote").Content as ToggleSwitch;
            toggle!.IsOn = true;
            await WaitForProviderUiAsync(() => Field<bool>(canceled, "working"), "Pending plugin snapshot did not start");
            canceled.CancelAndHide(); await shown;
            await WaitForProviderUiAsync(() => !Field<bool>(canceled, "working"), "Canceled plugin snapshot did not finish");
            Check(canceled.Result is null && original.Files.Count == before.Files.Count && JsonNode.DeepEquals(original.Manifest, before.Manifest),
                context + ": closing during snapshot cancels work without publishing a selection or modifying the original");
        }
        finally { pending.TrySetCanceled(); pending = null; if (shown.Status == Windows.Foundation.AsyncStatus.Started) { canceled.CancelAndHide(); await shown; } }
    }

    private async Task CheckPluginEditorKeyboardAsync(ElementTheme theme)
    {
        var root = new Grid { RequestedTheme = theme }; window!.Content = root;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 900)); window.Activate();
        await WaitForProviderUiAsync(() => root.XamlRoot is not null, "Plugin keyboard fixture visual root did not load");
        var dialog = new PluginEditDialog(null, false, AgentPluginPackages.DefaultNamespace, [], (_, _) => Task.FromResult<IReadOnlyList<DesktopSkillFile>>([]), [])
            { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        var shown = dialog.ShowAsync(); await Task.Delay(120);
        try
        {
            Descendants(dialog).OfType<TextBox>().Single(input => input.Name == "Plugin_name").Text = "keyboard.fixture";
            var tabs = Field<NavigationView>(dialog, "tabs");
            tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().Single(item => item.Tag as string == "keywords"); await Task.Delay(80);
            var input = Descendants(dialog).OfType<TextBox>().Single(field => field.Name == "PluginLabelInput"); input.Text = "  keyboard label  ";
            await PressComposerKeyAsync(input, Windows.System.VirtualKey.Enter);
            Check(Descendants(dialog).OfType<Grid>().Single(row => row.Name == "PluginLabelRow").Tag as string == "keyboard label"
                && input.Text == "" && shown.Status == Windows.Foundation.AsyncStatus.Started && dialog.Result is null,
                "Plugin keyboard / " + theme + ": native Enter adds a trimmed label without saving or closing");
            input.Text = "keyboard label"; await PressComposerKeyAsync(input, Windows.System.VirtualKey.Enter);
            Check(Descendants(dialog).OfType<Grid>().Count(row => row.Name == "PluginLabelRow") == 1 && input.Text == "",
                "Plugin keyboard / " + theme + ": native Enter prevents exact duplicates");
        }
        finally { dialog.CancelAndHide(); await shown; }
    }
}
