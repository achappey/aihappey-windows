using System.Text.Json;
using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ModelContextProtocol.Protocol;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private static ElicitRequestParams UiElicitRequest() => JsonSerializer.Deserialize<ElicitRequestParams>("""
    {"message":"Please supply additional information","requestedSchema":{"type":"object","properties":{
      "text":{"type":"string","title":"Name","default":"Ada","minLength":2},
      "number":{"type":"number","minimum":0,"maximum":100,"default":1.5},
      "integer":{"type":"integer","title":"Count"},"boolean":{"type":"boolean","default":false},
      "date":{"type":"string","format":"date"},"timestamp":{"type":"string","format":"date-time"},
      "email":{"type":"string","format":"email"},"uri":{"type":"string","format":"uri"},
      "single":{"type":"string","enum":["a","b"],"default":"a"},
      "titled":{"type":"string","oneOf":[{"const":"a","title":"Alpha"}]},
      "legacy":{"type":"string","enum":["a"],"enumNames":["Alpha"]},
      "multi":{"type":"array","items":{"type":"string","enum":["a","b"]}},
      "titledMulti":{"type":"array","items":{"anyOf":[{"const":"a","title":"Alpha"}]}}
    },"required":["integer","boolean"]}}
    """)!;

    private async Task CheckElicitationAsync(ElementTheme theme)
    {
        var root = new Grid { RequestedTheme = theme };
        window!.Content = root; window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 850)); window.Activate();
        await Task.Delay(100);
        var context = "Model context / " + theme;
        var original = new DesktopSettings();
        var settings = new SettingsDialog(original, true, "en") { XamlRoot = root.XamlRoot, RequestedTheme = theme };
        SystemAppearance.PrepareDialog(settings);
        var shown = settings.ShowAsync(); await Task.Delay(100);
        var tabs = (NavigationView)settings.FindName("Tabs");
        tabs.SelectedItem = settings.FindName("ModelContextTab"); settings.UpdateLayout();
        var nested = (NavigationView)settings.FindName("ModelContextPage");
        Check(nested.PaneDisplayMode == NavigationViewPaneDisplayMode.Top && nested.MenuItems.Count == 2, context + ": vertical Model context opens horizontal Client/Extensions navigation");
        Check(((ToggleSwitch)settings.FindName("FormElicitation")).IsOn && !((ToggleSwitch)settings.FindName("UrlElicitation")).IsOn
            && !((ToggleSwitch)settings.FindName("UrlElicitation")).IsEnabled, context + ": form enabled by default and URL off/disabled");
        Check(((NumberBox)settings.FindName("ToolTimeout")).Value == 5 && ((ToggleSwitch)settings.FindName("ResetTimeoutOnProgress")).IsOn,
            context + ": browser-aligned tool defaults");
        Check(Descendants(settings).OfType<TextBlock>().Any(t => t.Text == "Elicitation") && Descendants(settings).OfType<TextBlock>().Any(t => t.Text == "Tools"),
            context + ": native resources localize Client sections");
        nested.SelectedItem = settings.FindName("McpExtensionsTab"); settings.UpdateLayout();
        Check(((ToggleSwitch)settings.FindName("EnableMcpApps")).IsOn && ((ToggleSwitch)settings.FindName("EnableMcpSkills")).IsOn
            && Descendants(settings).OfType<TextBlock>().Any(t => t.Text == "Official extensions"), context + ": official extensions both on by default");
        nested.SelectedItem = settings.FindName("McpClientTab");
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(500, 850)); await Task.Delay(100); settings.UpdateLayout();
        Check(((Grid)settings.FindName("Layout")).ActualWidth <= root.ActualWidth && tabs.IsPaneToggleButtonVisible,
            context + ": Model context retains narrow-screen settings navigation");
        ((ToggleSwitch)settings.FindName("FormElicitation")).IsOn = false;
        ((NumberBox)settings.FindName("ToolTimeout")).Value = 17;
        ((ToggleSwitch)settings.FindName("EnableMcpSkills")).IsOn = false;
        InvokeButton(Descendants(settings).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await shown;
        Check(settings.Result is { ModelContext.EnableFormElicitation: false, ModelContext.ToolTimeoutMinutes: 17, ModelContext.EnableSkills: false }
            && original.ModelContext.EnableFormElicitation, context + ": Save returns isolated complete preferences");
        var canceled = new SettingsDialog(original, true, "en") { XamlRoot = root.XamlRoot };
        shown = canceled.ShowAsync(); await Task.Delay(80);
        ((ToggleSwitch)canceled.FindName("EnableMcpApps")).IsOn = false;
        canceled.Hide(); await shown;
        Check(canceled.Result is null && original.ModelContext.EnableApps, context + ": Cancel discards Model context edits");

        foreach (var action in new[] { "accept", "decline", "cancel" })
        {
            var dialog = new ElicitationDialog("Fixture MCP server", UiElicitRequest()) { XamlRoot = root.XamlRoot, RequestedTheme = theme };
            SystemAppearance.PrepareDialog(dialog);
            shown = dialog.ShowAsync(); await Task.Delay(100); dialog.UpdateLayout();
            var fields = Descendants(dialog).OfType<FrameworkElement>().Where(e => e.Name == "ElicitationField").ToArray();
            Check(fields.Length == 13 && fields.OfType<ComboBox>().Count() == 3 && fields.OfType<CalendarDatePicker>().Count() == 1,
                context + ": native controls render every MCP field variant");
            Check(fields.All(f => !string.IsNullOrEmpty(AutomationProperties.GetName(f))), context + ": every field has an accessible label");
            Check(fields.OfType<CheckBox>().Single().IsChecked == false, context + ": explicit false default retained");
            Check(Descendants(dialog).OfType<ScrollViewer>().First().ActualWidth <= root.ActualWidth,
                context + ": form fits narrow viewport and scrolls vertically");
            if (action == "accept")
            {
                InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await Task.Delay(80);
                Check(!shown.Status.Equals(Windows.Foundation.AsyncStatus.Completed)
                    && Descendants(dialog).OfType<TextBlock>().Any(t => t.Text == "This field is required."), context + ": invalid required input blocks acceptance and shows inline error");
                fields.OfType<TextBox>().Single(f => (string?)f.Tag == "integer").Text = "3";
                fields.OfType<CalendarDatePicker>().Single().Date = new DateTimeOffset(1800, 1, 2, 0, 0, 0, TimeSpan.Zero);
                await Task.Delay(100); dialog.UpdateLayout();
                InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "PrimaryButton"));
            }
            else InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == (action == "decline" ? "SecondaryButton" : "CloseButton")));
            for (var i = 0; i < 100 && shown.Status == Windows.Foundation.AsyncStatus.Started; i++) await Task.Delay(10);
            Check(shown.Status == Windows.Foundation.AsyncStatus.Completed, context + ": dialog closes after " + action);
            await shown;
            Check(dialog.Result.Action == action && (action == "accept" ? dialog.Result.Content!["integer"].GetDouble() == 3 : dialog.Result.Content is null),
                context + ": native form returns " + action + " without leaking declined/canceled values");
            if (action == "accept") Check(dialog.Result.Content!["date"].GetString() == "1800-01-02",
                context + ": MCP date input is not restricted to the native control's default 100-year range");
        }
        // SDK callbacks use background threads. Exercise the real shell presenter and its queue.
        var session = new DesktopSession(new UiHost(true), new UiRuntime(), new());
        var shell = new ChatShell(session) { RequestedTheme = theme };
        window.Content = shell; await Task.Delay(100);
        using var stop = new CancellationTokenSource();
        var first = Task.Run(() => session.ElicitAsync("First server", UiElicitRequest(), stop.Token));
        for (var i = 0; i < 100 && Field<ElicitationDialog?>(shell, "elicitationDialog") is null; i++) await Task.Delay(10);
        Check(Field<ElicitationDialog?>(shell, "elicitationDialog") is not null, context + ": SDK background callback marshals native form to UI thread");
        using var queuedStop = new CancellationTokenSource();
        var queued = Task.Run(() => session.ElicitAsync("Second server", UiElicitRequest(), queuedStop.Token));
        await Task.Delay(50); queuedStop.Cancel();
        try { await queued; throw new Exception("Queued prompt should cancel"); }
        catch (OperationCanceledException) { Check(true, context + ": concurrent queued prompt cancels without opening another native dialog"); }
        stop.Cancel();
        try { await first; throw new Exception("Open prompt should cancel"); }
        catch (OperationCanceledException) { Check(true, context + ": Stop closes active elicitation form"); }
        var shutdownPrompt = Task.Run(() => session.ElicitAsync("Shutdown server", UiElicitRequest(), default));
        for (var i = 0; i < 100 && Field<ElicitationDialog?>(shell, "elicitationDialog") is null; i++) await Task.Delay(10);
        await shell.ShutdownAsync();
        try { await shutdownPrompt; throw new Exception("Shutdown prompt should cancel"); }
        catch (OperationCanceledException) { Check(true, context + ": shutdown cancels active prompt and clears presenter"); }
    }
}
