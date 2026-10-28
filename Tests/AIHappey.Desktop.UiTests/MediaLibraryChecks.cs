using System.Reflection;
using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task CheckMediaLibrariesAsync(ElementTheme theme)
    {
        var context = "Media libraries / " + theme;
        var directory = Path.Combine(Path.GetTempPath(), "aihappey-media-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var root = new Grid { RequestedTheme = theme }; window!.Content = root;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900)); window.Activate(); await Task.Delay(100);
        var path = Path.Combine(directory, "sample.wav");
        // A short, local PCM wave; tests never use a provider, network, microphone or file picker.
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write("RIFF"u8); writer.Write(36 + 1600); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(1600); writer.Write(new byte[1600]);
        }
        try
        {
            JsonObject Response(string model) => new()
            {
                ["text"] = "Transcribed text", ["language"] = "en",
                ["request"] = new JsonObject { ["body"] = "{\"voice\":\"test-voice\",\"nested\":{\"enabled\":true}}" },
                ["response"] = new JsonObject { ["modelId"] = model, ["body"] = "{\"transcript\":\"provider-result-marker\",\"items\":[1,2,3]}" },
                ["providerMetadata"] = new JsonObject { ["echo"] = new JsonObject { ["language"] = "en" }, ["gateway"] = new JsonObject { ["cost"] = .001 } },
                ["warnings"] = new JsonArray(new JsonObject { ["message"] = "warning-marker" }),
                ["segments"] = new JsonArray(new JsonObject { ["startSecond"] = 0, ["endSecond"] = 1, ["text"] = "Segment text" })
            };
            var speech = new LibrarySpeech(new StoredSpeech { Id = "speech-fixture", Model = "echo/voice/nested", Text = "Speech input text",
                CreatedAt = DateTimeOffset.UtcNow, Format = "wav", MediaType = "audio/wav", File = "sample.wav",
                Input = new JsonObject { ["model"] = "echo/voice/nested", ["text"] = "legacy-input-marker" }, Response = Response("echo/voice/nested") }, path);
            var transcription = new LibraryTranscription(new StoredTranscription { Id = "transcription-fixture", Model = "echo/transcribe/nested",
                Filename = "sample.wav", MediaType = "audio/wav", CreatedAt = DateTimeOffset.UtcNow, Response = Response("echo/transcribe/nested") }, path);
            var speechSnapshot = speech.Item.Response.ToJsonString(); var transcriptionSnapshot = transcription.Item.Response.ToJsonString();
            var speechPage = new SpeechPage { RequestedTheme = theme }; root.Children.Add(speechPage);
            speechPage.SetModelAvailable(true); speechPage.SetItems([speech]); await Task.Delay(300); speechPage.UpdateLayout();
            CheckMediaToolbar(speechPage, "Import", context);
            var speechCard = Descendants(speechPage).OfType<Border>().Single(c => c.Name == "SpeechCard");
            Check(Descendants(speechCard).OfType<TextBlock>().Any(t => t.Name == "SpeechModel" && t.Text == "voice/nested"
                && AutomationProperties.GetName(t) == "echo/voice/nested"), context + ": loaded provider logo removes only provider prefix and retains accessible full ID");
            Check(Descendants(speechCard).OfType<Grid>().Any(g => g.GetType().Name == "ProviderLogo" && g.Children.OfType<Image>().Single().Visibility == Visibility.Visible), context + ": embedded native provider artwork displayed");
            var footer = Descendants(speechCard).OfType<Border>().Single(c => c.Name == "SpeechCardFooter");
            Check(footer.BorderThickness.Top == 1 && Grid.GetRow(footer) == 2
                && Descendants(footer).OfType<Button>().Select(b => b.Name).SequenceEqual(["SpeechDetails", "SpeechDownload"])
                && !Descendants(speechCard).OfType<Button>().Any(b => b.Name is "SpeechPlay" or "SpeechDelete"), context + ": Info/Download in divided footer, no duplicate Play or inline Delete");
            var detailsCalls = 0; var downloads = 0; var deletes = 0;
            // Refresh after setting callbacks, as cards capture their action callbacks when built.
            speechPage.DetailsRequested = _ => { detailsCalls++; return Task.CompletedTask; };
            speechPage.DownloadRequested = _ => { downloads++; return Task.CompletedTask; };
            speechPage.DeleteRequested = _ => { deletes++; return Task.CompletedTask; };
            speechPage.SetItems([speech]); await Task.Delay(120);
            InvokeButton(Descendants(speechPage).OfType<Button>().Single(b => b.Name == "SpeechDetails"));
            InvokeButton(Descendants(speechPage).OfType<Button>().Single(b => b.Name == "SpeechDownload")); await Task.Delay(40);
            var more = Descendants(speechPage).OfType<Button>().Single(b => b.Name == "SpeechActions");
            var deleteMenu = (MenuFlyout)more.Flyout;
            Check(deleteMenu.Items.Count == 1 && ((MenuFlyoutItem)deleteMenu.Items[0]).Text == "Delete", context + ": Delete is only in header overflow menu");
            deleteMenu.ShowAt(more); await Task.Delay(60); Invoke((MenuFlyoutItem)deleteMenu.Items[0]); deleteMenu.Hide(); await Task.Delay(40);
            Check(detailsCalls == 1 && downloads == 1 && deletes == 1, context + ": speech footer and context menu invoke their intended actions");
            speechPage.SetBusy(true, true);
            InvokeButton(Descendants(speechPage).OfType<Button>().Single(b => b.Name == "SpeechDetails")); await Task.Delay(30);
            Check(detailsCalls == 1, context + ": busy speech card cannot open details"); speechPage.SetBusy(false, false);
            await CheckMediaPageWidthsAsync(speechPage, context);
            root.Children.Clear();

            var transcriptionsPage = new TranscriptionsPage { RequestedTheme = theme }; root.Children.Add(transcriptionsPage);
            transcriptionsPage.SetModelAvailable(true); transcriptionsPage.SetItems([transcription]); await Task.Delay(250); transcriptionsPage.UpdateLayout();
            CheckMediaToolbar(transcriptionsPage, "AddFiles", context);
            Check(Descendants(transcriptionsPage).OfType<TextBlock>().Any(t => t.Text == "transcribe/nested")
                && Descendants(transcriptionsPage).OfType<Button>().Single(b => b.Name == "TranscriptionActions").Flyout is MenuFlyout,
                context + ": transcription preserves card actions and shows branded short model ID");
            await CheckMediaPageWidthsAsync(transcriptionsPage, context);
            root.Children.Clear(); root.Children.Add(speechPage);

            // Offline remote artwork and unknown provider IDs retain their identifying prefix.
            foreach (var model in new[] { "openai/voice", "unknown-provider/voice", "other-prefix/voice" })
            {
                var response = new JsonObject { ["response"] = new JsonObject { ["modelId"] = model } };
                if (model.StartsWith("other-prefix")) response["providerMetadata"] = new JsonObject { ["echo"] = new JsonObject() };
                speechPage.SetItems([new LibrarySpeech(new StoredSpeech { Model = model, Text = "Fallback", Response = response }, path)]);
                await Task.Delay(180);
                Check(Descendants(speechPage).OfType<TextBlock>().Single(t => t.Name == "SpeechModel").Text == model,
                    context + ": unavailable/unknown/mismatched logo does not strip " + model);
            }
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900)); await Task.Delay(80);
            await CheckMediaDialogAsync(new SpeechDetailsDialog(speech), root, theme, "Speech", context);
            await CheckMediaDialogAsync(new TranscriptionDetailsDialog(transcription), root, theme, "Transcription", context);
            Check(speech.Item.Response.ToJsonString() == speechSnapshot && transcription.Item.Response.ToJsonString() == transcriptionSnapshot,
                context + ": inspection parses cloned provider bodies without mutating stored data");

            // Missing request data uses saved input. Malformed JSON remains selectable text, not a blank view or exception.
            speech.Item.Response.Remove("request");
            var legacy = new SpeechDetailsDialog(speech) { XamlRoot = root.XamlRoot, RequestedTheme = theme }; SystemAppearance.PrepareDialog(legacy);
            var legacyShown = legacy.ShowAsync(); await Task.Delay(130);
            var legacyTabs = Descendants(legacy).OfType<NavigationView>().Single(t => t.Name == "SpeechDetailsTabs");
            legacyTabs.SelectedItem = legacyTabs.MenuItems.OfType<NavigationViewItem>().Single(t => t.Name == "SpeechProviderInputTab"); await Task.Delay(80);
            Check(Descendants(legacy).OfType<TextBlock>().Any(t => t.Text.Contains("legacy-input-marker")), context + ": saved input is visible for legacy speech");
            legacy.Hide(); await legacyShown;
            var dataType = typeof(SpeechDetailsDialog).Assembly.GetType("AIHappey.Desktop.Core.MediaDetailsView")!;
            var parse = dataType.GetMethod("ParseBody", BindingFlags.Static | BindingFlags.NonPublic)!;
            var malformed = JsonValue.Create("not valid { JSON")!;
            Check(JsonNode.DeepEquals((JsonNode)parse.Invoke(null, [malformed])!, malformed), context + ": malformed provider body preserved as text");
            var parsedObject = JsonNode.Parse("{\"nested\":{\"value\":true}}")!;
            Check(JsonNode.DeepEquals((JsonNode)parse.Invoke(null, [parsedObject])!, parsedObject), context + ": object provider body also preserved");
        }
        finally { root.Children.Clear(); window.Content = null; Directory.Delete(directory, true); }
    }

    private void CheckMediaToolbar(UserControl page, string attachmentName, string context)
    {
        var attach = (Button)page.FindName(attachmentName); var settings = (Button)page.FindName("Settings");
        var parent = (Panel)VisualTreeHelper.GetParent(attach);
        Check(parent.Children.IndexOf(attach) < parent.Children.IndexOf(settings) && attach.TabIndex == settings.TabIndex
            && attach.TransformToVisual(page).TransformPoint(new()).X < settings.TransformToVisual(page).TransformPoint(new()).X,
            context + ": " + page.Name + " attachments precede settings visually and in keyboard order");
    }

    private async Task CheckMediaPageWidthsAsync(UserControl page, string context)
    {
        foreach (var width in new[] { 480, 720, 1280 })
        {
            window!.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 900)); await Task.Delay(90); page.UpdateLayout();
            var grid = (GridView)page.FindName("LibraryGrid");
            Check(Descendants(page).OfType<Border>().Where(c => c.Name is "SpeechCard" or "TranscriptionCard").All(c => c.ActualWidth <= page.ActualWidth)
                && Descendants(grid).OfType<ScrollViewer>().First().ScrollableWidth < 1, context + $": {page.Name} cards fit at {width}px");
        }
    }

    private async Task CheckMediaDialogAsync(ContentDialog dialog, FrameworkElement root, ElementTheme theme, string kind, string context)
    {
        dialog.XamlRoot = root.XamlRoot; dialog.RequestedTheme = theme; SystemAppearance.PrepareDialog(dialog);
        var shown = dialog.ShowAsync();
        try
        {
            await Task.Delay(180); dialog.UpdateLayout();
            var tabs = Descendants(dialog).OfType<NavigationView>().Single(t => t.Name == kind + "DetailsTabs"); CheckNativeTabs(tabs, context + ": " + kind);
            Check(!Descendants(dialog).OfType<TabView>().Any(), context + ": " + kind + " has app-standard native navigation, not document tabs");
            if (kind == "Speech")
            {
                var player = Descendants(dialog).OfType<MediaPlayerElement>().Single();
                Check(player.TransformToVisual(dialog).TransformPoint(new()).Y < tabs.TransformToVisual(dialog).TransformPoint(new()).Y
                    && player.ActualHeight >= 48 && player.ActualHeight <= 96 && player.MediaPlayer is not null, context + ": adaptive player stays above tabs and opens local audio");
            }
            foreach (var suffix in new[] { "ProviderInput", "ProviderResult" })
            {
                var tab = tabs.MenuItems.OfType<NavigationViewItem>().Single(t => t.Name == kind + suffix + "Tab"); tabs.SelectedItem = tab; await Task.Delay(80);
                // Expand nested provider sections, including arrays and metadata, through native controls.
                for (var depth = 0; depth < 5; depth++)
                {
                    foreach (var expander in Descendants(dialog).OfType<Expander>().ToArray()) expander.IsExpanded = true;
                    await Task.Delay(30);
                }
                dialog.UpdateLayout();
                var pane = Descendants(dialog).OfType<StackPanel>().Single(p => p.Name == kind + suffix);
                var text = string.Join(" ", Descendants(pane).OfType<TextBlock>().Select(t => t.Text));
                Check(pane.ActualHeight > 40 && pane.ActualWidth > 100 && Descendants(pane).OfType<Expander>().Any()
                    && !Descendants(pane).OfType<TextBox>().Any()
                    && text.Contains(suffix == "ProviderInput" ? "test-voice" : "provider-result-marker")
                    && (suffix != "ProviderResult" || text.Contains("warning-marker")), context + ": " + kind + " " + suffix + " is populated, selectable native JSON");
            }
            foreach (var size in new[] { (480, 640), (720, 720), (1280, 900) })
            {
                window!.AppWindow.Resize(new Windows.Graphics.SizeInt32(size.Item1, size.Item2)); await Task.Delay(100); dialog.UpdateLayout();
                var scroll = Descendants(dialog).OfType<ScrollViewer>().Single(s => s.Name == kind + "DetailsPage");
                Check(scroll.ActualHeight > 40 && scroll.ActualWidth > 100 && scroll.ActualWidth <= root.ActualWidth
                    && scroll.ScrollableWidth < 1, context + $": {kind} provider pane remains usable at {size} (viewport={scroll.ActualWidth}x{scroll.ActualHeight}, root={root.ActualWidth}x{root.ActualHeight}, overflow={scroll.ScrollableWidth}, layout={((FrameworkElement)dialog.Content).ActualWidth}x{((FrameworkElement)dialog.Content).ActualHeight})");
                if (size.Item1 == 480) Check(scroll.ScrollableHeight > 0, context + ": long provider result scrolls within narrow " + kind + " dialog");
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
            }
            if (kind == "Transcription")
            {
                tabs.SelectedItem = tabs.MenuItems.OfType<NavigationViewItem>().Single(t => t.Name == "TranscriptionSegmentsTab"); await Task.Delay(60);
                Check(Descendants(dialog).OfType<TextBlock>().Any(t => t.Text == "Segment text"), context + ": transcription segments retained");
                InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "TranscriptionExportText")); await shown;
                Check(((TranscriptionDetailsDialog)dialog).Action == TranscriptionDetailAction.ExportText, context + ": text export action retained");
            }
            else
            {
                InvokeButton(Descendants(dialog).OfType<Button>().Single(b => b.Name == "PrimaryButton")); await shown;
                Check(((SpeechDetailsDialog)dialog).Action == SpeechDetailAction.Download && !Descendants(dialog).OfType<MediaPlayerElement>().Any(e => e.MediaPlayer is not null),
                    context + ": modal download retained and closed player released");
            }
        }
        finally { dialog.Hide(); await shown; window!.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900)); await Task.Delay(60); }
    }
}
