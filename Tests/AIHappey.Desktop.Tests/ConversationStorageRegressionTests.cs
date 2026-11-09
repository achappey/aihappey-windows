using AIHappey.Desktop.Core;

internal static class ConversationStorageRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var host = new ConversationHost();
        await using var session = new DesktopSession(host, new TestRuntime(), new());
        var directory = Path.Combine(root, "fresh-cloud");
        session.Settings = await SettingsStore.LoadForSessionAsync(directory, session);
        check(session.Settings.ConversationStorage.CloudEnabled && session.Settings.ConversationStorage.Selected == "local"
            && !session.UsesRemoteConversations, "conversation storage: fresh cloud-capable profiles enable Cloud but stay local");
        check(host.Authorizations == 0, "conversation storage: enabling the default never requests consent or remote IO");
        await SettingsStore.SaveAsync(directory, session.Settings);
        session.Settings = await SettingsStore.LoadForSessionAsync(directory, session);
        check(session.Settings.ConversationStorage.CloudEnabled && !session.UsesRemoteConversations,
            "conversation storage: default Cloud/local preferences survive restart");

        foreach (var enabled in new[] { false, true })
        foreach (var selected in new[] { "local", "remote" })
        {
            var saved = new DesktopSettings { ConversationStorage = new() { CloudEnabled = enabled, Selected = selected } };
            await SettingsStore.SaveAsync(directory, saved);
            session.Settings = await SettingsStore.LoadForSessionAsync(directory, session);
            check(session.Settings.ConversationStorage.CloudEnabled == enabled
                && session.Settings.ConversationStorage.Selected == (enabled ? selected : "local")
                && session.UsesRemoteConversations == (enabled && selected == "remote"),
                $"conversation storage: saved preference wins over defaults ({enabled}, {selected})");
        }

        session.Settings.ConversationStorage.CloudEnabled = false;
        await SettingsStore.SaveAsync(directory, session.Settings);
        session.Settings = await SettingsStore.LoadForSessionAsync(directory, session);
        check(!session.Settings.ConversationStorage.CloudEnabled && !session.UsesRemoteConversations,
            "conversation storage: explicit opt-out survives restart");

        host.ConversationsEndpoint = null;
        session.Settings = await SettingsStore.LoadForSessionAsync(Path.Combine(root, "no-endpoint"), session);
        check(!session.Settings.ConversationStorage.CloudEnabled && session.Settings.ConversationStorage.Selected == "local"
            && !session.UsesRemoteConversations, "conversation storage: an unconfigured host stays local with Cloud off");
        await using var publicSession = new DesktopSession(new TestHost(), new TestRuntime(), new());
        publicSession.Settings = await SettingsStore.LoadForSessionAsync(Path.Combine(root, "public-host"), publicSession);
        check(!publicSession.Settings.ConversationStorage.CloudEnabled && !publicSession.UsesRemoteConversations,
            "conversation storage: hosts without conversation support keep Cloud off");
        check(host.Authorizations == 0, "conversation storage: loading defaults or saved settings never authenticates");
    }

    private sealed class ConversationHost : IDesktopHost, IDesktopConversationHost
    {
        public string ProfileId => "conversation-defaults-test";
        public bool AllowLocal => true;
        public string AccountLabel => "test";
        public string HistoryIdentity => "test";
        public Uri? ConversationsEndpoint { get; set; } = new("https://cloud.example/conversations");
        public int Authorizations { get; private set; }
        public Task AuthenticateConversationsAsync(HttpRequestMessage request, bool interactive, CancellationToken cancellationToken)
        { Authorizations++; throw new InvalidOperationException("Unexpected conversation authentication while loading preferences."); }
        public Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ManageAccountAsync(object xamlRoot, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
