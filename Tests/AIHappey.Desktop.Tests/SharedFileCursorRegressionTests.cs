using System.Text.Json;
using AIHappey.Desktop.Core;

internal static class SharedFileCursorRegressionTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Data(JsonElement value) => value.GetProperty("structuredContent");
    private static bool Error(JsonElement value) => value.GetProperty("isError").GetBoolean();

    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        var folder = Path.Combine(root, "cursor-documents"); Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "first.txt"); await File.WriteAllTextAsync(file, "first");
        await File.WriteAllTextAsync(Path.Combine(folder, "second.txt"), "second");
        var store = new SharedFileStore(Path.Combine(root, "cursor-references"));
        var partition = HistoryStore.Partition("cursor-profile", "cursor-account");
        var sharedFolder = await store.AddAsync(partition, folder, true);
        await store.AddAsync(partition, file, false);
        using var tools = new LocalSharedFileTools(store, partition, new());

        var initial = Data(await tools.CallAsync(LocalSharedFileTools.ListTool, Args(new { limit = 1 }), default));
        foreach (var cursor in new[] { "", " ", "\t\r\n" })
        {
            var result = await tools.CallAsync(LocalSharedFileTools.ListTool, Args(new { cursor, limit = 1 }), default);
            check(!Error(result) && Data(result).GetRawText() == initial.GetRawText(),
                "shared files: blank cursor starts the same first reference page as omitted cursor");
        }
        var continuation = Data(await tools.CallAsync(LocalSharedFileTools.ListTool,
            Args(new { cursor = initial.GetProperty("nextCursor").GetString(), limit = 1 }), default));
        check(continuation.GetProperty("data").GetArrayLength() == 1 && !continuation.GetProperty("hasMore").GetBoolean()
            && continuation.GetProperty("data")[0].GetProperty("id").GetString() != initial.GetProperty("data")[0].GetProperty("id").GetString(),
            "shared files: non-empty reference cursor still continues instead of restarting");
        check(Error(await tools.CallAsync(LocalSharedFileTools.ListTool, Args(new { cursor = "invalid" }), default)),
            "shared files: unknown non-empty reference cursor remains rejected");
        check(Error(await tools.CallAsync(LocalSharedFileTools.ListTool, Args(new { cursor = 1 }), default)),
            "shared files: non-string cursor remains rejected");

        var firstDirectory = Data(await tools.CallAsync(LocalSharedFileTools.DirectoryTool, Args(new { id = sharedFolder.Id, limit = 1 }), default));
        foreach (var cursor in new[] { "", " ", "\t\r\n" })
        {
            var result = await tools.CallAsync(LocalSharedFileTools.DirectoryTool, Args(new { id = sharedFolder.Id, cursor, limit = 1 }), default);
            check(!Error(result) && Data(result).GetProperty("data").GetRawText() == firstDirectory.GetProperty("data").GetRawText()
                && Data(result).GetProperty("hasMore").GetBoolean(),
                "shared files: blank cursor starts the first directory page and returns a real continuation token");
        }
        var nextCursor = firstDirectory.GetProperty("nextCursor").GetString();
        var nextDirectory = Data(await tools.CallAsync(LocalSharedFileTools.DirectoryTool, Args(new { id = sharedFolder.Id, cursor = nextCursor, limit = 1 }), default));
        check(nextDirectory.GetProperty("data").GetArrayLength() == 1 && !nextDirectory.GetProperty("hasMore").GetBoolean()
            && nextDirectory.GetProperty("data")[0].GetProperty("relativePath").GetString() != firstDirectory.GetProperty("data")[0].GetProperty("relativePath").GetString(),
            "shared files: non-empty directory cursor continues without duplicating the first entry");
        check(Error(await tools.CallAsync(LocalSharedFileTools.DirectoryTool, Args(new { id = sharedFolder.Id, cursor = nextCursor }), default)),
            "shared files: directory continuation tokens remain single-use");
        check(Error(await tools.CallAsync(LocalSharedFileTools.DirectoryTool, Args(new { id = sharedFolder.Id, cursor = "invalid" }), default)),
            "shared files: unknown non-empty directory cursor remains rejected");

        using var empty = new LocalSharedFileTools(store, HistoryStore.Partition("empty-cursor-account"), new());
        var emptyResult = await empty.CallAsync(LocalSharedFileTools.ListTool, Args(new { cursor = "", limit = 100 }), default);
        check(!Error(emptyResult) && Data(emptyResult).GetProperty("data").GetArrayLength() == 0 && !Data(emptyResult).GetProperty("hasMore").GetBoolean(),
            "shared files: empty reference store with empty cursor is a successful empty first page");
    }
}
