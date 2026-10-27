using System.Text.Json.Nodes;
using static AIHappey.Desktop.Core.ChatSettingsFields;

namespace AIHappey.Desktop.Core;

internal sealed partial class GoogleChatSettingsForm
{
    private void ResponseFormats()
    {
        Action render = () => { };
        var body = Section("response_format", G("interactions.responseFormat.title"), On("response_format"), value =>
        {
            model.Set("response_format", value ? new JsonArray(new JsonObject { ["type"] = "text" }) : null, !value); render();
        });
        foreach (var type in GoogleChatConfig.FormatTypes)
        {
            var add = Button(body, G("interactions.responseFormat.types." + type), () => fields.Changed(() =>
            {
                var values = model.Formats;
                values.Add(type == "text" ? new JsonObject { ["type"] = type } : new JsonObject { ["type"] = type, ["delivery"] = "inline" });
                model.FormatsChanged(values); render();
            }));
            add.Name = "Google_response_format_Add_" + type;
            fields.Watch(() => add.IsEnabled = model.Get("response_format") is not null && !model.Formats.Any(n => n is JsonObject obj && ProviderChatConfig.Text(obj["type"]) == type));
        }
        render = Rows("response_format", body, () => model.Formats, model.FormatsChanged, On("response_format"),
            (row, local, draft, save, _) =>
            {
                var type = draft.String("type");
                row.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock { Text = L(G("interactions.responseFormat.types." + type)), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap });
                var (left, right) = Pair(row);
                var prefix = "interactions.responseFormat.";
                switch (type)
                {
                    case "audio":
                        RowSelect(left, local, draft, G(prefix + "mimeType"), "mime_type", GoogleChatConfig.AudioMimeTypes, save, On("response_format"), true);
                        RowNumber(right, local, draft, G(prefix + "sampleRate"), "sample_rate", save, On("response_format"));
                        RowNumber(right, local, draft, G(prefix + "bitRate"), "bit_rate", save, On("response_format")); break;
                    case "image":
                        RowSelect(left, local, draft, G(prefix + "aspectRatio"), "aspect_ratio", GoogleChatConfig.ImageAspectRatios, save, On("response_format"), true);
                        RowSelect(right, local, draft, G(prefix + "imageSize"), "image_size", GoogleChatConfig.ImageSizes, save, On("response_format"), true); break;
                    case "video":
                        RowSelect(left, local, draft, G(prefix + "aspectRatio"), "aspect_ratio", ["16:9", "9:16"], save, On("response_format"), true);
                        RowSelect(right, local, draft, G(prefix + "resolution"), "resolution", ["360p", "720p", "1080p", "4k"], save, On("response_format"), true);
                        RowText(row, local, draft, G(prefix + "duration"), "duration", save, On("response_format")); break;
                }
            }, min: 1);
    }
    private void Speech()
    {
        const string path = "generation_config/speech_config";
        Action render = () => { };
        var body = Section("speech_config", G("interactions.speech.title"), () => model.Get(path) is JsonArray, value =>
        {
            model.Generation("speech_config", value ? new JsonArray(new JsonObject()) : null, !value); render();
        });
        render = Rows("speech_config", body, () => (JsonArray?)model.Get(path)?.DeepClone() ?? new(),
            values => model.Generation("speech_config", values), On(path), (row, local, draft, save, _) =>
            {
                var (left, right) = Pair(row);
                RowText(left, local, draft, G("interactions.speech.language"), "language", save, On(path));
                RowText(right, local, draft, G("interactions.speech.speaker"), "speaker", save, On(path));
                RowText(row, local, draft, G("interactions.speech.voice"), "voice", save, On(path));
            }, () => new(), G("interactions.speech.addSpeaker"), min: 1, max: 2);
    }
    private void Transcription()
    {
        const string path = "generation_config/transcription_config";
        var body = Section("transcription_config", G("interactions.transcription.title"), On(path), value =>
            model.Generation("transcription_config", value ? new JsonObject() : null, !value));
        StringListEditor(body, "transcription_languages", G("interactions.transcription.languageCodes"), path + "/language_codes", On(path));
        StringListEditor(body, "transcription_vocabulary", G("interactions.transcription.customVocabulary"), path + "/custom_vocabulary", On(path));
        fields.Switch(body, G("interactions.transcription.speakerDiarization"), () => model.String(path + "/diarization_mode") == "speaker",
            value => model.Generation("transcription_config/diarization_mode", value ? JsonValue.Create("speaker") : null, !value), On(path));
        fields.Switch(body, G("interactions.transcription.wordTimestamps"), () => (model.Get(path + "/timestamp_granularities") as JsonArray)?.Any(n => ProviderChatConfig.Text(n) == "word") == true,
            value => model.Generation("transcription_config/timestamp_granularities", value ? ProviderChatConfig.Strings(["word"]) : null, !value), On(path));
    }
    private void Safety()
    {
        Action render = () => { };
        var body = Section("safety_settings", G("safetySettings.title"), () => model.Get("safety_settings") is JsonArray, value =>
        {
            model.Set("safety_settings", value ? new JsonArray(GoogleChatConfig.SafetyDefault()) : null, !value); render();
        });
        render = Rows("safety_settings", body, () => (JsonArray?)model.Get("safety_settings")?.DeepClone() ?? new(),
            values => model.Set("safety_settings", values), On("safety_settings"), (row, local, draft, save, _) =>
            {
                RowSelect(row, local, draft, G("safetySettings.type"), "type", GoogleChatConfig.SafetyTypes, save, On("safety_settings"), translate: value => L(G("safetySettings.categories." + value)));
                var (left, right) = Pair(row);
                RowSelect(left, local, draft, G("safetySettings.method"), "method", GoogleChatConfig.SafetyMethods, save, On("safety_settings"), translate: value => L(G("safetySettings.methods." + value)));
                RowSelect(right, local, draft, G("safetySettings.threshold"), "threshold", GoogleChatConfig.SafetyThresholds, save, On("safety_settings"), translate: value => L(G("safetySettings.thresholds." + value)));
            }, GoogleChatConfig.SafetyDefault, G("safetySettings.add"));
    }
}
