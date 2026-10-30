using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;

namespace AIHappey.Desktop.UiTests;

public partial class App
{
    private async Task<bool> RunUiSuitesAsync()
    {
        async Task BothHosts(Func<bool, ElementTheme, Task> run, ElementTheme theme)
        {
            foreach (var local in new[] { true, false }) await run(local, theme);
        }
        var suites = new Dictionary<string, Func<ElementTheme, Task>>(StringComparer.Ordinal)
        {
            ["startup"] = CheckStartupAsync,
            ["charts"] = CheckChatChartsAsync,
            ["chat-files"] = CheckChatFilesAsync,
            ["context-search"] = CheckContextSearchAsync,
            ["composer-progress"] = CheckToolLayoutAndSkillProgressAsync,
            ["transcript"] = theme => BothHosts(CheckTranscriptAsync, theme),
            ["shell"] = theme => BothHosts(CheckShellAsync, theme),
            ["sidebar"] = theme => BothHosts(CheckSidebarAsync, theme),
            ["streaming-attachments"] = CheckStreamingAttachmentsAsync,
            ["icons"] = CheckIconsAsync,
            ["overview-tabs"] = CheckOverviewTabsAsync,
            ["usage"] = CheckUsageAsync,
            ["providers"] = CheckProvidersAsync,
            ["agents"] = CheckAgentsAsync,
            ["mcp-presentation"] = CheckMcpPresentationAsync,
            ["elicitation"] = CheckElicitationAsync,
            ["ai-models"] = CheckAiModelsAsync,
            ["skills"] = CheckSkillsAsync,
            ["local-tools"] = CheckLocalToolsAsync,
            ["images"] = CheckImagesAsync,
            ["media-libraries"] = CheckMediaLibrariesAsync,
            ["transcription-tabs"] = CheckTranscriptionTabsAsync,
            ["videos"] = CheckVideosAsync,
            // Previously these were reachable only through focused flags, not the full run.
            ["realtime"] = CheckRealtimeAsync,
            ["explorer"] = CheckExplorerAsync,
            ["chat-skills-settings"] = CheckChatSkillsSettingsAsync,
            ["settings-appearance"] = CheckChatToolsSettingsAsync,
            ["chat-settings-scrolling"] = CheckChatSettingsScrollingAsync,
            ["chat-provider-forms"] = CheckNativeChatProvidersAsync,
            ["tool-output"] = CheckToolOutputSettingsAsync,
        };
        var flags = Environment.GetCommandLineArgs().Skip(2).ToArray();
        // Hardware/media access is explicit opt-in, never part of the default UI regression run.
        if (flags.SequenceEqual(["--fishaudio-media-only"])) suites["fishaudio-media"] = _ => CheckFishAudioHiddenMediaAsync();
        var selected = suites.Keys.ToArray();
        if (flags.Length > 0)
        {
            if (flags.Length != 1 || !flags[0].StartsWith("--", StringComparison.Ordinal)
                || !flags[0].EndsWith("-only", StringComparison.Ordinal)) throw new ArgumentException("Expected --<suite>-only.");
            var name = flags[0][2..^5];
            selected = name switch
            {
                "activity" => ["transcript"],
                "authoring-tools" => ["local-tools"],
                "chat-settings" => ["context-search", "composer-progress", "chat-skills-settings", "settings-appearance", "chat-settings-scrolling", "chat-provider-forms"],
                _ => [name]
            };
            foreach (var key in selected) if (!suites.ContainsKey(key)) throw new ArgumentException("Unknown UI suite: " + key);
        }
        Check(DesktopBranding.ResolveName(null) == "aihappey" && DesktopBranding.ResolveName(" ") == "aihappey"
            && DesktopBranding.ResolveName(" chathappey ") == "chathappey", "build branding fallback and custom name");
        var failed = 0;
        foreach (var name in selected)
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                results.Add($"SUITE START: {name}/{theme}");
                File.WriteAllLines(report, results);
                try
                {
                    await suites[name](theme);
                    results.Add($"SUITE PASS: {name}/{theme}");
                }
                catch (Exception error)
                {
                    failed++;
                    results.Add($"SUITE FAIL: {name}/{theme}: {error}");
                }
                File.WriteAllLines(report, results);
            }
        results.Add($"UI SUITES COMPLETE: {selected.Length * 2 - failed} passed; {failed} failed; {selected.Length * 2} total.");
        results.Add(failed == 0 ? "UI COMPLETE: passed" : "UI COMPLETE: failed");
        File.WriteAllLines(report, results);
        return failed == 0;
    }
}
