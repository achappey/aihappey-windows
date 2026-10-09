using ModelContextProtocol;

namespace AIHappey.Desktop.Core;

/// <summary>SDK progress is already correlated to the request. Report synchronously, never via a UI context.</summary>
public sealed class McpToolTimeout : IProgress<ProgressNotificationValue>, IDisposable
{
    private readonly object sync = new();
    private readonly CancellationTokenSource source;
    private readonly TimeSpan duration;
    private readonly bool resetOnProgress;
    private bool disposed;
    public CancellationToken Token => source.Token;
    public McpToolTimeout(TimeSpan duration, bool resetOnProgress, CancellationToken ct)
    {
        this.duration = duration; this.resetOnProgress = resetOnProgress;
        source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(duration);
    }
    public void Report(ProgressNotificationValue value)
    {
        lock (sync)
            if (!disposed && resetOnProgress && !source.IsCancellationRequested) source.CancelAfter(duration);
    }
    public void Dispose()
    {
        lock (sync) { disposed = true; source.Dispose(); }
    }
}
