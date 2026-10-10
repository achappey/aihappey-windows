using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckSkillEditorAsync(ElementTheme theme)
    {
        var root = new Grid { RequestedTheme = theme }; window!.Content = root;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 900)); window.Activate(); await Task.Delay(100);
        root.UpdateLayout();
        var context = "Skill editor / " + theme;
        var original = new DesktopSkillDraft { Name = "fixture-skill", Description = "Fixture description", Instructions = "# Instructions",
            Files = [new("references/info.md", Encoding.UTF8.GetBytes("Info")), new("assets/file.bin", [1, 2, 3])] };
        original.Frontmatter["license"] = "Apache-2.0"; original.Frontmatter["metadata"] = new JsonObject { ["author"] = "fixture" };
        var create = new SkillEditDialog(new(), false) { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        DesktopSkillDraft? saved = null; create.SaveAsync = (value, _) => { saved = value; return Task.CompletedTask; };
        SystemAppearance.PrepareDialog(create); var shown = create.ShowAsync(); await Task.Delay(120);
        Check(!create.IsPrimaryButtonEnabled && Descendants(create).OfType<NavigationView>().Single().MenuItems.Count == 3, context + ": create has three native tabs and incomplete Save disabled");
        var name = Descendants(create).OfType<TextBox>().Single(b => b.Name == "SkillName"); name.Text = "My New Skill";
        var description = Descendants(create).OfType<TextBox>().Single(b => b.Name == "SkillDescription"); description.Text = "When asked for a fixture, use this skill.";
        await WaitForSkillUiAsync(() => create.Draft.Name == "my-new-skill" && create.IsPrimaryButtonEnabled);
        Check(create.Draft.Name == "my-new-skill" && create.IsPrimaryButtonEnabled, context + $": browser-style name normalization and empty-body support (name={create.Draft.Name}, description={create.Draft.Description}, enabled={create.IsPrimaryButtonEnabled})");
        description.Text = new string('x', 1025); await WaitForSkillUiAsync(() => create.Draft.Description.Length == 1025 && !create.IsPrimaryButtonEnabled);
        Check(!create.IsPrimaryButtonEnabled, context + ": overlong description disables Save");
        description.Text = "Description";
        await WaitForSkillUiAsync(() => create.Draft.Description == "Description" && create.IsPrimaryButtonEnabled);
        var tabs = Descendants(create).OfType<NavigationView>().Single(); SelectNativeTab(tabs, "content"); await Task.Delay(60);
        Descendants(create).OfType<TextBox>().Single(b => b.Name == "SkillInstructions").Text = "# New instructions";
        await WaitForSkillUiAsync(() => create.Draft.Instructions == "# New instructions");
        SelectNativeTab(tabs, "general"); await Task.Delay(60);
        Check(Descendants(create).OfType<TextBox>().Single(b => b.Name == "SkillDescription").Text == "Description", context + ": switching tabs preserves draft fields");
        InvokeButton(Descendants(create).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await shown;
        Check(saved?.Name == "my-new-skill" && saved.Instructions == "# New instructions" && create.Result is not null, context + ": create Save returns validated isolated draft");

        var edit = new SkillEditDialog(original, true) { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        var fail = true; saved = null;
        edit.SaveAsync = (value, _) => { if (fail) throw new IOException("disk failure"); saved = value; return Task.CompletedTask; };
        SystemAppearance.PrepareDialog(edit); shown = edit.ShowAsync(); await Task.Delay(120);
        Check(!Descendants(edit).OfType<TextBox>().Single(b => b.Name == "SkillName").IsEnabled && JsonNode.DeepEquals(edit.Draft.Frontmatter, original.Frontmatter),
            context + ": immutable name and optional metadata survive opening");
        Descendants(edit).OfType<TextBox>().Single(b => b.Name == "SkillDescription").Text = "Edited description";
        await WaitForSkillUiAsync(() => edit.Draft.Description == "Edited description");
        tabs = Descendants(edit).OfType<NavigationView>().Single(); SelectNativeTab(tabs, "files"); await Task.Delay(80); edit.UpdateLayout();
        Check(Descendants(edit).OfType<SettingsCard>().Count(c => c.Name == "SkillFileCard") == 2
            && Descendants(edit).OfType<Button>().Where(b => b.Name.StartsWith("SkillFile")).All(b => AutomationProperties.GetName(b).Length > 0),
            context + ": file cards and actions have accessible labels");
        var path = Descendants(edit).OfType<TextBox>().Single(b => b.Name == "SkillFilePath" && b.Tag as string == "references/info.md"); path.Text = "../bad";
        InvokeButton(Descendants(edit).OfType<Button>().Single(b => b.Name == "SkillFileRename" && b.Tag as string == "references/info.md"));
        Check(edit.Draft.Files.Any(f => f.Path == "references/info.md") && Descendants(edit).OfType<TextBlock>().Single(t => t.Name == "SkillEditorFeedback").Visibility == Visibility.Visible,
            context + ": unsafe file rename rejected with feedback and original file retained");
        path.Text = "references/renamed.md"; InvokeButton(Descendants(edit).OfType<Button>().Single(b => b.Name == "SkillFileRename" && b.Tag as string == "references/info.md"));
        Check(edit.Draft.Files.Any(f => f.Path == "references/renamed.md"), context + ": safe relative path rename updates draft");
        await Task.Delay(80); edit.UpdateLayout();
        InvokeButton(Descendants(edit).OfType<Button>().Single(b => b.Name == "SkillFileRemove" && b.Tag as string == "assets/file.bin"));
        Check(edit.Draft.Files.Count == 1 && original.Files.Count == 2 && original.Description == "Fixture description", context + ": resource removal and form edits never mutate original");
        await Task.Delay(100); edit.UpdateLayout();
        foreach (var width in new[] { 500, 1100 })
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 900)); await Task.Delay(120); edit.UpdateLayout();
            foreach (var key in new[] { "general", "content", "files" })
            {
                // Top NavigationView may move items into native overflow on resize; do not
                // construct an automation peer for an item detached from the primary strip.
                tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().Single(i => i.Tag as string == key);
                await Task.Delay(80); edit.UpdateLayout();
                Check(Descendants(edit).OfType<ScrollViewer>().All(v => v.ScrollableWidth < 1)
                    && Descendants(edit).OfType<TextBox>().All(b => Within(b, edit)), context + $": {key} fits at {width}px without horizontal clipping");
            }
        }
        edit.RequestedTheme = theme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light; await Task.Delay(60);
        Check(edit.Draft.Description == "Edited description", context + ": native theme switch preserves form");
        InvokeButton(Descendants(edit).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await Task.Delay(100);
        Check(shown.Status == Windows.Foundation.AsyncStatus.Started && edit.Result is null && edit.IsPrimaryButtonEnabled,
            context + ": disk save failure retains editable modal and draft");
        fail = false; InvokeButton(Descendants(edit).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await shown;
        Check(saved?.Files.Count == 1 && saved.Frontmatter["license"]!.GetValue<string>() == "Apache-2.0", context + ": retry saves resources and optional frontmatter");
        var cancel = new SkillEditDialog(original, true) { XamlRoot = root.XamlRoot }; shown = cancel.ShowAsync(); await Task.Delay(100);
        Descendants(cancel).OfType<TextBox>().Single(b => b.Name == "SkillDescription").Text = "Discarded";
        InvokeButton(Descendants(cancel).OfType<Button>().Single(b => b.Name == "CloseButton")); await shown;
        Check(cancel.Result is null && original.Description == "Fixture description", context + ": cancel discards all changes");
        var current = true; var stale = new SkillEditDialog(original, true, () => current) { XamlRoot = root.XamlRoot }; var saves = 0;
        stale.SaveAsync = (_, _) => { saves++; return Task.CompletedTask; }; shown = stale.ShowAsync(); await Task.Delay(100); current = false;
        InvokeButton(Descendants(stale).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await Task.Delay(80);
        Check(saves == 0 && stale.Result is null, context + ": account/API change blocks Save before persistence"); stale.CancelAndHide(); await shown;
        await CheckLocalSkillAgentAsync(root, original, theme);
        await CheckLocalSkillShellAsync(original, theme);
    }
    private static async Task WaitForSkillUiAsync(Func<bool> ready)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (ready()) return;
            await Task.Delay(25);
        }
        throw new InvalidOperationException("Expected skill editor UI transition did not finish.");
    }
    private async Task CheckLocalSkillAgentAsync(Grid root, DesktopSkillDraft draft, ElementTheme theme)
    {
        window!.Content = root; window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 900)); await Task.Delay(80);
        var session = new DesktopSession(new AgentUiHost(), new AgentUiRuntime(), new());
        using var handler = new AgentUiHandler(); using var http = new HttpClient(handler);
        var catalog = new DesktopCatalogClient(new DesktopChatClient(session, http), http);
        var descriptor = new DesktopSkill("local:fixture", draft.Name, draft.Description, "local", "2"); var downloads = 0;
        var agent = DesktopAgent.Parse("""{"name":"LocalSkillsAgent","description":"Local fixture","instructions":"Use selected skills.","model":{"id":"openai/fixture"}}""");
        var dialog = new AgentEditDialog(agent, true, session, catalog, http, [new("openai/fixture", "Fixture")], localSkills: [descriptor],
            localSkillArchive: (_, _) => { downloads++; return Task.FromResult(DesktopSkillPackages.Export(draft)); }) { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        var shown = dialog.ShowAsync(); await Task.Delay(100); SelectNativeTab(Descendants(dialog).OfType<NavigationView>().Single(), "skills"); await Task.Delay(200);
        var toggle = Descendants(dialog).OfType<ToggleSwitch>().Single(t => t.Name == "AgentSkillEnabled" && t.Tag as string == descriptor.Id);
        toggle.IsOn = true; await Task.Delay(100);
        var inline = (dialog.Draft.Definition["skills"] as JsonArray)?.OfType<JsonObject>().Single();
        Check(downloads == 1 && DesktopAgent.Text(inline?["type"]) == "inline" && inline?["skill_id"] is null
            && DesktopAgent.Text(inline?["source"]?["media_type"]) == "application/zip", $"Skill editor / {theme}: agent includes local skill as inline archive, never remote reference");
        var row = Descendants(dialog).OfType<SettingsExpander>().Single(e => e.Tag as string == descriptor.Id); row.IsExpanded = true; await Task.Delay(80);
        Check(!Descendants(row).OfType<ComboBox>().Single(b => b.Name == "AgentSkillMode").IsEnabled, $"Skill editor / {theme}: local skill cannot switch to backend reference mode");
        dialog.Hide(); await shown; await session.DisposeAsync();
    }
    private async Task CheckLocalSkillShellAsync(DesktopSkillDraft draft, ElementTheme theme)
    {
        var session = new DesktopSession(new UiHost(true), new UiRuntime(), new()); var shell = new ChatShell(session) { RequestedTheme = theme };
        window!.Content = shell; window.Activate(); await Task.Delay(200); await HistoryIdleAsync(shell);
        try
        {
            var stored = await Field<DesktopLocalSkillStore>(shell, "localSkillStore").SaveAsync(session.HistoryPartition, draft, null, default);
            await (Task)Call(shell, "RefreshLocalSkillsAsync", CancellationToken.None)!;
            session.Settings.Chat.EnabledSkillIds = [stored.Id]; Call(shell, "RenderContextTags");
            await Task.Delay(80); shell.UpdateLayout();
            Check(Descendants(shell).OfType<Border>().Single(b => b.Name == "EnabledSkillBadge").Tag as string == stored.Id, $"Skill editor / {theme}: stored local skill resolves to enabled badge");
            var runtime = (McpTurnSnapshot)Call(shell, "CaptureSkillRuntime", session.Settings.Chat)!;
            var activated = await runtime.CallAsync("activate_skill", JsonSerializer.SerializeToElement(new { skill_id = stored.Id }), "call", "en", default);
            Check(!activated.GetProperty("isError").GetBoolean() && runtime.Context.Single().GetRawText().Contains(stored.Id), $"Skill editor / {theme}: local chat skill activates without remote catalog");
            var pageType = typeof(ChatShell).Assembly.GetType("AIHappey.Desktop.Core.DesktopPage")!; Call(shell, "ShowPage", Enum.Parse(pageType, "Skills"));
            await Task.Delay(80); shell.UpdateLayout();
            var plus = Field<Button>(shell, "addSkill");
            Check(plus.Visibility == Visibility.Visible && ((MenuFlyout)plus.Flyout).Items.Count == 2 && Field<Button>(shell, "addAgent").Visibility == Visibility.Collapsed,
                $"Skill editor / {theme}: Skills header has dedicated create/import menu");
            var overview = Field<UserControl>(shell, "skillsOverview"); var tabs = Descendants(overview).OfType<NavigationView>().Single();
            tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().Single(i => i.Tag as string == "local");
            await Task.Delay(100); shell.UpdateLayout();
            Check(Descendants(overview).OfType<Button>().Any(b => b.Name == "CatalogSkillEdit") && Descendants(overview).OfType<Button>().Any(b => b.Name == "CatalogDelete")
                && Descendants(overview).OfType<Button>().Any(b => b.Name == "CatalogDownload"), $"Skill editor / {theme}: Local cards expose edit/delete/export");
            Call(shell, "SetOverviewBusy", true); Check(!plus.IsEnabled, $"Skill editor / {theme}: busy state disables Skills add action"); Call(shell, "SetOverviewBusy", false);
            await (Task)Call(shell, "LoadOverviewAsync", overview, CancellationToken.None, false)!; await Task.Delay(80); shell.UpdateLayout();
            Check(Field<IReadOnlyList<DesktopSkill>>(shell, "localSkills").Count == 1 && Descendants(overview).OfType<Border>().Any(b => b.Name == "CatalogCard"),
                $"Skill editor / {theme}: local catalog remains usable when remote discovery fails");
            var selection = await (Task<DesktopSkillSelection>)Call(shell, "LoadSkillSelectionAsync", CancellationToken.None)!;
            Check(selection.Items.Any(s => s.Id == stored.Id && s.Origin == "local"), $"Skill editor / {theme}: chat settings include local skills offline");
            var editTask = (Task)Call(shell, "EditSkillAsync", null!, plus)!;
            await WaitForSkillUiAsync(() => Field<SkillEditDialog?>(shell, "skillEditor") is not null);
            var editor = Field<SkillEditDialog>(shell, "skillEditor"); await Task.Delay(120);
            Descendants(editor).OfType<TextBox>().Single(b => b.Name == "SkillName").Text = "created-in-shell";
            Descendants(editor).OfType<TextBox>().Single(b => b.Name == "SkillDescription").Text = "Created through Skills actions";
            await WaitForSkillUiAsync(() => editor.IsPrimaryButtonEnabled);
            InvokeButton(Descendants(editor).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await editTask;
            var created = Field<IReadOnlyList<DesktopSkill>>(shell, "localSkills").Single(s => s.Name == "created-in-shell");
            var settings = await SettingsStore.LoadAsync(session.DataDirectory, new());
            Check(settings.Chat.EnabledSkillIds.Contains(created.Id) && session.Settings.Chat.EnabledSkillIds.Contains(created.Id),
                $"Skill editor / {theme}: shell create persists and automatically enables local skill");
            var item = new CatalogItem(CatalogKind.Skill, stored.Id, stored.Name, stored.Description) { Origin = CatalogOrigin.Local };
            await (Task)Call(shell, "ToggleCatalogFavoriteAsync", item)!;
            var deletion = (Task)Call(shell, "DeleteSkillAsync", item)!;
            var confirmation = await OpenDialogAsync(shell); await Task.Delay(100);
            InvokeButton(Descendants(confirmation).OfType<Button>().Single(b => b.Name == "CloseButton")); await deletion;
            Check(Field<IReadOnlyList<DesktopSkill>>(shell, "localSkills").Any(s => s.Id == stored.Id), $"Skill editor / {theme}: canceled delete retains skill");
            deletion = (Task)Call(shell, "DeleteSkillAsync", item)!; confirmation = await OpenDialogAsync(shell); await Task.Delay(100); Confirm(confirmation); await deletion;
            settings = await SettingsStore.LoadAsync(session.DataDirectory, new());
            Check(!settings.Chat.EnabledSkillIds.Contains(stored.Id) && !Field<HashSet<string>>(shell, "favorites").Contains(item.Key)
                && !Field<IReadOnlyList<DesktopSkill>>(shell, "localSkills").Any(s => s.Id == stored.Id), $"Skill editor / {theme}: confirmed delete removes archive, selection, and favorite");
            var createdItem = new CatalogItem(CatalogKind.Skill, created.Id, created.Name, created.Description) { Origin = CatalogOrigin.Local };
            deletion = (Task)Call(shell, "DeleteSkillAsync", createdItem)!; confirmation = await OpenDialogAsync(shell); await Task.Delay(100); Confirm(confirmation); await deletion;
            Check(tabs.MenuItems.OfType<NavigationViewItem>().Any(i => i.Tag as string == "local"), $"Skill editor / {theme}: Local tab retained when empty");
        }
        finally { await shell.ShutdownAsync(); window.Content = null; if (Directory.Exists(session.DataDirectory)) Directory.Delete(session.DataDirectory, true); }
    }
}
