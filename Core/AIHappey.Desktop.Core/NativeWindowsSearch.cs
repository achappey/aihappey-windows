using System.Data.OleDb;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AIHappey.Desktop.Core;

/// <summary>Native AQS translation and read-only Search.CollatorDSO access. No raw model SQL.</summary>
public sealed class NativeWindowsSearch(Action<string>? diagnostics = null) : ILocalWindowsSearch
{
    // Keep at most one provider call in flight, including a provider ignoring cancellation.
    // The worker owns the permit until its COM/OLE DB objects have actually been disposed.
    private static readonly SemaphoreSlim workerGate = new(1, 1);
    public async Task<LocalWindowsSearchResults> SearchAsync(LocalWindowsSearchRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        try
        {
            await workerGate.WaitAsync(token);
            var work = Task.Run(() =>
            {
                try { return Search(request, token, diagnostics); }
                finally { workerGate.Release(); }
            });
            // Observe a late fault even if the caller stopped waiting for the native worker.
            _ = work.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return await work.WaitAsync(token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new LocalToolException(DesktopResources.Get("LocalSearchTimeout")); }
    }

    public static string Restrictions(string? folder)
    {
        var restriction = "AND System.ItemUrl LIKE 'file:%' AND System.ItemType <> 'Directory'";
        if (folder is null) return restriction;
        var path = LocalFilePaths.Normalize(folder);
        // SCOPE is recursive. The URL is produced from a validated path; apostrophes are
        // still escaped as SQL literals. Never concatenate the user's AQS into SQL.
        var url = new Uri(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar).AbsoluteUri;
        return restriction + " AND SCOPE='" + url.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static LocalWindowsSearchResults Search(LocalWindowsSearchRequest request, CancellationToken ct, Action<string>? diagnostics)
    {
        ct.ThrowIfCancellationRequested();
        Log(diagnostics, "worker", 0);
        ISearchManager? manager = null; ISearchCatalogManager? catalog = null; ISearchQueryHelper? helper = null;
        try
        {
            try
            {
                Log(diagnostics, "activate-manager", 0);
                manager = (ISearchManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new("7D096C5F-AC08-4F1F-BEB7-5C22C517CE39"), true)!)!;
                Check(manager.GetCatalog("SystemIndex", out catalog), "get-catalog", diagnostics);
                Check(catalog.GetQueryHelper(out helper), "get-query-helper", diagnostics);
            }
            catch (COMException error)
            {
                Log(diagnostics, "initialize-com-failed", error.HResult);
                throw new LocalToolException(DesktopResources.Get("LocalSearchUnavailable"));
            }
            Check(helper.GetConnectionString(out var connectionString), "get-connection-string", diagnostics);
            Check(helper.SetQuerySyntax(1)); // SEARCH_ADVANCED_QUERY_SYNTAX from SearchAPI.h.
            Check(helper.SetQueryKeywordLocale(0x0409)); // Stable English kind:/ext: keywords.
            Check(helper.SetQueryContentLocale((uint)CultureInfo.CurrentCulture.LCID));
            Check(helper.SetQuerySelectColumns("System.ItemPathDisplay,System.ItemUrl,System.FileName,System.Size,System.DateModified,System.ItemType,System.Search.Rank"));
            Check(helper.SetQueryWhereRestrictions(Restrictions(request.Folder)));
            Check(helper.SetQuerySorting("System.Search.Rank DESC,System.DateModified DESC,System.ItemUrl ASC"));
            Check(helper.SetQueryMaxResults(request.Limit + 1));
            var hr = helper.GenerateSQLFromUserQuery(request.Query, out var sql);
            Log(diagnostics, "generate-query", hr);
            if (hr < 0) throw new LocalToolException(DesktopResources.Get("LocalSearchInvalidQuery"));
            ct.ThrowIfCancellationRequested();
            // Search.CollatorDSO rejects DBPROP_INIT_TIMEOUT (0x80040E21). Preserve the
            // native helper's connection string; caller/command timeouts bound waiting.
            using var connection = new OleDbConnection(connectionString);
            try { connection.Open(); Log(diagnostics, "open-provider", 0); }
            catch (OleDbException error)
            {
                Log(diagnostics, "open-provider-failed", error.HResult);
                foreach (OleDbError detail in error.Errors) Log(diagnostics, "provider-native-error", detail.NativeError);
                throw new LocalToolException(DesktopResources.Get("LocalSearchUnavailable"));
            }
            ct.ThrowIfCancellationRequested();
            using var command = new OleDbCommand(sql, connection) { CommandTimeout = 10 };
            using var cancel = ct.Register(() => { try { command.Cancel(); } catch (Exception e) when (e is not OutOfMemoryException) { } });
            var watch = Stopwatch.StartNew(); var stage = "execute-query";
            try
            {
                using var reader = command.ExecuteReader();
                Log(diagnostics, "execute-query elapsed-ms=" + watch.ElapsedMilliseconds, 0);
                var results = new List<LocalWindowsSearchItem>(); var rows = 0;
                stage = "read-row";
                while (reader is not null && reader.Read())
                {
                    ct.ThrowIfCancellationRequested(); rows++;
                    if (rows > request.Limit) break;
                    var url = Text(reader, 1);
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc || !string.IsNullOrEmpty(uri.Host)) continue;
                    try
                    {
                        var path = LocalFilePaths.Normalize(uri.LocalPath);
                        if (request.Folder is not null && !LocalFilePaths.IsWithin(path, request.Folder)) continue;
                        results.Add(new(path, Path.GetFileName(path), reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                            reader.IsDBNull(4) ? null : DateTime.SpecifyKind(Convert.ToDateTime(reader.GetValue(4), CultureInfo.InvariantCulture), DateTimeKind.Utc),
                            Text(reader, 5), reader.IsDBNull(6) ? null : Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture)));
                    }
                    catch (LocalToolException) { }
                }
                ct.ThrowIfCancellationRequested();
                return new(results, rows > request.Limit);
            }
            catch (OleDbException error)
            {
                Log(diagnostics, stage + "-failed elapsed-ms=" + watch.ElapsedMilliseconds, error.HResult);
                foreach (OleDbError detail in error.Errors) Log(diagnostics, "query-native-error", detail.NativeError);
                ct.ThrowIfCancellationRequested();
                throw new LocalToolException(DesktopResources.Get("LocalSearchQueryFailed"));
            }
        }
        finally
        {
            if (helper is not null) Marshal.FinalReleaseComObject(helper);
            if (catalog is not null) Marshal.FinalReleaseComObject(catalog);
            if (manager is not null) Marshal.FinalReleaseComObject(manager);
        }
    }
    private static string? Text(OleDbDataReader reader, int column) => reader.IsDBNull(column) ? null : Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture);
    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    private static void Check(int hr, string stage, Action<string>? diagnostics)
    {
        Log(diagnostics, stage, hr); Check(hr);
    }
    private static void Log(Action<string>? diagnostics, string stage, int hr)
    {
        // No queries, paths, SQL, connection strings, or arbitrary SDK messages.
        var message = $"WindowsSearch stage={stage} hr=0x{hr:X8} process={RuntimeInformation.ProcessArchitecture} os={RuntimeInformation.OSArchitecture} apartment={Thread.CurrentThread.GetApartmentState()}";
        Trace.WriteLine(message);
        diagnostics?.Invoke(message);
    }

    // Vtable ordering and LPWSTR marshalling verified against Microsoft's SearchAPI.h:
    // https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/SearchAPI.h
    // These private interfaces expose only the prefix required for querying. Unused
    // PROPVARIANT/sink arguments stay opaque; no catalog mutation method is ever called.
    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF69"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISearchManager
    {
        [PreserveSig] int GetIndexerVersionStr(out nint value);
        [PreserveSig] int GetIndexerVersion(out uint major, out uint minor);
        [PreserveSig] int GetParameter([MarshalAs(UnmanagedType.LPWStr)] string name, out nint value);
        [PreserveSig] int SetParameter([MarshalAs(UnmanagedType.LPWStr)] string name, nint value);
        [PreserveSig] int GetProxyName(out nint value);
        [PreserveSig] int GetBypassList(out nint value);
        [PreserveSig] int SetProxy(int access, int local, uint port, nint name, nint bypass);
        [PreserveSig] int GetCatalog([MarshalAs(UnmanagedType.LPWStr)] string name, out ISearchCatalogManager catalog);
    }
    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF50"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISearchCatalogManager
    {
        [PreserveSig] int GetName(out nint name);
        [PreserveSig] int GetParameter(nint name, out nint value);
        [PreserveSig] int SetParameter(nint name, nint value);
        [PreserveSig] int GetCatalogStatus(out int status, out int reason);
        [PreserveSig] int Reset();
        [PreserveSig] int Reindex();
        [PreserveSig] int ReindexMatchingURLs(nint pattern);
        [PreserveSig] int ReindexSearchRoot(nint root);
        [PreserveSig] int SetConnectTimeout(uint timeout);
        [PreserveSig] int GetConnectTimeout(out uint timeout);
        [PreserveSig] int SetDataTimeout(uint timeout);
        [PreserveSig] int GetDataTimeout(out uint timeout);
        [PreserveSig] int NumberOfItems(out int count);
        [PreserveSig] int NumberOfItemsToIndex(out int incremental, out int notifications, out int highPriority);
        [PreserveSig] int URLBeingIndexed(out nint url);
        [PreserveSig] int GetURLIndexingState(nint url, out uint state);
        [PreserveSig] int GetPersistentItemsChangedSink(out nint sink);
        [PreserveSig] int RegisterViewForNotification(nint view, nint sink, out uint cookie);
        [PreserveSig] int GetItemsChangedSink(nint site, in Guid iid, out nint sink, out Guid reset, out Guid checkpoint, out uint number);
        [PreserveSig] int UnregisterViewForNotification(uint cookie);
        [PreserveSig] int SetExtensionClusion(nint extension, int exclude);
        [PreserveSig] int EnumerateExcludedExtensions(out nint extensions);
        [PreserveSig] int GetQueryHelper(out ISearchQueryHelper helper);
    }
    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF63"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISearchQueryHelper
    {
        [PreserveSig] int GetConnectionString([MarshalAs(UnmanagedType.LPWStr)] out string connection);
        [PreserveSig] int SetQueryContentLocale(uint locale);
        [PreserveSig] int GetQueryContentLocale(out uint locale);
        [PreserveSig] int SetQueryKeywordLocale(uint locale);
        [PreserveSig] int GetQueryKeywordLocale(out uint locale);
        [PreserveSig] int SetQueryTermExpansion(int expansion);
        [PreserveSig] int GetQueryTermExpansion(out int expansion);
        [PreserveSig] int SetQuerySyntax(int syntax);
        [PreserveSig] int GetQuerySyntax(out int syntax);
        [PreserveSig] int SetQueryContentProperties(nint properties);
        [PreserveSig] int GetQueryContentProperties(out nint properties);
        [PreserveSig] int SetQuerySelectColumns([MarshalAs(UnmanagedType.LPWStr)] string columns);
        [PreserveSig] int GetQuerySelectColumns(out nint columns);
        [PreserveSig] int SetQueryWhereRestrictions([MarshalAs(UnmanagedType.LPWStr)] string restrictions);
        [PreserveSig] int GetQueryWhereRestrictions(out nint restrictions);
        [PreserveSig] int SetQuerySorting([MarshalAs(UnmanagedType.LPWStr)] string sorting);
        [PreserveSig] int GetQuerySorting(out nint sorting);
        [PreserveSig] int GenerateSQLFromUserQuery([MarshalAs(UnmanagedType.LPWStr)] string query, [MarshalAs(UnmanagedType.LPWStr)] out string sql);
        [PreserveSig] int WriteProperties(uint item, uint count, nint columns, nint values, nint modified);
        [PreserveSig] int SetQueryMaxResults(int count);
    }
}
