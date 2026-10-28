using System.Text.Json.Nodes;
using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Brand saved media without losing model identity when artwork cannot be loaded.</summary>
internal static class MediaCardIdentity
{
    internal static CatalogProvider? Provider(JsonObject response, params string[] models)
    {
        if (response["providerMetadata"] is JsonObject metadata)
            foreach (var key in metadata.Select(p => p.Key.Trim().ToLowerInvariant()).Where(k => k != "gateway"))
                if (ProviderCatalog.All.TryGetValue(key, out var provider)) return provider;
        foreach (var model in models)
        {
            var slash = model.IndexOf('/');
            if (slash > 0 && ProviderCatalog.All.TryGetValue(model[..slash].ToLowerInvariant(), out var provider)) return provider;
        }
        return null;
    }

    internal static string DisplayModel(string model, CatalogProvider provider, bool logoShown) =>
        logoShown && model.StartsWith(provider.Id + "/", StringComparison.OrdinalIgnoreCase)
            && model.Length > provider.Id.Length + 1 ? model[(provider.Id.Length + 1)..] : model;

    internal static UIElement Artwork(JsonObject response, string model, string storedModel, TextBlock label, Icon fallback)
    {
        label.Text = model;
        ToolTipService.SetToolTip(label, model); AutomationProperties.SetName(label, model);
        if (Provider(response, model, storedModel) is not { } provider)
            return new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top,
                Children = { DesktopIcons.Create(fallback, 24) } };
        var logo = new ProviderLogo(provider, 32, showPlaceholder: true, placeholderIcon: fallback) { VerticalAlignment = VerticalAlignment.Top };
        ToolTipService.SetToolTip(logo, provider.Name);
        logo.DisplayChanged += () => label.Text = DisplayModel(model, provider, logo.DisplayedSource is not null);
        return logo;
    }
}
