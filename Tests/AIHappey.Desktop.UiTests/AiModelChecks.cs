using System.Reflection;
using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckAiModelsAsync(ElementTheme theme)
    {
        var root = new Grid { RequestedTheme = theme };
        window!.Content = root; window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 850)); window.Activate();
        await Task.Delay(100);
        var context = "AI model settings / " + theme;
        var catalog = AiModelCatalog.Types.SelectMany((type, index) => new[]
        {
            new ChatTarget("p/" + type + "-old", type + " old") { ModelType = type, Created = 1 },
            new ChatTarget("p/" + type + "-new", type + " new") { ModelType = type, Created = 100 + index }
        }).ToArray();
        var original = new DesktopSettings { AllowedToolList = ["keep-tool"] };
        foreach (var type in AiModelCatalog.Types) original.AiModels.SetDefault(type, "p/" + type + "-old");
        original.ModelContext.ToolTimeoutMinutes = 13;
        var dialog = new SettingsDialog(original, true, "en", catalog) { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        SystemAppearance.PrepareDialog(dialog);
        var shown = dialog.ShowAsync(); await Task.Delay(100);
        var tabs = (NavigationView)dialog.FindName("Tabs");
        tabs.SelectedItem = dialog.FindName("ArtificialIntelligenceTab"); dialog.UpdateLayout();
        var view = dialog.AiModelView;
        Check(view.PaneDisplayMode == NavigationViewPaneDisplayMode.Top && view.MenuItems.Count == 9
            && view.MenuItems.OfType<NavigationViewItem>().Select(x => (string)x.Tag).SequenceEqual(AiModelCatalog.Types),
            context + ": nine horizontal tabs retain browser order");
        foreach (var type in AiModelCatalog.Types)
        {
            view.SelectedItem = view.MenuItems.OfType<NavigationViewItem>().Single(x => (string)x.Tag == type);
            dialog.UpdateLayout();
            var picker = Descendants(view).OfType<AutoSuggestBox>().Single(x => x.Name == "AiDefaultModel_" + type);
            var options = ((IEnumerable<ChatTarget>)picker.ItemsSource).ToArray();
            Check(picker.Text == "p/" + type + "-old" && options.Length == 2
                && options.All(x => x.ModelType == type) && options[0].Created > options[1].Created,
                context + ": " + type + " default and type-specific newest-first catalog");
            Check(!string.IsNullOrEmpty(AutomationProperties.GetName(picker)), context + ": accessible " + type + " picker");
            picker.Text = "p/" + type + "-new";
            InvokeButton(Descendants(picker).OfType<Button>().Single(x => x.Name == "QueryButton"));
            Check(picker.Text == "p/" + type + "-new", context + ": native query button commits available " + type + " model");
            var switches = Descendants(view).OfType<ToggleSwitch>().Where(x => x.Name == "AiChatWith_" + type).ToArray();
            Check(switches.Length == (type is "image" or "video" or "speech" or "transcription" ? 1 : 0),
                context + ": Chat with switch only on supported type " + type);
            if (switches.Length == 1)
            {
                Check(!switches[0].IsOn && !string.IsNullOrEmpty(AutomationProperties.GetName(switches[0])), context + ": switch is off and labeled");
                switches[0].IsOn = true;
            }
        }
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(500, 850)); await Task.Delay(100); dialog.UpdateLayout();
        Check(((Grid)dialog.FindName("Layout")).ActualWidth <= root.ActualWidth && tabs.IsPaneToggleButtonVisible
            && view.ActualWidth <= root.ActualWidth, context + ": narrow layout fits viewport and retains native tab overflow");
        InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await shown;
        Check(dialog.Result is { } saved && AiModelCatalog.Types.All(t => saved.AiModels.DefaultFor(t) == "p/" + t + "-new")
            && new[] { "image", "video", "speech", "transcription" }.All(saved.AiModels.AllowsChat)
            && saved.ModelContext.ToolTimeoutMinutes == 13 && saved.AllowedToolList.SequenceEqual(["keep-tool"])
            && !original.AiModels.ChatWithImageModels && original.AiModels.LanguageModel == "p/language-old",
            context + ": Save isolates all defaults/switches and preserves unrelated preferences");

        var canceled = new SettingsDialog(original, true, "en", catalog) { XamlRoot = root.XamlRoot };
        shown = canceled.ShowAsync(); await Task.Delay(100);
        ((NavigationView)canceled.FindName("Tabs")).SelectedItem = canceled.FindName("ArtificialIntelligenceTab"); canceled.UpdateLayout();
        InvokeButton(Descendants(canceled.AiModelView).OfType<Button>().Single(x => x.Name == "AiClearDefault_language"));
        canceled.Hide(); await shown;
        Check(canceled.Result is null && original.AiModels.LanguageModel == "p/language-old", context + ": Cancel discards a native clear-default edit");

        var unavailable = original.Clone(); unavailable.AiModels.LanguageModel = "missing";
        var missing = new SettingsDialog(unavailable, true, "en", catalog) { XamlRoot = root.XamlRoot };
        shown = missing.ShowAsync(); await Task.Delay(100);
        ((NavigationView)missing.FindName("Tabs")).SelectedItem = missing.FindName("ArtificialIntelligenceTab"); missing.UpdateLayout();
        Check(Descendants(missing.AiModelView).OfType<TextBlock>().Any(x => x.Text == DesktopResources.Get("AiDefaultUnavailable")),
            context + ": unavailable preference remains visible with an explanatory state");
        InvokeButton(Descendants(missing).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await shown;
        Check(missing.Result?.AiModels.LanguageModel == "missing", context + ": viewing/saving does not auto-replace unavailable defaults");

        var noPreference = new DesktopSettings();
        var effective = new SettingsDialog(noPreference, true, "en", catalog) { XamlRoot = root.XamlRoot };
        shown = effective.ShowAsync(); await Task.Delay(100);
        ((NavigationView)effective.FindName("Tabs")).SelectedItem = effective.FindName("ArtificialIntelligenceTab"); effective.UpdateLayout();
        Check(Descendants(effective.AiModelView).OfType<AutoSuggestBox>().Single(x => x.Name == "AiDefaultModel_language").Text == "p/language-new",
            context + ": unset language preference displays its effective newest-language fallback");
        InvokeButton(Descendants(effective).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await shown;
        Check(effective.Result?.AiModels.LanguageModel is null, context + ": viewing fallback never creates an explicit default preference");

        var deferred = new TaskCompletionSource<IReadOnlyList<ChatTarget>>();
        var asyncDialog = new SettingsDialog(original, true, "en", loadAiModels: _ => deferred.Task) { XamlRoot = root.XamlRoot };
        shown = asyncDialog.ShowAsync(); await Task.Delay(100);
        ((NavigationView)asyncDialog.FindName("Tabs")).SelectedItem = asyncDialog.FindName("ArtificialIntelligenceTab"); asyncDialog.UpdateLayout();
        Check(Descendants(asyncDialog.AiModelView).OfType<AutoSuggestBox>().Single(x => x.Name == "AiDefaultModel_language").IsEnabled == false,
            context + ": uncached AI catalog shows disabled pickers while loading");
        deferred.SetResult(catalog); await Task.Delay(100); asyncDialog.UpdateLayout();
        Check(Descendants(asyncDialog.AiModelView).OfType<AutoSuggestBox>().Single(x => x.Name == "AiDefaultModel_language").IsEnabled,
            context + ": settings opened outside model mode accept an asynchronously loaded full AI catalog");
        asyncDialog.Hide(); await shown;

        var failed = new SettingsDialog(original, true, "en", loadAiModels: _ => Task.FromException<IReadOnlyList<ChatTarget>>(new IOException("fixture failure")))
            { XamlRoot = root.XamlRoot };
        shown = failed.ShowAsync(); await Task.Delay(100);
        ((NavigationView)failed.FindName("Tabs")).SelectedItem = failed.FindName("ArtificialIntelligenceTab"); failed.UpdateLayout();
        Check(Descendants(failed.AiModelView).OfType<TextBlock>().Any(x => x.Text == DesktopResources.Get("AiModelsLoadFailed")),
            context + ": catalog failure is a sanitized native state, not a settings failure");
        InvokeButton(Descendants(failed).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await shown;
        Check(failed.Result?.AiModels.LanguageModel == original.AiModels.LanguageModel, context + ": failed catalog loading preserves saved defaults");

        var lateCatalog = new TaskCompletionSource<IReadOnlyList<ChatTarget>>();
        CancellationToken catalogToken = default;
        var closing = new SettingsDialog(original, true, "en", loadAiModels: ct => { catalogToken = ct; return lateCatalog.Task; }) { XamlRoot = root.XamlRoot };
        shown = closing.ShowAsync(); await Task.Delay(100); closing.Hide(); await shown;
        Check(catalogToken.IsCancellationRequested, context + ": closing settings cancels catalog lifetime");
        lateCatalog.SetResult(catalog); await Task.Delay(50);
        Check(closing.Result is null, context + ": late catalog arrival cannot save or mutate a canceled draft");

        var empty = new AiModelSettingsView(new(), []);
        Check(Descendants(empty).OfType<AutoSuggestBox>().All(x => !((IEnumerable<ChatTarget>)x.ItemsSource).Any()), context + ": empty per-type catalogs contain no fake options");
        var loading = new AiModelSettingsView(new());
        Check(Descendants(loading).OfType<AutoSuggestBox>().All(x => !x.IsEnabled), context + ": model pickers disabled during catalog loading");
        loading.SetCatalog(catalog);
        Check(Descendants(loading).OfType<AutoSuggestBox>().All(x => x.IsEnabled), context + ": catalog arrival enables selection without changing preferences");

        var session = new DesktopSession(new UiHost(true), new UiRuntime(), new());
        var shell = new ChatShell(session);
        try
        {
            typeof(ChatShell).GetField("targets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, catalog);
            var picker = Field<AutoSuggestBox>(shell, "target");
            void Call(string method, params object[] arguments) => typeof(ChatShell).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, arguments);
            Call("UpdateTargetSuggestions", "");
            Check(((IEnumerable<ChatTarget>)picker.ItemsSource).All(x => x.ModelType == "language"), context + ": actual chat suggestions initially expose language only");
            session.Settings.AiModels.ChatWithImageModels = true;
            picker.Text = "p/language-old";
            Call("UpdateTargetSuggestions", "");
            Check(picker.Text == "p/language-old" && ((IEnumerable<ChatTarget>)picker.ItemsSource).Any(x => x.ModelType == "image"),
                context + ": eligibility refresh exposes enabled types without changing current selection");
            session.Settings.AiModels.LanguageModel = "p/language-old";
            picker.Text = "p/image-new";
            Call("NewConversation");
            Check(picker.Text == "p/language-old", context + ": new AI chat replaces previous selection with preferred language model");
            session.Settings.AiModels.LanguageModel = "missing";
            Call("NewConversation");
            Check(picker.Text == "p/language-new", context + ": new AI chat falls back to newest language, never newest image");
            Call("UpdateMode", ServiceKind.Agents);
            picker.Text = "agent-id";
            Call("NewConversation");
            Check(picker.Text == "agent-id", context + ": new agent chat does not apply language preference");
        }
        finally { await shell.ShutdownAsync(); }
    }
}
