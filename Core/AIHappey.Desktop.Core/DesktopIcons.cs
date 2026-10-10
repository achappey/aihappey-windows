using FluentIcons.Common;
using FluentIcons.WinUI;

namespace AIHappey.Desktop.Core;

/// <summary>Named Fluent UI artwork. Every call creates a control for a single visual parent.</summary>
internal static class DesktopIcons
{
    // Resizable uses the upstream 20px artwork at the requested display size.
    // Leave Foreground and FlowDirection inherited from the native control template.
    public static FluentIcon Create(Icon icon, double size = 20, IconVariant variant = IconVariant.Regular) => new()
    {
        Icon = icon, FontSize = size, IconSize = IconSize.Resizable, IconVariant = variant
    };

    public static Icon ModelType(string? type) => type switch
    {
        "image" => Icon.Image, "transcription" => Icon.Mic, "audio" => Icon.Headphones,
        "speech" => Icon.Speaker2, "video" => Icon.Video, "decision" => Icon.Branch,
        "reranking" => Icon.ArrowSort, "embedding" => Icon.Folder, _ => Icon.Chat
    };

    public static Icon ProviderLink(string key) => key switch
    {
        "pricing" => Icon.Payment, "console" => Icon.Code, "docs" => Icon.Document,
        "termsOfService" => Icon.Document, "privacyPolicy" => Icon.Shield, _ => Icon.Globe
    };
}
