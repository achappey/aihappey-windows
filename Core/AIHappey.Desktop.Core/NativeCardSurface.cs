using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.CompilerServices;

namespace AIHappey.Desktop.Core;

/// <summary>Scoped Fluent card styling. All resource references live in compiled XAML,
/// so WinUI re-resolves them for runtime theme/contrast changes. Never touch native templates.</summary>
internal static class NativeCardSurface
{
    private static readonly ConditionalWeakTable<FrameworkElement, ResourceDictionary> scopes = new();

    private static Style Style(FrameworkElement element, string key)
    {
        var resources = scopes.GetValue(element, owner =>
        {
            var dictionary = new ResourceDictionary
            {
                Source = new Uri("ms-appx:///AIHappey.Desktop.Core/NativeCardStyles.xaml")
            };
            owner.Resources.MergedDictionaries.Add(dictionary);
            return dictionary;
        });
        // Only app-owned keys in our compiled dictionary, never optional Application resources.
        return (Style)resources[key];
    }

    public static void Card(Border card, bool inset = false) => card.Style = Style(card, inset ? "NativeCardInsetStyle" : "NativeCardStyle");
    public static void Divider(Border divider) => divider.Style = Style(divider, "NativeCardDividerStyle");
    public static void Badge(Border badge) => badge.Style = Style(badge, "NativeCardBadgeStyle");
    public static void Secondary(TextBlock text, bool metadata = false) => text.Style = Style(text, metadata ? "NativeCardMetadataStyle" : "NativeCardSecondaryTextStyle");
    public static void Action(Button button)
    {
        button.Style = Style(button, "NativeCardActionStyle");
        ControlAppearance.Stock(button);
    }
}
