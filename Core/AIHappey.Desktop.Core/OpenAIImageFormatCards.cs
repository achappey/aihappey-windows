using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Shared Images/Responses controls. Values are passed through; the API validates format combinations.</summary>
internal static class OpenAIImageFormatCards
{
    public static void Output(Panel parent, ChatSettingsFields fields, OpenAIChatConfig model, string prefix, Func<bool>? enabled = null)
    {
        var (left, right) = ChatSettingsFields.Pair(parent);
        fields.Select(left, "openai.imageOutputFormat", () => model.String(prefix + "output_format"),
            ["png", "jpeg", "webp", "gif", "svg"], value => model.Optional(prefix + "output_format", value), enabled,
            inherit: true, inheritLabel: ChatSettingsFields.L("default"));
        fields.Select(right, "openai.imageComplexity", () => model.String(prefix + "complexity"),
            ["low", "medium", "high"], value => model.Optional(prefix + "complexity", value), enabled, inherit: true);
    }

    public static void Add(Panel parent, ChatSettingsFields fields, OpenAIChatConfig model, string prefix, Func<bool>? enabled = null)
    {
        NativeSettingsSurface.Expander(parent, "OpenAI_ImageGIF", "GIF", null, out var gif);
        Frames(gif, fields, model, prefix, enabled);
        Svg(parent, fields, model, prefix, enabled);
    }

    public static void Frames(Panel parent, ChatSettingsFields fields, OpenAIChatConfig model, string prefix, Func<bool>? enabled = null)
    {
        var (left, right) = ChatSettingsFields.Pair(parent);
        foreach (var (key, label, placeholder) in new[] { ("frame_count", "openai.imageFrameCount", "9"), ("frame_duration_ms", "openai.imageFrameDuration", "200") })
        {
            var input = fields.Number(key == "frame_count" ? left : right, label, () => model.Get(prefix + key)?.ToJsonString() ?? "",
                value => model.Set(prefix + key, value is null ? null : JsonValue.Create(value.Value), value is null), enabled, whole: true);
            input.PlaceholderText = placeholder;
        }
    }

    public static void Svg(Panel parent, ChatSettingsFields fields, OpenAIChatConfig model, string prefix, Func<bool>? enabled = null)
    {
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        var toggle = fields.HeaderSwitch(heading, "openai.imageSvg", () => model.Get(prefix + "svg") is JsonObject,
            on => model.Set(prefix + "svg", on ? new JsonObject() : null, !on));
        if (enabled is not null) fields.Watch(() => toggle.IsEnabled = enabled());
        NativeSettingsSurface.Expander(parent, "OpenAI_ImageSVG", "SVG", heading, out var svg);
        bool SvgOn() => (enabled?.Invoke() ?? true) && model.Get(prefix + "svg") is JsonObject;
        fields.Text(svg, "openai.imageSvgModel", () => model.String(prefix + "svg/model"),
            value => model.Optional(prefix + "svg/model", value), SvgOn);
        fields.Select(svg, "reasoningEffort", () => model.String(prefix + "svg/reasoning/effort"), OpenAIChatConfig.Efforts,
            value =>
            {
                model.Optional(prefix + "svg/reasoning/effort", value);
                if (model.Get(prefix + "svg/reasoning") is JsonObject { Count: 0 }) model.Set(prefix + "svg/reasoning", null, true);
            }, SvgOn, inherit: true);
    }
}
