using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey.Desktop.Core;

/// <summary>Small native URL/selection dialog; all candidate validation happens at Import.</summary>
public sealed class GitHubSkillImportDialog : ContentDialog, IResponsiveDialog
{
    private readonly GitHubSkillsClient client;
    private readonly Func<bool> isCurrent;
    private readonly Func<DesktopSkillDraft, CancellationToken, Task> import;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? discoveryLifetime;
    private readonly TextBox url = new() { Name = "SkillGitHubUrl", Header = DesktopResources.Get("SkillGitHubUrl"), PlaceholderText = "https://github.com/owner/repository" };
    private readonly Button discover = new() { Name = "SkillGitHubDiscover", Content = DesktopResources.Get("SkillGitHubDiscover") };
    private readonly StackPanel layout = new() { Spacing = 12 };
    private readonly StackPanel candidates = new() { Spacing = 8 };
    private readonly Dictionary<string, CheckBox> choices = new(StringComparer.Ordinal);
    private readonly TextBlock feedback = new() { Name = "SkillGitHubFeedback", TextWrapping = TextWrapping.Wrap };
    private GitHubSkillDiscovery? discovery;
    private bool complete;
    private bool working;
    private bool importing;
    public int ImportedCount { get; private set; }

    public GitHubSkillImportDialog(GitHubSkillsClient client, Func<DesktopSkillDraft, CancellationToken, Task> import, Func<bool>? isCurrent = null)
    {
        this.client = client; this.import = import; this.isCurrent = isCurrent ?? (() => true);
        Name = "GitHubSkillImportDialog"; Title = DesktopResources.Get("SkillFromGitHub");
        Resources["ContentDialogMaxWidth"] = 600d; Resources["ContentDialogMinWidth"] = 0d;
        PrimaryButtonText = DesktopResources.Get("Import"); CloseButtonText = DesktopResources.Get("Cancel"); DefaultButton = ContentDialogButton.None;
        ControlAppearance.Stock(url); ControlAppearance.Stock(discover);
        ToolbarControls.Label(url, DesktopResources.Get("SkillGitHubUrl")); ToolbarControls.Label(discover, DesktopResources.Get("SkillGitHubDiscover"));
        AutomationProperties.SetLiveSetting(feedback, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        layout.Children.Add(url); layout.Children.Add(discover);
        layout.Children.Add(new TextBlock { Text = DesktopResources.Get("SkillGitHubHint"), TextWrapping = TextWrapping.Wrap });
        layout.Children.Add(new ScrollViewer { Name = "SkillGitHubCandidates", MaxHeight = 240, Content = candidates,
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        layout.Children.Add(feedback);
        Content = new ScrollViewer { Content = layout, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        url.TextChanged += (_, _) =>
        {
            discoveryLifetime?.Cancel(); discovery = null; complete = false; choices.Clear(); candidates.Children.Clear(); feedback.Text = ""; Update();
        };
        discover.Click += async (_, _) => await DiscoverAsync();
        PrimaryButtonClick += Import;
        Closing += (_, args) => { if (importing && !lifetime.IsCancellationRequested) args.Cancel = true; };
        Opened += (_, _) => { SizeToRoot(); XamlRoot.Changed += RootChanged; url.Focus(FocusState.Programmatic); };
        Closed += (_, _) => { lifetime.Cancel(); discoveryLifetime?.Cancel(); if (XamlRoot is not null) XamlRoot.Changed -= RootChanged; };
        Update();
    }
    public void CancelAndHide() { lifetime.Cancel(); discoveryLifetime?.Cancel(); Hide(); }
    private void Check(CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (!isCurrent()) throw new OperationCanceledException(ct); }
    private void Update()
    {
        var valid = false;
        try { GitHubSkillsClient.ParseUrl(url.Text); valid = true; }
        catch (GitHubSkillException error) { if (url.Text.Length > 0 && !working) feedback.Text = error.Message; }
        discover.IsEnabled = valid && !working; url.IsEnabled = !importing;
        IsPrimaryButtonEnabled = !working && complete && choices.Values.Any(c => c.IsChecked == true);
        foreach (var choice in choices.Values) choice.IsEnabled = !importing;
    }
    private void ShowCandidates(GitHubSkillDiscovery value)
    {
        discovery = value;
        foreach (var candidate in value.Candidates)
        {
            if (choices.ContainsKey(candidate.Path)) continue;
            var label = candidate.Path.Length > 0 ? candidate.Path : candidate.Name;
            var choice = new CheckBox { Name = "SkillGitHubCandidate", Tag = candidate.Path,
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch };
            ToolbarControls.Label(choice, label); choice.Checked += (_, _) => Update(); choice.Unchecked += (_, _) => Update();
            choices.Add(candidate.Path, choice); candidates.Children.Add(choice);
        }
    }
    private async Task DiscoverAsync()
    {
        if (working) return;
        discoveryLifetime?.Cancel(); discoveryLifetime?.Dispose(); discoveryLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var ct = discoveryLifetime.Token; working = true; complete = false; discovery = null; choices.Clear(); candidates.Children.Clear();
        feedback.Text = DesktopResources.Get("SkillGitHubDiscovering"); Update();
        try
        {
            Check(ct);
            var result = await client.DiscoverAsync(url.Text, ct, value => { Check(ct); ShowCandidates(value); });
            Check(ct); ShowCandidates(result); complete = true; feedback.Text = "";
            if (result.Candidates.Count == 1) choices.Values.Single().IsChecked = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!ct.IsCancellationRequested) feedback.Text = Error(error); }
        finally { working = false; Update(); }
    }
    private async void Import(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; if (working || !complete || discovery is null) return;
        var selected = discovery.Candidates.Where(c => choices[c.Path].IsChecked == true).ToArray(); if (selected.Length == 0) return;
        var deferral = args.GetDeferral(); importing = working = true; Update();
        var errors = new List<string>(); var count = 0;
        try
        {
            foreach (var candidate in selected)
            {
                try
                {
                    Check(lifetime.Token); feedback.Text = DesktopResources.Get("SkillGitHubImporting");
                    var bytes = await client.DownloadAsync(discovery, candidate, lifetime.Token); Check(lifetime.Token);
                    var parsed = await DesktopSkillPackages.ImportAsync(bytes, lifetime.Token);
                    if (parsed.Skills.Count != 1 || parsed.Diagnostics.Count > 0) throw new InvalidDataException(string.Join("\n", parsed.Diagnostics.DefaultIfEmpty(DesktopResources.Get("SkillGitHubInvalidSkill"))));
                    Check(lifetime.Token); await import(parsed.Skills[0], lifetime.Token); Check(lifetime.Token);
                    count++; ImportedCount++; choices[candidate.Path].IsChecked = false;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { errors.Add((candidate.Path.Length > 0 ? candidate.Path : candidate.Name) + ": " + Error(error)); }
            }
            feedback.Text = DesktopResources.Format("SkillImportedCount", count) + (errors.Count == 0 ? "" : "\n" + string.Join("\n", errors));
        }
        catch (OperationCanceledException) { }
        finally { importing = working = false; Update(); deferral.Complete(); }
    }
    private static string Error(Exception error) => error is GitHubSkillException or InvalidDataException or YamlDotNet.Core.YamlException
        ? error.Message : DesktopResources.Get("SkillGitHubDownload");
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToRoot();
    void IResponsiveDialog.SizeToRoot() => SizeToRoot();
    private void SizeToRoot()
    {
        if (XamlRoot is null) return;
        layout.Width = Math.Max(0, Math.Min(480, XamlRoot.Size.Width - 96));
        ((ScrollViewer)Content).MaxHeight = Math.Max(0, XamlRoot.Size.Height - 240);
    }
}
