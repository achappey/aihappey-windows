namespace AIHappey.Desktop.Core;

/// <summary>Also binds legacy server-initiated callbacks to the active request's stop/timeout token.</summary>
internal sealed class McpRequestLifetime : IDisposable
{
    private readonly object sync = new();
    private readonly CancellationTokenSource connection = new();
    private CancellationToken active;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public void SetActive(CancellationToken token) { lock (sync) active = token; }
    public CancellationTokenSource Link(CancellationToken serverRequest)
    {
        lock (sync) return CancellationTokenSource.CreateLinkedTokenSource(serverRequest, connection.Token, active);
    }
    public void Dispose() { connection.Cancel(); connection.Dispose(); }
}
