using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Desktop.Core;

/// <summary>Distributor configuration, deliberately separate from persisted user preferences.</summary>
public sealed class DesktopContextOptions
{
    public string? AppName { get; set; }
    public string? ChatbotInstructions { get; set; }
    public string[] McpCatalogUrls { get; set; } = [];

    public static DesktopContextOptions Load(string path)
    {
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<DesktopContextOptions>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("Invalid app context configuration.");
    }
}

/// <summary>Only safe identity fields cross the host boundary. Never tokens or token-cache data.</summary>
public sealed record DesktopUserContext(string? Username = null, string? Name = null, string? Id = null, string? TenantId = null);

public sealed record SystemContextInput(DesktopContextOptions Options, JsonObject SystemInformation,
    DesktopUserContext? User, string PreferredLanguage, bool DarkMode, DateTimeOffset Now)
{
    public McpTurnSnapshot Mcp { get; init; } = McpTurnSnapshot.Empty;
}

/// <summary>Hosts can replace composition, including adding future supported skills/MCP parts.</summary>
public interface ISystemContextComposer
{
    UIMessage Compose(SystemContextInput input);
}

public sealed class DesktopSystemContextComposer : ISystemContextComposer
{
    public UIMessage Compose(SystemContextInput input)
    {
        var parts = new List<UIMessagePart>();
        foreach (var block in input.Mcp.Context) parts.Add(Text(block.GetRawText()));
        if (!string.IsNullOrWhiteSpace(input.Options.ChatbotInstructions))
            parts.Add(Text(new JsonObject { ["chatBotInstructions"] = input.Options.ChatbotInstructions.Replace("\\n", "\n") }.ToJsonString()));
        var system = (JsonObject)input.SystemInformation.DeepClone();
        if (input.Options.AppName is { Length: > 0 } appName) system["appName"] = appName;
        parts.Add(Text(new JsonObject { ["systemInformation"] = system }.ToJsonString()));
        var user = new JsonObject { ["preferredLanguage"] = input.PreferredLanguage, ["darkMode"] = input.DarkMode };
        Add("username", input.User?.Username); Add("name", input.User?.Name);
        Add("id", input.User?.Id); Add("tenantId", input.User?.TenantId);
        parts.Add(Text(user.ToJsonString()));
        return new UIMessage
        {
            Id = Guid.NewGuid().ToString(), Role = Role.system, Parts = parts,
            Metadata = new() { ["timestamp"] = input.Now.ToUniversalTime().ToString("O"), ["author"] = "system" }
        };
        void Add(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) user[key] = value; }
    }

    private static UIMessagePart Text(string value) => PortableConversations.Part(new JsonObject { ["type"] = "text", ["text"] = value });
}

public static class DesktopSystemContext
{
    /// <summary>Non-visual native baseline. The shell adds current display/window measurements.</summary>
    public static JsonObject SystemInformation(DateTimeOffset now)
    {
        var timezone = TimeZoneInfo.Local.Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timezone, out var iana)) timezone = iana;
        return new()
        {
            ["platform"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["hardwareConcurrency"] = Environment.ProcessorCount,
            ["language"] = CultureInfo.CurrentUICulture.Name,
            ["timezone"] = timezone,
            ["utcNow"] = now.ToUniversalTime().ToString("O"),
            ["localNow"] = now.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)
        };
    }

    /// <summary>Replace all imported/stale system messages without modifying conversation history.</summary>
    public static List<UIMessage> RequestMessages(ServiceKind service, IEnumerable<UIMessage> history, UIMessage? context)
    {
        var messages = history.Where(message => message.Role != Role.system).ToList();
        if (service == ServiceKind.Ai)
        {
            if (context?.Role != Role.system) throw new InvalidOperationException("A system context message is required for model chat.");
            messages.Insert(0, context);
        }
        return messages;
    }
}
