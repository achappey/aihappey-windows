namespace AIHappey.Desktop.Core;

/// <summary>Cloud availability preference and explicit source selection; neither choice migrates conversation documents.</summary>
public sealed class ConversationStoragePreferences
{
    private bool cloudEnabled;
    private string selected = "local";
    public bool CloudEnabled
    {
        get => cloudEnabled;
        set { cloudEnabled = value; if (!value) selected = "local"; }
    }
    public string Selected
    {
        get => cloudEnabled ? selected : "local";
        set => selected = value == "remote" ? "remote" : "local";
    }
    public ConversationStoragePreferences Clone() => new() { CloudEnabled = CloudEnabled, Selected = Selected };
}

/// <summary>Optional host-owned conversation endpoint and authentication, independent of inference services.</summary>
public interface IDesktopConversationHost
{
    Uri? ConversationsEndpoint { get; }
    Task AuthenticateConversationsAsync(HttpRequestMessage request, bool interactive, CancellationToken cancellationToken);
}
