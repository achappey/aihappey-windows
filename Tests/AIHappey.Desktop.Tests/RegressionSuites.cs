using AIHappey.Desktop.Core;

/// <summary>Independent suites: an unrelated failure must not prevent discovery of later failures.</summary>
internal static class RegressionSuites
{
    public static async Task RunAsync(string[] args, Action<bool, string> check, Action<Action, string> reject,
        string root, Func<Task> boundaries)
    {
        Task Sync(Action action) { action(); return Task.CompletedTask; }
        string Data(string suite) => Path.Combine(root, suite);
        DesktopSession Session() => new(new TestHost(), new TestRuntime(), new());
        var suites = new Dictionary<string, Func<Task>>(StringComparer.Ordinal)
        {
            ["runner"] = () => RunnerRegressionTests.RunAsync(check),
            ["canvas"] = () => Sync(() => CanvasRegressionTests.Run(check)),
            ["primitive-routing"] = () => PrimitiveRoutingRegressionTests.RunAsync(check, Data("routing")),
            ["realtime"] = () => RealtimeRegressionTests.RunAsync(check, Data("realtime")),
            ["agent-realtime"] = async () => { await AgentRealtimeRegressionTests.RunAsync(check, Data("agent-realtime")); await StreamingSpeechRegressionTests.RunAsync(check); },
            ["overview-consistency"] = () => OverviewConsistencyRegressionTests.RunAsync(check, Data("overview")),
            ["usage"] = () => UsageRegressionTests.RunAsync(check, Data("usage")),
            ["enterprise-settings"] = () => EnterpriseSettingsRegressionTests.RunAsync(check, Data("enterprise")),
            ["provider-settings"] = () => ProviderSettingsRegressionTests.RunAsync(check, Data("provider-settings")),
            ["explorer"] = () => ExplorerRegressionTests.RunAsync(check, Data("explorer")),
            ["plugins"] = () => PluginRegressionTests.RunAsync(check, Data("plugins")),
            // Includes chat-tool settings, tool-output content, and provider forms.
            ["chat-settings"] = () => ChatSettingsRegressionTests.RunAsync(check, Data("chat-settings")),
            ["chat-debug"] = () => ChatDebugRegressionTests.RunAsync(check, Data("chat-debug")),
            ["chat-files"] = () => ChatFileRegressionTests.RunAsync(check, Data("chat-files")),
            ["chat-errors"] = () => ChatErrorRegressionTests.RunAsync(check, Data("chat-errors")),
            ["providers"] = () => ProviderRegressionTests.RunAsync(check, Data("providers")),
            // Includes shared cursors, local files, Windows tools, desktop tools, and authoring.
            ["local-tools"] = () => LocalToolRegressionTests.RunAsync(check, Data("local-tools")),
            ["models-overview"] = () => ModelsOverviewRegressionTests.RunAsync(check, Data("models")),
            ["agents"] = () => AgentRegressionTests.RunAsync(check, Data("agents")),
            ["transcript"] = () => Sync(() => TranscriptRegressionTests.Run(check)),
            ["tool-approval"] = () => ToolApprovalRegressionTests.RunAsync(check, Data("approval")),
            ["mcp-presentation"] = () => McpPresentationRegressionTests.RunAsync(check, Data("mcp-presentation")),
            ["mcp-auth"] = () => McpAuthenticationRegressionTests.RunAsync(check, Data("mcp-auth")),
            ["elicitation"] = () => ElicitationRegressionTests.RunAsync(check, Data("elicitation")),
            ["skills"] = () => SkillRegressionTests.RunAsync(check, Data("skills")),
            ["images"] = () => ImageRegressionTests.RunAsync(check, Data("images")),
            ["videos"] = () => VideoRegressionTests.RunAsync(check, Data("videos")),
            ["speech"] = () => SpeechRegressionTests.RunAsync(check, Data("speech")),
            ["composer-attachments"] = () => ComposerAttachmentRegressionTests.RunAsync(check, reject, Session()),
            ["catalog"] = () => CatalogRegressionTests.RunAsync(check, reject, Data("catalog"), Session()),
            ["ai-models"] = () => AiModelRegressionTests.RunAsync(check, Data("ai-models"), Session()),
            ["boundaries"] = boundaries,
        };
        if (args.SequenceEqual(["--list-suites"]))
        {
            foreach (var name in suites.Keys) Console.WriteLine(name);
            return;
        }
        // Existing focused flags remain supported, but no longer run unrelated prerequisites.
        var extra = new Dictionary<string, Func<Task>>(StringComparer.Ordinal)
        {
            ["windows-tools"] = () => WindowsToolRegressionTests.RunAsync(check, Data("windows-tools")),
            ["authoring-tools"] = () => AuthoringToolRegressionTests.RunAsync(check, Data("authoring-tools")),
            ["chat-provider-forms"] = () => ChatProviderFormRegressionTests.RunAsync(check, Data("provider-forms")),
        };
        string[] selected = suites.Keys.ToArray();
        if (args.Length > 0 && args[0].StartsWith('-'))
        {
            if (args.Length != 1 || !args[0].EndsWith("-only", StringComparison.Ordinal))
                throw new ArgumentException("Use --list-suites, --usage-snapshot, or --<suite>-only.");
            var name = args[0][2..^5];
            selected = name switch
            {
                "chat-streaming" => ["chat-files", "chat-errors", "transcript"],
                "agent-presentation" => ["agents", "transcript"],
                _ => [name]
            };
            foreach (var key in selected)
                if (!suites.ContainsKey(key) && !extra.ContainsKey(key)) throw new ArgumentException("Unknown suite: " + key);
        }
        else if (args.Length > 1) throw new ArgumentException("Expected at most one bundled-runtime directory.");

        var failed = await ExecuteAsync(selected, name => suites.TryGetValue(name, out var run) ? run : extra[name], Console.Out, Console.Error);
        Console.WriteLine($"SUITES COMPLETE: {selected.Length - failed} passed; {failed} failed; {selected.Length} total.");
        if (failed != 0) Environment.ExitCode = 1;
    }

    internal static async Task<int> ExecuteAsync(IEnumerable<string> selected, Func<string, Func<Task>> resolve,
        TextWriter output, TextWriter errors)
    {
        var failed = 0;
        foreach (var name in selected)
        {
            output.WriteLine("SUITE START: " + name);
            try
            {
                await resolve(name)();
                output.WriteLine("SUITE PASS: " + name);
            }
            catch (Exception error)
            {
                failed++;
                errors.WriteLine($"SUITE FAIL: {name}\n{error}");
            }
        }
        return failed;
    }
}
