using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace AIHappey.Desktop.Core;

/// <summary>On-demand Shell Apps-folder discovery and ShellExecuteEx(open). No command interpreter or arguments.</summary>
internal sealed class NativeWindowsAppLauncher(Func<bool>? isCurrent = null)
{
    private void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isCurrent?.Invoke() == false) throw new OperationCanceledException(ct);
    }

    public Task<LocalInstalledAppList> ListAsync(string query, int limit, CancellationToken ct) => OnStaAsync(() =>
    {
        var apps = new List<LocalInstalledApp>(); var more = false;
        VisitApps((id, name, _) =>
        {
            if (!name.Contains(query, StringComparison.OrdinalIgnoreCase) && !id.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
            if (apps.Count == limit) { more = true; return false; }
            apps.Add(new(id, name)); return true;
        }, ct);
        return new LocalInstalledAppList(apps, more);
    }, ct);

    public Task LaunchAsync(LocalAppLaunchRequest request, CancellationToken ct) => OnStaAsync(() =>
    {
        request = LocalAppLauncherTools.Validate(request); Check(ct);
        nint pidl = 0;
        try
        {
            var file = request.Target;
            if (request.Kind == "app")
            {
                var found = false;
                VisitApps((id, _, item) =>
                {
                    if (!id.Equals(request.Target, StringComparison.Ordinal)) return true;
                    // Classic Apps-folder entries can contain shortcut arguments. Do not execute such indirect commands.
                    var arguments = Property(item, "System.Link.Arguments");
                    if (!string.IsNullOrWhiteSpace(arguments)) throw new LocalToolException(DesktopResources.Get("LocalAppInvalid"));
                    var executable = Property(item, "System.Link.TargetParsingPath");
                    if (!string.IsNullOrEmpty(executable)) RequireExecutable(LocalAppLauncherTools.Validate(new("executable", executable)).Target);
                    found = true; return false;
                }, ct);
                if (!found) throw new LocalToolException(DesktopResources.Get("LocalAppUnavailable"));
                file = @"shell:AppsFolder\" + request.Target;
                Marshal.ThrowExceptionForHR(SHParseDisplayName(file, 0, out pidl, 0, out _));
            }
            else if (request.Kind == "executable") RequireExecutable(file);
            else if (request.Kind == "document")
            {
                // Resolve the registered handler only to reject required-elevation/script hosts; Windows still opens the document normally.
                var executable = new StringBuilder(32768); uint length = (uint)executable.Capacity;
                if (AssocQueryString(0x40 /* VERIFY */, 2 /* EXECUTABLE */, Path.GetExtension(file), "open", executable, ref length) != 0)
                    throw new LocalToolException(DesktopResources.Get("LocalAppUnavailable"));
                var handler = executable.ToString();
                if (Path.GetFileNameWithoutExtension(handler).ToLowerInvariant() is "cmd" or "powershell" or "pwsh" or "wscript" or "cscript" or "mshta" or "rundll32" or "regsvr32")
                    throw new LocalToolException(DesktopResources.Get("LocalAppInvalid"));
                RequireExecutable(handler);
            }
            Check(ct);
            var execute = new ShellExecuteInfo
            {
                Size = Marshal.SizeOf<ShellExecuteInfo>(), Mask = 0x100u /* NOASYNC */ | 0x400u /* FLAG_NO_UI */ | (pidl != 0 ? 0xCu /* IDLIST */ : 0u),
                Verb = "open", File = pidl == 0 ? file : null, IdList = pidl, Show = 1
                // Parameters and Directory deliberately remain null. Never use the runas verb.
            };
            if (!ShellExecuteEx(ref execute)) throw new LocalToolException(DesktopResources.Get("LocalAppUnavailable"));
            return true;
        }
        finally { if (pidl != 0) Marshal.FreeCoTaskMem(pidl); }
    }, ct);

    private static string? Property(dynamic item, string name)
    {
        try { return item.ExtendedProperty(name) as string; }
        catch (COMException) { return null; }
    }

    private void VisitApps(Func<string, string, object, bool> visit, CancellationToken ct)
    {
        object? shell = null, folder = null, items = null;
        try
        {
            Check(ct);
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application") ?? throw new LocalToolException(DesktopResources.Get("LocalAppUnavailable")));
            folder = ((dynamic)shell!).NameSpace("shell:AppsFolder");
            if (folder is null) throw new LocalToolException(DesktopResources.Get("LocalAppUnavailable"));
            items = ((dynamic)folder).Items();
            var count = (int)((dynamic)items).Count;
            for (var i = 0; i < count; i++)
            {
                Check(ct); object? item = null;
                try
                {
                    item = ((dynamic)items).Item(i);
                    var id = Property(item!, "System.AppUserModel.ID");
                    if (string.IsNullOrWhiteSpace(id)) id = (string)((dynamic)item!).Path;
                    var name = (string)((dynamic)item!).Name;
                    if (string.IsNullOrWhiteSpace(id) || id.Length > 512 || id.Any(char.IsControl) || id.IndexOfAny(['"', '/', '\\', ':', ';', '|', '<', '>']) >= 0) continue;
                    if (!visit(id, name.Length > 512 ? name[..512] : name, item!)) break;
                }
                finally { Release(item); }
            }
            Check(ct);
        }
        finally { Release(items); Release(folder); Release(shell); }
    }

    private Task<T> OnStaAsync<T>(Func<T> call, CancellationToken ct)
    {
        Check(ct);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = completion.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        var thread = new Thread(() =>
        {
            try { Check(ct); var result = call(); Check(ct); completion.TrySetResult(result); }
            catch (OperationCanceledException) { completion.TrySetCanceled(ct); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "AIHappey app launcher" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(ct);
    }

    private static void RequireExecutable(string path)
    {
        if (!GetBinaryType(path, out var type) || type is not (0 or 6)) throw new LocalToolException(DesktopResources.Get("LocalAppInvalid"));
        // ShellExecute(open) can otherwise trigger UAC from an EXE manifest even without runas.
        // Load resources as data, never executing DLL entry points or mapping dependent modules.
        var module = LoadLibraryEx(path, 0, 0x2 | 0x20 /* AS_DATAFILE | AS_IMAGE_RESOURCE */);
        if (module == 0) throw new LocalToolException(DesktopResources.Get("LocalAppUnavailable"));
        try
        {
            var resource = FindResource(module, (nint)1, (nint)24 /* RT_MANIFEST */);
            if (resource == 0)
            {
                // No embedded manifest allows Windows installer/elevation heuristics. Fail closed rather than risk a UAC prompt.
                throw new LocalToolException(DesktopResources.Get("LocalAppElevationUnsupported"));
            }
            var size = SizeofResource(module, resource);
            if (size is 0 or > 1_048_576) throw new LocalToolException(DesktopResources.Get("LocalAppElevationUnsupported"));
            var data = LockResource(LoadResource(module, resource));
            if (data == 0) throw new LocalToolException(DesktopResources.Get("LocalAppUnavailable"));
            var bytes = new byte[size]; Marshal.Copy(data, bytes, 0, bytes.Length);
            using var stream = new MemoryStream(bytes);
            var manifest = XDocument.Load(stream);
            var level = manifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "requestedExecutionLevel")?.Attribute("level")?.Value;
            if (level != "asInvoker" || manifest.Descendants().Any(e => e.Name.LocalName == "autoElevate" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)))
                throw new LocalToolException(DesktopResources.Get("LocalAppElevationUnsupported"));
        }
        finally { FreeLibrary(module); }
    }

    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size; public uint Mask; public nint Window;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb, File, Parameters, Directory;
        public int Show; public nint Instance, IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public nint ClassKey; public uint HotKey; public nint Icon, Process;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellExecuteEx(ref ShellExecuteInfo execute);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, nint context, out nint pidl, uint attributes, out uint actualAttributes);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int AssocQueryString(uint flags, uint kind, string association, string extra, StringBuilder output, ref uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetBinaryType(string file, out uint type);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint FindResource(nint module, nint name, nint type);
    [DllImport("kernel32.dll")] private static extern uint SizeofResource(nint module, nint resource);
    [DllImport("kernel32.dll")] private static extern nint LoadResource(nint module, nint resource);
    [DllImport("kernel32.dll")] private static extern nint LockResource(nint resource);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(nint module);
}
