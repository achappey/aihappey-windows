using System.Diagnostics;
using System.Text;

namespace AIHappey.Desktop.Core;

public sealed record PowerShellCommandResult(string Stdout, string Stderr, int? ExitCode, bool TimedOut, bool Truncated);

/// <summary>One-shot local-user execution, not a security sandbox. Both shell adapters use this engine.</summary>
public sealed class PowerShellExecutor(Func<bool>? isCurrent = null)
{
    public const int DefaultTimeoutMs = 60_000, MaximumTimeoutMs = 120_000;
    public const int DefaultOutputLength = 16_384, MaximumOutputLength = 65_536;
    public static string Executable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    public async Task<IReadOnlyList<PowerShellCommandResult>> ExecuteAsync(IReadOnlyList<string> commands,
        int? timeoutMs, int? maxOutputLength, string? workingDirectory, CancellationToken ct)
    {
        Check(ct);
        if (commands.Count is < 1 or > 32 || commands.Any(string.IsNullOrWhiteSpace) || commands.Sum(c => (long)c.Length) > 262_144)
            throw new LocalToolException("Supply 1–32 non-empty commands, at most 262144 characters in total.");
        if (timeoutMs is <= 0 || maxOutputLength is <= 0) throw new LocalToolException("Execution limits must be positive integers.");
        var directory = workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new LocalToolException("Working directory must be an existing absolute local directory.");
        if (directory.StartsWith(@"\\", StringComparison.Ordinal)) throw new LocalToolException("Network working directories are not supported.");
        var budget = new OutputBudget(Math.Min(maxOutputLength ?? DefaultOutputLength, MaximumOutputLength));
        using var timeout = new CancellationTokenSource(Math.Min(timeoutMs ?? DefaultTimeoutMs, MaximumTimeoutMs));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var results = new List<PowerShellCommandResult>(commands.Count);
        foreach (var command in commands)
        {
            Check(ct);
            results.Add(timeout.IsCancellationRequested ? new("", "", null, true, false)
                : await RunAsync(command, directory, budget, lifetime.Token, ct));
        }
        Check(ct);
        return results;
    }

    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isCurrent?.Invoke() == false) throw new OperationCanceledException(ct);
    }

    private async Task<PowerShellCommandResult> RunAsync(string command, string directory, OutputBudget budget,
        CancellationToken lifetime, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            "[Console]::InputEncoding = [Text.UTF8Encoding]::new($false); [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); $ErrorActionPreference = 'Stop'; try { & ([ScriptBlock]::Create([Console]::In.ReadToEnd())); $ok = $?; if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; if (-not $ok) { exit 1 } } catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }" })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var job = new PowerShellProcessJob();
        using var drain = new CancellationTokenSource();
        var stdout = new Capture(budget); var stderr = new Capture(budget);
        Task readers = Task.CompletedTask;
        var timedOut = false;
        try
        {
            Check(ct); lifetime.ThrowIfCancellationRequested();
            if (!process.Start()) throw new LocalToolException("PowerShell could not be started.");
            // The bootstrap blocks on stdin. Assign the kill-on-close job before supplying any model code.
            job.Assign(process);
            readers = Task.WhenAll(stdout.ReadAsync(process.StandardOutput, drain.Token), stderr.ReadAsync(process.StandardError, drain.Token));
            await process.StandardInput.WriteAsync(command.AsMemory(), lifetime);
            process.StandardInput.Close();
            await process.WaitForExitAsync(lifetime);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && lifetime.IsCancellationRequested)
        { timedOut = true; }
        catch (System.ComponentModel.Win32Exception)
        { throw new LocalToolException("PowerShell could not be started or its process lifetime could not be controlled."); }
        finally
        {
            job.Dispose();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await readers.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { drain.Cancel(); await readers; }
        }
        Check(ct);
        return new(stdout.Text, stderr.Text, timedOut ? null : process.ExitCode, timedOut, stdout.Truncated || stderr.Truncated);
    }

    private sealed class OutputBudget(int remaining)
    {
        public int Take(int count) { lock (this) { var size = Math.Min(count, remaining); remaining -= size; return size; } }
    }
    private sealed class Capture(OutputBudget budget)
    {
        private readonly StringBuilder text = new();
        public string Text => text.ToString();
        public bool Truncated { get; private set; }
        public async Task ReadAsync(StreamReader reader, CancellationToken ct)
        {
            var buffer = new char[4096];
            try
            {
                while (await reader.ReadAsync(buffer.AsMemory(), ct) is var count && count > 0)
                {
                    var size = budget.Take(count); text.Append(buffer, 0, size); Truncated |= size != count;
                    // Continue draining after the cap; never block the child on a full pipe.
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }
}
