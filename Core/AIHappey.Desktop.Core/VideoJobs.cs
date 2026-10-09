using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed class VideoJob
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Partition { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Model { get; set; } = "";
    public string Prompt { get; set; } = "";
    public int RequestedVideos { get; set; }
    public string OutputRoot { get; set; } = "";
    public JsonObject Request { get; set; } = new();
    public string? Operation { get; set; }
    public string State { get; set; } = "submitting";
    public string? Error { get; set; }
    public List<string> Warnings { get; set; } = [];
    public bool Pending => State is "submitting" or "pending";
    public VideoJob Snapshot(bool includeRequest = true) => new()
    {
        Version = Version, Id = Id, Partition = Partition, CreatedAt = CreatedAt, Model = Model, Prompt = Prompt,
        RequestedVideos = RequestedVideos, OutputRoot = OutputRoot, Request = includeRequest ? (JsonObject)Request.DeepClone() : new(),
        Operation = Operation, State = State, Error = Error, Warnings = Warnings.ToList()
    };
}

public sealed class VideoJobStore(string root, string partition)
{
    public string Folder { get; } = Path.Combine(Path.GetFullPath(root), HistoryStore.Partition(partition));
    public Task SaveAsync(VideoJob job)
    {
        if (!VideoDisk.SafeId(job.Id) || job.Partition != partition || !Path.IsPathFullyQualified(job.OutputRoot)) throw new IOException("Invalid video job.");
        return VideoDisk.WriteAsync(VideoDisk.Leaf(Folder, job.Id + ".json"), job);
    }
    private string Sidecar(string id, string suffix) => VideoDisk.SafeId(id) ? VideoDisk.Leaf(Folder, id + suffix) : throw new IOException("Invalid video job identifier.");
    public Task SaveReceiptAsync(VideoJob job) => VideoDisk.WriteAsync(Sidecar(job.Id, ".operation"), new JsonObject { ["operation"] = job.Operation });
    public Task SaveResultAsync(VideoJob job, JsonObject result) => VideoDisk.WriteAsync(Sidecar(job.Id, ".result"), VideoDisk.Metadata(result));
    public async Task<JsonObject?> ReadResultAsync(VideoJob job, CancellationToken ct)
    {
        var file = Sidecar(job.Id, ".result"); return File.Exists(file) ? await VideoDisk.ReadObjectAsync(File.OpenRead(file), VideoDisk.MaximumJsonBytes, ct) : null;
    }
    public void DeleteResult(VideoJob job) { var file = Sidecar(job.Id, ".result"); if (File.Exists(file)) File.Delete(file); }
    public async Task<IReadOnlyList<VideoJob>> LoadAsync(CancellationToken ct = default)
    {
        VideoDisk.Prepare(Folder); var jobs = new List<VideoJob>();
        foreach (var path in Directory.EnumerateFiles(Folder, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            if (!VideoDisk.SafeId(Path.GetFileNameWithoutExtension(path))) continue;
            try
            {
                VideoDisk.CheckPath(path);
                var job = (await VideoDisk.ReadObjectAsync(File.OpenRead(path), VideoDisk.MaximumJsonBytes, ct)).Deserialize<VideoJob>(VideoDisk.Json);
                if (job is null || job.Version != 1 || job.Partition != partition || job.Id != Path.GetFileNameWithoutExtension(path)
                    || job.RequestedVideos is < 1 or > 10 || !Path.IsPathFullyQualified(job.OutputRoot) || job.Request is null) continue;
                if (job.State is "submitting" or "uncertain")
                {
                    var receipt = Sidecar(job.Id, ".operation");
                    if (File.Exists(receipt)) job.Operation = VideoDisk.Text((await VideoDisk.ReadObjectAsync(File.OpenRead(receipt), 1024 * 1024, ct))["operation"]);
                    if (job.Operation is not null) { _ = DesktopVideoClient.StatusPath(job.Operation); job.State = "pending"; job.Error = null; }
                    else { job.State = "uncertain"; job.Error = DesktopResources.Get("VideoStartUncertain"); }
                    await SaveAsync(job);
                }
                if (job.State == "pending") _ = DesktopVideoClient.StatusPath(job.Operation ?? "");
                jobs.Add(job);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException) { }
        }
        return jobs.OrderByDescending(j => j.CreatedAt).ToArray();
    }
}

/// <summary>One coordinator per shell. No WinUI dependencies and no shell-wide busy/operation token.</summary>
public sealed class VideoJobCoordinator : IAsyncDisposable
{
    private readonly string root;
    private readonly IDesktopVideoClient client;
    private readonly HttpClient anonymousHttp;
    private readonly Func<string> currentPartition;
    private readonly Func<int> interval;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim polling = new(1), submitting = new(1), state = new(1);
    private readonly List<VideoJob> jobs = [];
    private Task? loop;
    private string? partition;
    private bool disposed;
    public event Action? Changed;
    public VideoJobCoordinator(string root, IDesktopVideoClient client, HttpClient anonymousHttp, Func<string> currentPartition, Func<int> interval)
    { this.root = root; this.client = client; this.anonymousHttp = anonymousHttp; this.currentPartition = currentPartition; this.interval = interval; }
    public async Task StartAsync(CancellationToken ct = default)
    {
        await EnsurePartitionAsync(ct); loop ??= LoopAsync();
    }
    private async Task EnsurePartitionAsync(CancellationToken ct)
    {
        await state.WaitAsync(ct);
        try
        {
            var next = currentPartition(); if (partition == next) return;
            var saved = await new VideoJobStore(root, next).LoadAsync(ct);
            partition = next; jobs.Clear(); jobs.AddRange(saved);
        }
        finally { state.Release(); }
        Notify();
    }
    public async Task<IReadOnlyList<VideoJob>> SnapshotAsync(CancellationToken ct = default)
    {
        await EnsurePartitionAsync(ct); await state.WaitAsync(ct);
        try { return jobs.Select(j => j.Snapshot(includeRequest: false)).ToArray(); } finally { state.Release(); }
    }
    private void Notify() { if (!disposed) Changed?.Invoke(); }
    private async Task PublishAsync(VideoJob job)
    {
        await state.WaitAsync();
        try
        {
            if (partition != job.Partition) return;
            var index = jobs.FindIndex(j => j.Id == job.Id);
            if (index < 0) jobs.Insert(0, job.Snapshot()); else jobs[index] = job.Snapshot();
        }
        finally { state.Release(); }
        Notify();
    }
    public async Task SubmitAsync(IReadOnlyList<JsonObject> requests, string outputRoot, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        await submitting.WaitAsync(linked.Token);
        try
        {
            await EnsurePartitionAsync(linked.Token); var captured = currentPartition();
            var store = new VideoJobStore(root, captured);
            // Check destination before any paid request; persistence is a precondition of submission.
            VideoDisk.Prepare(new VideoLibraryStore(outputRoot, captured).Folder);
            foreach (var request in requests)
            {
                linked.Token.ThrowIfCancellationRequested(); if (currentPartition() != captured) throw new OperationCanceledException(linked.Token);
                var job = new VideoJob { Partition = captured, OutputRoot = Path.GetFullPath(outputRoot), Model = VideoDisk.Text(request["model"]) ?? "",
                    Prompt = VideoDisk.Text(request["prompt"]) ?? "", RequestedVideos = request["n"]!.GetValue<int>(), Request = VideoDisk.Metadata(request) };
                await store.SaveAsync(job);
                await PublishAsync(job);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token); timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    var response = await client.StartAsync(request, captured, timeout.Token);
                    job.Operation = VideoDisk.Text(response["operation"]) ?? throw new JsonException("Missing video operation.");
                    _ = DesktopVideoClient.StatusPath(job.Operation);
                    job.State = "pending"; job.Warnings = VideoDisk.Warnings(response).ToList();
                    // Do not allow navigation/shutdown cancellation to discard an accepted operation.
                    await store.SaveReceiptAsync(job);
                    await store.SaveAsync(job); await PublishAsync(job);
                }
                catch
                {
                    if (job.Operation is null) { job.State = "uncertain"; job.Error = DesktopResources.Get("VideoStartUncertain"); }
                    else { job.State = "pending"; job.Error = DesktopResources.Get("VideoStorageRetry"); }
                    try { if (job.Operation is not null) await store.SaveReceiptAsync(job); await store.SaveAsync(job); }
                    finally { await PublishAsync(job); }
                    throw;
                }
            }
        }
        finally { submitting.Release(); }
    }
    private async Task LoopAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try { await PollOnceAsync(lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            catch (Exception) { /* Journal/endpoint temporarily unavailable; retain jobs and retry later. */ }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(interval(), 5, 60)), lifetime.Token); }
            catch (OperationCanceledException) { break; }
        }
    }
    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        if (!await polling.WaitAsync(0, ct)) return;
        try
        {
            await EnsurePartitionAsync(ct); var captured = currentPartition();
            await state.WaitAsync(ct); VideoJob[] pending;
            try { pending = jobs.Where(j => j.State == "pending").Select(j => j.Snapshot()).ToArray(); } finally { state.Release(); }
            // Sequential polling bounds video response memory and prevents duplicate commits for a job.
            foreach (var job in pending)
            {
                ct.ThrowIfCancellationRequested(); if (currentPartition() != captured) return;
                var store = new VideoJobStore(root, captured);
                try
                {
                    // Retry an operation journal write before asking for the next status.
                    await store.SaveAsync(job);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    var result = await store.ReadResultAsync(job, ct) ?? await client.StatusAsync(job.Operation!, captured, timeout.Token);
                    job.Warnings = job.Warnings.Concat(VideoDisk.Warnings(result)).Distinct().Take(100).ToList();
                    var status = VideoDisk.Text(result["status"]);
                    if (status == "pending") job.Error = null;
                    else if (status == "error") { await store.SaveResultAsync(job, result); job.State = "error"; job.Error = VideoDisk.Text(result["error"]) ?? DesktopResources.Get("VideoGenerationFailed"); }
                    else if (status == "completed")
                    {
                        // Keep the returned output in app data before touching a removable/custom destination.
                        // A restart/storage retry can finish locally even if provider results later expire.
                        await store.SaveResultAsync(job, result);
                        // Commit to captured account/root, even if the account changed after this response.
                        await new VideoLibraryStore(job.OutputRoot, captured).SaveAsync(job, result, anonymousHttp, ct);
                        job.State = "completed"; job.Error = null;
                        // Paid request bytes are no longer required; avoid retaining large input blobs forever.
                        job.Request.Remove("image"); job.Request.Remove("inputReferences"); job.Request.Remove("frameImages");
                    }
                    else throw new JsonException("Unknown video status.");
                    await store.SaveAsync(job); await PublishAsync(job);
                    if (job.State is "completed" or "error") store.DeleteResult(job);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    // A failed terminal journal write must retry, while deterministic commit prevents duplicates.
                    if (job.State is "completed" or "error") job.State = "pending";
                    job.Error = DesktopResources.Get("VideoPollingRetry");
                    try { await store.SaveAsync(job); } catch (Exception) { }
                    await PublishAsync(job);
                }
            }
        }
        finally { polling.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        disposed = true; Changed = null; lifetime.Cancel(); if (loop is not null) await loop;
        await submitting.WaitAsync(); submitting.Release(); await polling.WaitAsync(); polling.Release(); lifetime.Dispose();
    }
}
