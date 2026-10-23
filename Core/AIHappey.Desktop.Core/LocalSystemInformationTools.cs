using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace AIHappey.Desktop.Core;

/// <summary>On-demand .NET/Win32 snapshots only. No WMI, performance-counter subscription, command lines, or retained samples.</summary>
public sealed class LocalSystemInformationTools(Func<bool>? isCurrent = null)
{
    public const string SummaryTool = "local_system_get_info";
    public const string ProcessesTool = "local_system_list_processes";

    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isCurrent?.Invoke() == false) throw new OperationCanceledException(ct);
    }

    public Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(() => Task.Run(() =>
    {
        Check(ct);
        object result;
        if (name == SummaryTool)
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref memory)) throw new LocalToolException(DesktopResources.Get("LocalSystemUnavailable"));
            using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var version = new OsVersion { Size = Marshal.SizeOf<OsVersion>(), ServicePack = "" };
            if (RtlGetVersion(ref version) != 0) throw new LocalToolException(DesktopResources.Get("LocalSystemUnavailable"));
            result = new
            {
                cpu = new { name = (cpu?.GetValue("ProcessorNameString") as string)?.Trim(), architecture = RuntimeInformation.OSArchitecture.ToString(), logicalProcessors = Environment.ProcessorCount },
                memory = new { totalBytes = memory.TotalPhysical, availableBytes = memory.AvailablePhysical, usedBytes = memory.TotalPhysical - memory.AvailablePhysical, loadPercent = memory.Load },
                windows = new { name = version.ProductType == 1 ? (version.Build >= 22000 ? "Windows 11" : "Windows 10") : "Windows Server", major = version.Major, minor = version.Minor, build = version.Build, architecture = RuntimeInformation.OSArchitecture.ToString() }
            };
        }
        else if (name == ProcessesTool)
        {
            var limit = DesktopLocalTools.Integer(input, "limit", 50, 200);
            var query = "";
            if (input.TryGetProperty("query", out var value))
            {
                if (value.ValueKind != JsonValueKind.String || (query = value.GetString()!).Length > 256 || query.Any(char.IsControl))
                    throw new LocalToolException(DesktopResources.Get("LocalSystemInvalid"));
            }
            var processes = Process.GetProcesses();
            var matches = new List<ProcessSnapshot>(); var partial = false; var more = false;
            try
            {
                foreach (var process in processes.OrderBy(p => p.Id))
                {
                    Check(ct);
                    try
                    {
                        var processName = process.ProcessName;
                        if (!processName.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                        if (matches.Count == limit) { more = true; break; }
                        long? workingSet = null; double? cpuTime = null;
                        try { workingSet = process.WorkingSet64; cpuTime = process.TotalProcessorTime.TotalMilliseconds; }
                        catch (Exception error) when (UnavailableProcess(error)) { partial = true; }
                        matches.Add(new(process.Id, processName, workingSet, cpuTime));
                    }
                    catch (Exception error) when (UnavailableProcess(error)) { partial = true; }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            result = new { processes = matches, count = matches.Count, hasMore = more, partial };
        }
        else throw new LocalToolException("Unsupported system information tool.");
        Check(ct);
        // Text-only content avoids duplicating filtered text in structuredContent.
        return DesktopLocalTools.Result(null, DesktopLocalTools.Text(JsonSerializer.Serialize(result, JsonSerializerOptions.Web)));
    }, ct), ct);

    private static bool UnavailableProcess(Exception error) => error is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException;
    private sealed record ProcessSnapshot(int ProcessId, string Name, long? WorkingSetBytes, double? TotalCpuTimeMilliseconds);
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersion
    {
        public int Size; public uint Major, Minor, Build, Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
        public ushort ServicePackMajor, ServicePackMinor, SuiteMask;
        public byte ProductType, Reserved;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)] private static extern int RtlGetVersion(ref OsVersion version);
}
