using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

public sealed partial class ChatShell
{
    private readonly Button viewSystemContext = new() { Name = "ViewSystemContext", Content = new FontIcon { Glyph = "\uE890" },
        Width = 40, Height = 40, Padding = new Thickness(0), CornerRadius = new CornerRadius(6) };
    private SystemContextDialog? systemContextDialog;

    private void PrepareSystemContext()
    {
        ToolbarControls.Subtle(viewSystemContext); ToolbarControls.Label(viewSystemContext, DesktopResources.Get("Context"));
        viewSystemContext.Click += async (_, _) => await ShowSystemContextAsync();
    }

    private UIMessage CaptureSystemContext(ChatPreferences? preferences = null, McpTurnSnapshot? mcp = null)
    {
        var information = session.SystemInformationProvider(DateTimeOffset.UtcNow);
        var scale = XamlRoot.RasterizationScale;
        var display = DisplayArea.GetFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId, DisplayAreaFallback.Primary);
        if (display is not null)
        {
            information["screen"] = new JsonObject
            {
                ["width"] = display.OuterBounds.Width / scale, ["height"] = display.OuterBounds.Height / scale,
                ["availWidth"] = display.WorkArea.Width / scale, ["availHeight"] = display.WorkArea.Height / scale
            };
        }
        information["window"] = new JsonObject
        {
            ["innerWidth"] = XamlRoot.Size.Width, ["innerHeight"] = XamlRoot.Size.Height, ["devicePixelRatio"] = scale
        };
        return session.CaptureSystemContext(ActualTheme == ElementTheme.Dark, information, preferences,
            mcp ?? activeMcpTurn ?? CaptureSkillRuntime(preferences ?? session.Settings.Chat));
    }

    private async Task ShowSystemContextAsync()
    {
        if (closing || historyDialogOpen || catalogDialog is not null || systemContextDialog is not null) return;
        if (!busy && Service == ServiceKind.Ai && session.Settings.Chat.EnabledSkillIds.Any(id => !id.StartsWith("mcp:", StringComparison.Ordinal)))
            await RunAsync(ct => LoadRuntimeSkillsAsync(ct));
        if (closing) return;
        var dialog = new SystemContextDialog(CaptureSystemContext(), session.ContextOptions.AppName ?? DesktopBranding.AppName,
            Service == ServiceKind.Agents) { XamlRoot = XamlRoot };
        SystemAppearance.PrepareDialog(dialog); systemContextDialog = dialog; historyDialogOpen = true;
        try { await dialog.ShowAsync(); }
        finally
        {
            systemContextDialog = null; historyDialogOpen = false;
            if (!closing) viewSystemContext.Focus(FocusState.Programmatic);
        }
    }
}
