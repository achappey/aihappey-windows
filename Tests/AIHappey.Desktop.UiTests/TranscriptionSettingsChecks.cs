using System.Text.Json.Nodes;
using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private static Dictionary<string, NumberBox> TranscriptionNumbers(TranscriptionSettingsDialog dialog) =>
        Field<List<ITranscriptionProviderForm>>(dialog, "forms").SelectMany(form => ProviderControls(form.View))
            .OfType<NumberBox>().ToDictionary(field => field.Name);

    private async Task CheckTranscriptionSettingsAsync(Grid root, ElementTheme theme)
    {
        var context = "Transcription settings / " + theme;
        string[] providers = ["openai", "google", "gladia", "smallestai", "mistral"];
        var original = new TranscriptionPreferences { SkipDictateWarning = true, Realtime = new() { ProviderOptions = new()
        {
            ["openai"] = new JsonObject
            {
                ["session"] = new JsonObject { ["audio"] = new JsonObject { ["input"] = new JsonObject
                    { ["turn_detection"] = new JsonObject { ["threshold"] = .65m, ["silence_duration_ms"] = 750, ["prefix_padding_ms"] = 250L } } } },
                ["expires_after"] = new JsonObject { ["seconds"] = 900 }
            },
            ["google"] = new JsonObject { ["expireTime"] = "45m" },
            ["gladia"] = new JsonObject { ["endpointing"] = .15m, ["maximum_duration_without_endpointing"] = 8 },
            ["smallestai"] = new JsonObject { ["language"] = "en", ["ttl_seconds"] = 600, ["diarize"] = true },
            ["mistral"] = new JsonObject { ["language"] = "nl", ["target_streaming_delay_ms"] = 960 },
            ["future-provider"] = new JsonObject { ["keep"] = true }
        } } };
        foreach (var provider in providers) original.Realtime.ProviderOptions[provider]!["future"] = new JsonObject { ["keep"] = true };
        original.ProviderOptions["openai"]["future"] = new JsonObject { ["keep"] = true };
        var originalJson = original.Realtime.ProviderOptions.ToJsonString();
        (string Name, double Initial, double Changed, double Default)[] expected =
        [
            ("Realtimeopenaithreshold", .65, .7, .5), ("Realtimeopenaisilence_duration_ms", 750, 850, 500),
            ("Realtimeopenaiprefix_padding_ms", 250, 350, 300), ("Realtimeopenaiseconds", 900, 1200, 600),
            ("Realtimegladiaendpointing", .15, .25, .05), ("Realtimegladiamaximum_duration_without_endpointing", 8, 9, 5),
            ("Realtimesmallestaittl_seconds", 600, 630, 300), ("Realtimemistraltarget_streaming_delay_ms", 960, 1000, 480)
        ];

        // Cover parsed numbers too, but never serialize the native fixture before opening or reopening it.
        var parsed = new RealtimeTranscriptionPreferences { ProviderOptions = JsonNode.Parse(originalJson)!.AsObject() };
        var parsedNumbers = providers.SelectMany(provider => ProviderControls(new RealtimeTranscriptionSettingsForm(provider, parsed).View))
            .OfType<NumberBox>().ToDictionary(field => field.Name);
        Check(expected.All(item => parsedNumbers[item.Name].Value == item.Initial), context + ": parsed JSON numbers retain their values");

        TranscriptionPreferences? saved = null;
        var dialog = new TranscriptionSettingsDialog(original, providers) { XamlRoot = root.XamlRoot, RequestedTheme = theme,
            SaveAsync = next => { saved = next; return Task.CompletedTask; } };
        SystemAppearance.PrepareDialog(dialog); var shown = dialog.ShowAsync();
        try
        {
            await Task.Delay(120);
            var forms = Field<List<ITranscriptionProviderForm>>(dialog, "forms");
            Check(forms.Count == 6 && forms.OfType<RealtimeTranscriptionSettingsForm>().Count() == 5,
                context + ": modal constructs batch settings and all five realtime providers");
            await CheckMediaSettingsTabsAsync(dialog, ["TranscriptionProviderTab_openai", "TranscriptionRealtimeTab_openai",
                "TranscriptionRealtimeTab_google", "TranscriptionRealtimeTab_gladia", "TranscriptionRealtimeTab_smallestai", "TranscriptionRealtimeTab_mistral"], context);
            var navigation = Field<NavigationView>(dialog, "tabs");
            var tabs = navigation.MenuItems.OfType<NavigationViewItem>().ToArray();
            for (var i = 0; i < forms.Count; i++)
            {
                navigation.SelectedItem = tabs[i]; await Task.Delay(40);
                Check(ReferenceEquals(Field<ScrollViewer>(dialog, "page").Content, forms[i].View), context + ": provider tab opens " + forms[i].Title);
            }
            var numbers = TranscriptionNumbers(dialog);
            Check(numbers.Count == expected.Length && expected.All(item => numbers[item.Name].Value == item.Initial),
                context + ": native integer, long and decimal values initialize without conversion exceptions or resets");
            foreach (var item in expected) numbers[item.Name].Value = item.Changed;
            Check(original.Realtime.ProviderOptions.ToJsonString() == originalJson, context + ": editing only changes the draft");
            InvokeButton(Descendants(dialog).OfType<Button>().Single(button => button.Name == "CloseButton")); await shown;
            Check(saved is not null, context + ": closing commits every provider and validates Mistral");
        }
        finally { dialog.DiscardOnShutdown = true; dialog.Hide(); await shown; }

        var options = saved!.Realtime.ProviderOptions;
        Check(options["openai"]!["session"]!["audio"]!["input"]!["turn_detection"]!["silence_duration_ms"]!.AsValue().TryGetValue<int>(out var silence) && silence == 850
            && options["mistral"]!["target_streaming_delay_ms"]!.AsValue().TryGetValue<int>(out var delay) && delay == 1000
            && options["smallestai"]!["ttl_seconds"]!.AsValue().TryGetValue<int>(out var ttl) && ttl == 630,
            context + ": integral settings remain integer-backed after saving");
        Check(providers.All(provider => options[provider]!["future"]!["keep"]!.GetValue<bool>())
            && options["future-provider"]!["keep"]!.GetValue<bool>() && saved.ProviderOptions["openai"]["future"]!["keep"]!.GetValue<bool>()
            && original.Realtime.ProviderOptions.ToJsonString() == originalJson, context + ": saving preserves unknown options, batch settings and the source preferences");
        saved.Realtime.Validate();

        TranscriptionPreferences? reset = null;
        var reopen = new TranscriptionSettingsDialog(saved, providers) { XamlRoot = root.XamlRoot, RequestedTheme = theme,
            SaveAsync = next => { reset = next; return Task.CompletedTask; } };
        SystemAppearance.PrepareDialog(reopen); shown = reopen.ShowAsync();
        try
        {
            await Task.Delay(120);
            var numbers = TranscriptionNumbers(reopen);
            Check(expected.All(item => numbers[item.Name].Value == item.Changed), context + ": saved in-memory settings reopen without a JSON round-trip");
            InvokeButton(Descendants(reopen).OfType<Button>().Single(button => button.Name == "PrimaryButton")); await Task.Delay(80);
            numbers = TranscriptionNumbers(reopen);
            var navigation = Field<NavigationView>(reopen, "tabs");
            Check(expected.All(item => numbers[item.Name].Value == item.Default)
                && navigation.SelectedItem == navigation.MenuItems[0]
                && ReferenceEquals(Field<ScrollViewer>(reopen, "page").Content, Field<List<ITranscriptionProviderForm>>(reopen, "forms")[0].View),
                context + ": restore defaults rebuilds controls and selects the first native tab safely");
            InvokeButton(Descendants(reopen).OfType<Button>().Single(button => button.Name == "CloseButton")); await shown;
            Check(reset is not null && reset.SkipDictateWarning
                && reset.Realtime.ProviderOptions["mistral"]!["target_streaming_delay_ms"]!.GetValue<int>() == 480,
                context + ": restored defaults save successfully and retain the dictate warning preference");
        }
        finally { reopen.DiscardOnShutdown = true; reopen.Hide(); await shown; }

        foreach (var unsupported in new[] { Array.Empty<string>(), new[] { "other" } })
        {
            var empty = new TranscriptionSettingsDialog(original, unsupported) { XamlRoot = root.XamlRoot, RequestedTheme = theme };
            shown = empty.ShowAsync();
            try
            {
                await Task.Delay(80);
                var navigation = Field<NavigationView>(empty, "tabs");
                Check(navigation.MenuItems.Count == 0 && navigation.SelectedItem is null
                    && Field<ScrollViewer>(empty, "page").Content is TextBlock message
                    && message.Text == DesktopResources.Get("TranscriptionNoProviderSettings"), context + ": no supported providers retains the empty-settings message");
            }
            finally { empty.DiscardOnShutdown = true; empty.Hide(); await shown; }
        }

        foreach (var (name, value) in new (string, JsonNode?)[]
        {
            ("null", null), ("numeric string", JsonValue.Create("750")), ("boolean", JsonValue.Create(true)),
            ("array", new JsonArray(750)), ("object", new JsonObject()), ("NaN", JsonValue.Create(double.NaN)),
            ("positive infinity", JsonValue.Create(double.PositiveInfinity)), ("negative infinity", JsonValue.Create(double.NegativeInfinity))
        })
        {
            var preferences = new RealtimeTranscriptionPreferences { ProviderOptions = new() { ["mistral"] = new JsonObject { ["target_streaming_delay_ms"] = value } } };
            var form = new RealtimeTranscriptionSettingsForm("mistral", preferences);
            Check(ProviderControls(form.View).OfType<NumberBox>().Single().Value == 480,
                context + ": " + name + " uses a safe display default rather than crashing");
        }
    }
}
