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
    public ServiceSettings For(ServiceKind kind) => kind == ServiceKind.Ai ? Ai : Agents;

    public void Validate(bool allowLocal)
    {
        foreach (var settings in new[] { Ai, Agents })
        {
            if (!allowLocal && settings.Location == RuntimeLocation.Local)
                throw new InvalidOperationException("This host only supports remote gateways.");
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
            throw new InvalidOperationException("Remote gateways must use an absolute HTTPS URL without credentials, query, or fragment; loopback is reserved for managed local mode.");
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
    public string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIHappey", "Desktop", Host.ProfileId);
    public string HistoryPartition => HistoryStore.Partition(Host.ProfileId, Host.HistoryIdentity,
        Settings.Ai.Location.ToString(), Settings.Ai.Location == RuntimeLocation.Remote ? Settings.Ai.RemoteUrl.TrimEnd('/') : "managed-ai",
        Settings.Agents.Location.ToString(), Settings.Agents.Location == RuntimeLocation.Remote ? Settings.Agents.RemoteUrl.TrimEnd('/') : "managed-agents");
    public ValueTask DisposeAsync() => Runtime.DisposeAsync();
}
