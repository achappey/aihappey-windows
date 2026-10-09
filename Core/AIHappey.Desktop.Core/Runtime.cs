namespace AIHappey.Desktop.Core;

public enum ServiceKind { Ai, Agents }
public enum RuntimeLocation { Local, Remote }

public sealed class ServiceSettings
{
    public RuntimeLocation Location { get; set; } = RuntimeLocation.Local;
    public string RemoteUrl { get; set; } = "";
}

public sealed class DesktopSettings
{
    public ServiceSettings Ai { get; set; } = new();
    public ServiceSettings Agents { get; set; } = new();
    public bool ConvertAttachmentsToText { get; set; } = true;
    public string? Language { get; set; }
    public ChatPreferences Chat { get; set; } = new();
    private AiModelPreferences aiModels = new();
    public AiModelPreferences AiModels { get => aiModels; set => aiModels = value ?? new(); }
    private ModelContextPreferences modelContext = new();
    public ModelContextPreferences ModelContext { get => modelContext; set => modelContext = value ?? new(); }
    private List<string> allowedToolList = [];
    public List<string> AllowedToolList
    {
        get => allowedToolList;
        set => allowedToolList = value?.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToList() ?? [];
    }
    public ServiceSettings For(ServiceKind kind) => kind == ServiceKind.Ai ? Ai : Agents;

    public DesktopSettings Clone() => new()
    {
        Ai = new() { Location = Ai.Location, RemoteUrl = Ai.RemoteUrl },
        Agents = new() { Location = Agents.Location, RemoteUrl = Agents.RemoteUrl },
        Language = Language, ConvertAttachmentsToText = ConvertAttachmentsToText,
        Chat = Chat.Clone(), AiModels = AiModels.Clone(), ModelContext = ModelContext.Clone(),
        AllowedToolList = AllowedToolList.ToList()
    };

    public void Validate(bool allowLocal)
    {
        foreach (var settings in new[] { Ai, Agents })
        {
            if (!allowLocal && settings.Location == RuntimeLocation.Local)
                throw new InvalidOperationException(DesktopResources.Get("RemoteOnly"));
            if (settings.Location == RuntimeLocation.Remote)
                _ = RemoteUri(settings.RemoteUrl);
        }
    }

    public static Uri RemoteUri(string url)
    {
        if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.IsLoopback)
            throw new InvalidOperationException(DesktopResources.Get("InvalidRemoteUrl"));
        return uri;
    }
}

/// <summary>Host policy and identity. Core never knows how tokens or provider keys are stored.</summary>
public interface IDesktopHost
{
    string ProfileId { get; }
    bool AllowLocal { get; }
    string AccountLabel { get; }
    string HistoryIdentity { get; }
    DesktopUserContext? UserContext => null;
    Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken cancellationToken);
    Task ManageAccountAsync(object xamlRoot, CancellationToken cancellationToken);
}

/// <summary>Resolves endpoints; only the public host supplies a managed-process implementation.</summary>
public interface IRuntimeResolver : IAsyncDisposable
{
    Task<Uri> ResolveAsync(ServiceKind service, DesktopSettings settings, CancellationToken cancellationToken);
}

public sealed class RemoteRuntimeResolver : IRuntimeResolver
{
    public Task<Uri> ResolveAsync(ServiceKind service, DesktopSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate(false);
        return Task.FromResult(DesktopSettings.RemoteUri(settings.For(service).RemoteUrl));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class DesktopSession(IDesktopHost host, IRuntimeResolver runtime, DesktopSettings settings) : IAsyncDisposable
{
    public IDesktopHost Host { get; } = host;
    public IRuntimeResolver Runtime { get; } = runtime;
    public DesktopSettings Settings { get; set; } = settings;
    public string ActiveLanguage { get; set; } = "en";
    public DesktopContextOptions ContextOptions { get; set; } = new();
    public ISystemContextComposer ContextComposer { get; set; } = new DesktopSystemContextComposer();
    public IDesktopMcpClientFactory McpClientFactory { get; set; } = new DesktopMcpClientFactory();
    public DesktopToolApprovalPolicy ToolApprovals { get; } = new();
    public DesktopMcpManager? Mcp { get; private set; }
    public DesktopElicitationHandler? ElicitationHandler { get; set; }
    public Task<ModelContextProtocol.Protocol.ElicitResult> ElicitAsync(string origin,
        ModelContextProtocol.Protocol.ElicitRequestParams request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Settings.ModelContext.EnableFormElicitation && ElicitationHandler is { } handler
            ? handler(origin, request, ct)
            : Task.FromResult(new ModelContextProtocol.Protocol.ElicitResult { Action = "decline" });
    }
    public string McpPartition => HistoryStore.Partition(Host.ProfileId, Host.HistoryIdentity);
    public DesktopMcpManager InitializeMcp()
    {
        if (Mcp is not null) return Mcp;
        if (McpClientFactory is DesktopMcpClientFactory sdk) sdk.Configure(() => Settings.ModelContext, ElicitAsync);
        return Mcp = new(McpClientFactory, new DesktopMcpStore(Path.Combine(DataDirectory, "mcp")), () => Settings.ModelContext);
    }
    public Func<DateTimeOffset, System.Text.Json.Nodes.JsonObject> SystemInformationProvider { get; set; } = DesktopSystemContext.SystemInformation;
    public AIHappey.Vercel.Models.UIMessage CaptureSystemContext(bool darkMode = false,
        System.Text.Json.Nodes.JsonObject? systemInformation = null, ChatPreferences? preferences = null, McpTurnSnapshot? mcp = null)
    {
        var now = DateTimeOffset.UtcNow;
        return ContextComposer.Compose(new(ContextOptions, systemInformation ?? SystemInformationProvider(now),
            Host.UserContext, ActiveLanguage, darkMode, (preferences ?? Settings.Chat).SystemInstructions, now)
            { Mcp = mcp ?? Mcp?.Capture() ?? McpTurnSnapshot.Empty });
    }
    public string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIHappey", "Desktop", Host.ProfileId);
    public string HistoryPartition => HistoryStore.Partition(Host.ProfileId, Host.HistoryIdentity,
        Settings.Ai.Location.ToString(), Settings.Ai.Location == RuntimeLocation.Remote ? Settings.Ai.RemoteUrl.TrimEnd('/') : "managed-ai",
        Settings.Agents.Location.ToString(), Settings.Agents.Location == RuntimeLocation.Remote ? Settings.Agents.RemoteUrl.TrimEnd('/') : "managed-agents");
    public async ValueTask DisposeAsync() { if (Mcp is not null) await Mcp.DisposeAsync(); await Runtime.DisposeAsync(); }
}
