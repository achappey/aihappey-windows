using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

/// <summary>Immutable enabled catalog and lazy readers for one inference turn.</summary>
public sealed class DesktopSkillTurn(IEnumerable<(DesktopSkill Skill, Func<CancellationToken, Task<DesktopSkillContent>> Load)> selected)
{
    public const string ActivateTool = "activate_skill";
    public const string ResourceTool = "read_skill_resource";
    private readonly Dictionary<string, (DesktopSkill Skill, Func<CancellationToken, Task<DesktopSkillContent>> Load)> skills = selected
        .DistinctBy(s => s.Skill.Id).ToDictionary(s => s.Skill.Id, StringComparer.Ordinal);
    public IReadOnlyList<DesktopSkill> Skills => skills.Values.Select(s => s.Skill).ToArray();
    public static bool Reserved(string name) => name is ActivateTool or ResourceTool;
    public static readonly JsonElement ActivationDefinition = Definition(ActivateTool, "Activate an enabled skill",
        "Loads the body instructions for an enabled agent skill. Use this when one of the available skills matches the current task. After activation, use read_skill_resource to load referenced bundled files by relative path.", false);
    public static readonly JsonElement ResourceDefinition = Definition(ResourceTool, "Read a bundled skill resource",
        "Reads a bundled file from an enabled skill by relative path. Use this after activate_skill when the skill instructions reference scripts, references, or assets. Paths are relative to the skill root.", true);
    private static JsonElement Definition(string name, string title, string description, bool resource)
    {
        var properties = new JsonObject { ["skill_id"] = new JsonObject { ["type"] = "string",
            ["description"] = "Exact enabled skill ID string shown in the system context; do not invent IDs or substitute the skill name." } };
        if (resource) properties["path"] = new JsonObject { ["type"] = "string", ["description"] = "Relative path within the skill directory, for example references/REFERENCE.md or scripts/run.py." };
        return JsonSerializer.SerializeToElement(new JsonObject { ["name"] = name, ["title"] = title, ["description"] = description,
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties,
                ["required"] = resource ? OpenAIChatConfig.Strings(["skill_id", "path"]) : OpenAIChatConfig.Strings(["skill_id"]) },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["destructiveHint"] = false, ["idempotentHint"] = true, ["openWorldHint"] = false } });
    }
    public JsonElement? Context => skills.Count == 0 ? null : JsonSerializer.SerializeToElement(new
    {
        availableSkills = new
        {
            activationTool = ActivateTool, resourceTool = ResourceTool,
            instructions = "The following skills provide specialized instructions for specific tasks. When a task matches a skill description, call activate_skill with the exact skill_id shown below to load its instructions. Do not use the skill name as skill_id unless it exactly matches the listed skill_id. After activation, use read_skill_resource with the same skill_id and a relative path when the instructions reference bundled files.",
            skillIdRequired = true,
            activationExamples = Skills.Select(s => $"- id={s.Id}; skill_id={s.Id}; name={s.Name}: Call activate_skill with skill_id \"{s.Id}\".").ToArray(),
            skills = Skills.Select(s => new { id = s.Id, skill_id = s.Id, exact_skill_id_to_activate = s.Id, name = s.Name,
                displayName = s.Label, description = s.Description, activation = $"Call activate_skill with skill_id \"{s.Id}\"." }).ToArray()
        }
    });
    public void Register(McpTurnSnapshot snapshot)
    {
        if (skills.Count == 0) return;
        snapshot.AddLocal(ActivationDefinition, (input, ct) => CallAsync(ActivateTool, input, ct));
        snapshot.AddLocal(ResourceDefinition, (input, ct) => CallAsync(ResourceTool, input, ct));
        if (Context is { } context) snapshot.AddContext(context);
    }
    public async Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var id = CatalogProjection.Text(input, "skill_id");
            if (id is null || !skills.TryGetValue(id, out var selected)) throw new InvalidDataException("The exact skill_id must refer to an enabled skill.");
            var loaded = await selected.Load(ct); ct.ThrowIfCancellationRequested(); var skill = loaded.Skill;
            if (name == ActivateTool)
            {
                var attributes = $"skill_id=\"{Escape(id)}\" name=\"{Escape(skill.Name)}\"";
                if (skill.Server is not null) attributes += $" server=\"{Escape(skill.Server)}\" uri=\"{Escape(skill.Uri!)}\"";
                var resources = loaded.ResourcePaths.Count == 0 ? "<skill_resources />" : "<skill_resources>\n"
                    + string.Join("\n", loaded.ResourcePaths.Select(p => "  <file>" + Escape(p) + "</file>")) + "\n</skill_resources>";
                return Result(new { skill = new { id, skill_id = id, name = skill.Name, description = skill.Description,
                    resourcePaths = loaded.ResourcePaths, server = skill.Server, uri = skill.Uri, instructions = loaded.Body } },
                    Text($"<skill_content {attributes}>\n{loaded.Body}\n\nUse read_skill_resource with this skill_id and a relative path from the resource list when you need bundled files referenced by the instructions.\n{resources}\n</skill_content>"));
            }
            if (name != ResourceTool) throw new InvalidDataException("Unsupported skill tool.");
            var path = SkillFiles.RelativePath(CatalogProjection.Text(input, "path") ?? "");
            var file = await loaded.Read(path, ct); ct.ThrowIfCancellationRequested();
            if (skill.Origin == "mcp")
            {
                var uri = skill.Uri![..^"SKILL.md".Length] + path;
                return Result(new { skillResource = new { skillName = skill.Name, server = skill.Server, uri, path } },
                    Text($"<skill_resource server=\"{Escape(skill.Server!)}\" uri=\"{Escape(uri)}\" path=\"{Escape(path)}\">\n{Encoding.UTF8.GetString(file.Data)}\n</skill_resource>"));
            }
            if (SkillFiles.IsText(file.MimeType))
            {
                var text = Encoding.UTF8.GetString(file.Data);
                return Result(new { skillResource = new { skillName = skill.Name, path, mimeType = file.MimeType, text } },
                    Text($"<skill_resource skill_id=\"{Escape(id)}\" name=\"{Escape(skill.Name)}\" path=\"{Escape(path)}\" mimeType=\"{file.MimeType}\">\n{text}\n</skill_resource>"));
            }
            return Result(new { skillResource = new { skillName = skill.Name, path, mimeType = file.MimeType, encoding = "base64" } },
                Text($"Binary skill resource {path} from skill {skill.Name}. mimeType={file.MimeType}."),
                new JsonObject { ["type"] = "resource", ["resource"] = new JsonObject {
                    ["uri"] = "skill://" + Uri.EscapeDataString(id) + "/" + string.Join("/", path.Split('/').Select(Uri.EscapeDataString)),
                    ["mimeType"] = file.MimeType, ["blob"] = Convert.ToBase64String(file.Data) } });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Do not return transport details, credentials or arbitrary exception text to the model.
            return JsonSerializer.SerializeToElement(new JsonObject { ["isError"] = true,
                ["content"] = new JsonArray(Text(DesktopResources.Get("SkillLoadFailed"))) });
        }
    }
    private static string Escape(string value) => SecurityElement.Escape(value) ?? "";
    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };
    private static JsonElement Result(object structured, params JsonObject[] content) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["isError"] = false, ["structuredContent"] = JsonSerializer.SerializeToNode(structured),
        ["content"] = new JsonArray(content.Select(c => (JsonNode)c).ToArray())
    });
}
