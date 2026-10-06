using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AIHappey.Desktop.Core;

namespace AIHappey_Desktop_HeaderAuth;

/// <summary>Owns child processes, not server assemblies. Both locations use the same Core HTTP client.</summary>
public sealed class ManagedLocalRuntime : IRuntimeResolver
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<ServiceKind, (Process Process, Uri Endpoint)> children = [];
    private readonly string root;
    private readonly string stateRoot;
    private OwnedProcessJob? job;
    private readonly HttpClient probe = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };

    public ManagedLocalRuntime(string root, string stateRoot)
    {
        this.root = root; this.stateRoot = stateRoot;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopOwned();
    }

    public async Task<Uri> ResolveAsync(ServiceKind service, DesktopSettings settings, CancellationToken ct)
    {
        settings.Validate(true);
        if (settings.For(service).Location == RuntimeLocation.Remote)
            return DesktopSettings.RemoteUri(settings.For(service).RemoteUrl);
        await gate.WaitAsync(ct);
        try
        {
            // A local Agents process always receives the configured AI endpoint, including mixed local/remote mode.
            Uri? ai = null;
            if (service == ServiceKind.Agents)
                ai = settings.Ai.Location == RuntimeLocation.Remote ? DesktopSettings.RemoteUri(settings.Ai.RemoteUrl) : await EnsureAsync(ServiceKind.Ai, null, ct);
            return await EnsureAsync(service, ai, ct);
        }
        finally { gate.Release(); }
    }

    private async Task<Uri> EnsureAsync(ServiceKind service, Uri? ai, CancellationToken ct)
    {
        if (children.TryGetValue(service, out var existing))
        {
            if (!existing.Process.HasExited) return existing.Endpoint;
            var exitCode = existing.Process.ExitCode;
            existing.Process.Dispose(); children.Remove(service);
            // A fresh request can explicitly restart; do not silently repeat inference after a crash.
            throw new InvalidOperationException($"The managed {service} service exited (code {exitCode}). Refresh the catalog to restart it.");
        }
        var executable = Path.Combine(root, service == ServiceKind.Ai ? "ai" : "agents", service == ServiceKind.Ai ? "AIHappey.Windows.exe" : "AgentHappey.Windows.exe");
        if (!File.Exists(executable)) throw new InvalidOperationException(
            $"The bundled {service} runtime is missing at '{executable}'. " +
            "For development, select Desktop HeaderAuth in VS Code and press F5; its pre-launch task builds the complete bundle. " +
            "For a published app, launch the desktop executable from the complete published folder; do not copy the executable alone.");
        var endpoint = new Uri($"http://127.0.0.1:{AvailablePort()}/");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add(endpoint.AbsoluteUri.TrimEnd('/'));
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        // Discard provider logs: server debug output can contain prompts or sensitive provider diagnostics.
        start.Environment["Logging__LogLevel__Default"] = "None";
        start.Environment["Logging__LogLevel__Microsoft.AspNetCore"] = "None";
        if (service == ServiceKind.Ai)
        {
            // Do not inherit legacy standalone provider keys into a desktop-managed service.
            // The empty file contains no credentials; all keys come from protected host request headers.
            Directory.CreateDirectory(stateRoot);
            var emptyDefaults = Path.Combine(stateRoot, "managed-empty-headers.json");
            await File.WriteAllTextAsync(emptyDefaults, "{}", ct);
            start.Environment["AIHAPPEY_HEADERS_FILE"] = emptyDefaults;
        }
        if (service == ServiceKind.Agents)
        {
            start.Environment["AiConfig__AiEndpoint"] = ai!.AbsoluteUri.TrimEnd('/');
            start.Environment["McpConfig__McpBaseUrl"] = endpoint.AbsoluteUri.TrimEnd('/');
            start.Environment["LocalResponses__RootPath"] = Path.Combine(stateRoot, "responses");
        }
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"The managed {service} service could not be started.");
            job ??= new OwnedProcessJob();
            job.Assign(process);
            process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            children.Add(service, (process, endpoint));
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readiness.CancelAfter(TimeSpan.FromSeconds(60));
            while (true)
            {
                readiness.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException($"The managed {service} service failed during startup (code {process.ExitCode}). Check the bundled runtime configuration.");
                try
                {
                    // A deliberately unmapped route answers 404 immediately once routing is ready.
                    // /v1/models can perform slow provider-network discovery and is not a health probe.
                    using var response = await probe.GetAsync(new Uri(endpoint, "__desktop_readiness"), HttpCompletionOption.ResponseHeadersRead, readiness.Token);
                    if (response.StatusCode == HttpStatusCode.NotFound) return endpoint;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!readiness.IsCancellationRequested) { }
                await Task.Delay(200, readiness.Token);
            }
        }
        catch
        {
            children.Remove(service); Stop(process); throw;
        }
    }

    private static int AvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }

    private void StopOwned()
    {
        // Normal shutdown calls DisposeAsync after cancellation. ProcessExit is a last-resort cleanup.
        foreach (var child in children.Values.ToArray()) Stop(child.Process);
        children.Clear();
        job?.Dispose(); job = null;
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { StopOwned(); }
        finally { gate.Release(); }
    }
}
