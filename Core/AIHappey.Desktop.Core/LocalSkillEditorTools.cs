using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Desktop.Core;

public sealed class LocalSkillEditorTools(DesktopLocalSkillStore store, DesktopSkillStore downloadedStore, string partition,
    LocalToolFiles files, Func<bool> isCurrent, Func<CancellationToken, Task> changed)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private void Check(CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (!isCurrent()) throw new OperationCanceledException(ct); }
    private static object Descriptor(DesktopSkill s) => new { id = s.Id, skillId = s.Id, name = s.Name, description = s.Description,
        origin = s.Origin, version = s.Version, defaultVersion = s.Version, latestVersion = s.Version, isDownloaded = true };
    private static object Summary(DesktopSkill s, params string[] affectedFiles) => new { success = true, skillId = s.Id, name = s.Name,
        version = s.Version, defaultVersion = s.DefaultVersion ?? s.Version, latestVersion = s.LatestVersion ?? s.Version,
        affectedFiles, diagnostics = Array.Empty<object>(),
        restorationHint = "Use local_skill_editor_restore_version with skillId and a previous version to restore older behavior." };
    private static string String(JsonElement input, string key, string? fallback = null)
    {
        if (!input.TryGetProperty(key, out var value)) return fallback ?? throw new LocalToolException("Missing " + key + ".");
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new LocalToolException(key + " must be a string.");
    }
    private static void Patch(DesktopSkillDraft draft, JsonElement input)
    {
        foreach (var key in new[] { "description", "license", "compatibility", "metadata", "allowedTools" })
        {
            if (!input.TryGetProperty(key, out var value)) continue;
            var field = key == "allowedTools" ? "allowed-tools" : key;
            if (key != "metadata" && value.ValueKind != JsonValueKind.String) throw new LocalToolException(key + " must be a string.");
            if (key == "metadata" && (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.String)))
                throw new LocalToolException("metadata must contain only string values.");
            draft.Frontmatter[field] = JsonNode.Parse(value.GetRawText());
        }
        if (input.TryGetProperty("instructions", out _)) draft.Instructions = String(input, "instructions").Trim();
    }
    private static async Task<DesktopSkillDraft> DraftAsync(DesktopSkillContent content, CancellationToken ct)
    {
        var manifest = await content.Read("SKILL.md", ct);
        var parsed = SkillFiles.Markdown(Encoding.UTF8.GetString(manifest.Data));
        var draft = new DesktopSkillDraft { Frontmatter = parsed.Frontmatter, Instructions = parsed.Body };
        foreach (var path in content.ResourcePaths) draft.Files.Add(await content.Read(path, ct));
        return draft;
    }
    public Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(async () =>
    {
        await gate.WaitAsync(ct);
        try
        {
            Check(ct);
            try
            {
                var local = await store.ListAsync(partition, ct);
                if (name == "local_skill_editor_create")
                {
                    var draft = new DesktopSkillDraft { Name = String(input, "name"), Description = String(input, "description") }; Patch(draft, input);
                    Check(ct); var created = await store.SaveAsync(partition, draft, null, ct); await NotifyAsync(ct);
                    return DesktopLocalTools.Result(Summary(created, "SKILL.md"));
                }
                var allDownloaded = await downloadedStore.DownloadedAsync(partition, ct, allVersions: true); Check(ct);
                var downloaded = allDownloaded.GroupBy(s => s.Skill.Id).Select(g => g.OrderByDescending(s => long.TryParse(s.Skill.Version, out var v) ? v : 0).First()).ToArray();
                if (name == "local_skill_editor_list") return DesktopLocalTools.Result(new { skills = local.Select(Descriptor).Concat(
                    downloaded.Where(s => !local.Any(l => l.Id == s.Skill.Id || l.Name == s.Skill.Name)).Select(s => Descriptor(s.Skill))) });
                var id = DesktopLocalTools.Required(input, "skillId");
                var head = local.FirstOrDefault(s => s.Id == id || s.Name == id);
                var cached = head is null ? downloaded.FirstOrDefault(s => s.Skill.Id == id || s.Skill.Name == id) : null;
                if (head is null && cached is null) throw new LocalToolException("Skill was not found locally.");
                if (name == "local_skill_editor_inspect")
                {
                    var version = input.TryGetProperty("version", out _) ? String(input, "version") : null;
                    DesktopSkill selected; DesktopSkillDraft draft;
                    if (head is not null) (selected, draft) = await store.InspectAsync(partition, head, version, ct);
                    else
                    {
                        var selectedCache = version is null ? cached! : allDownloaded.FirstOrDefault(s => s.Skill.Id == cached!.Skill.Id && s.Skill.Version == version)
                            ?? throw new LocalToolException("Skill version was not found locally.");
                        selected = selectedCache.Skill with { LatestVersion = cached!.Skill.Version, DefaultVersion = cached.Skill.Version }; draft = await DraftAsync(selectedCache, ct);
                    }
                    var data = JsonSerializer.SerializeToNode(Summary(selected))!.AsObject();
                    data["files"] = new JsonArray(draft.Files.Select(f => f.Path).Append("SKILL.md").Order(StringComparer.Ordinal).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
                    data["frontmatter"] = draft.Frontmatter.DeepClone(); data["body"] = draft.Instructions;
                    data["warnings"] = new JsonArray(); data["errors"] = new JsonArray(); Check(ct);
                    return DesktopLocalTools.Result(data);
                }
                if (name == "local_skill_editor_delete_skill")
                {
                    Check(ct); if (head is not null) await store.DeleteAsync(partition, head.Id, ct);
                    var source = cached?.Skill ?? (head?.SourceId is { } sourceId ? downloaded.FirstOrDefault(s => s.Skill.Id == sourceId)?.Skill : null);
                    if (source is not null) await downloadedStore.DeleteDownloadedAsync(partition, source.Id, source.Version!, ct);
                    await NotifyAsync(ct); return DesktopLocalTools.Result(new { success = true, deletedSkillId = id });
                }
                if (name is not ("local_skill_editor_update_manifest" or "local_skill_editor_upsert_file" or "local_skill_editor_import_local_file"
                    or "local_skill_editor_delete_file" or "local_skill_editor_restore_version")) throw new LocalToolException("Unsupported skill editor tool.");
                Action<DesktopSkillDraft> edit; string[] affected; bool nextVersion = false; string? restore = null;
                if (name == "local_skill_editor_update_manifest") { edit = d => Patch(d, input); affected = ["SKILL.md"]; nextVersion = true; }
                else if (name == "local_skill_editor_restore_version")
                { restore = DesktopLocalTools.Required(input, "version"); edit = _ => { }; affected = ["SKILL.md"]; nextVersion = true; }
                else
                {
                    var path = DesktopSkillPackages.ResourcePath(DesktopLocalTools.Required(input, "relativePath")); affected = [path];
                    DesktopSkillFile? addition = null;
                    if (name == "local_skill_editor_upsert_file")
                    {
                        var mime = String(input, "mimeType", "text/plain");
                        if (mime.Length == 0 || mime.Any(char.IsControl)) throw new LocalToolException("Invalid mimeType.");
                        addition = new(path, Encoding.UTF8.GetBytes(String(input, "content")), mime);
                    }
                    if (name == "local_skill_editor_import_local_file")
                    {
                        var file = await files.ReadAsync(DesktopLocalTools.Required(input, "localFileName"), true, ct);
                        addition = new(path, file.Content.ToArray(), file.MediaType);
                    }
                    edit = draft => { draft.Files.RemoveAll(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)); if (addition is not null) draft.Files.Add(addition); };
                }
                Check(ct);
                // Validate all arguments before materializing an editable copy of a downloaded package.
                if (head is null)
                {
                    var draft = await DraftAsync(cached!, ct); var validation = draft.Clone(); edit(validation); DesktopSkillPackages.Validate(validation);
                    head = await store.ImportDownloadedAsync(partition, cached!.Skill, draft, ct);
                    foreach (var old in allDownloaded.Where(s => s.Skill.Id == cached.Skill.Id && s.Skill.Version != cached.Skill.Version))
                        await store.ImportDownloadedAsync(partition, old.Skill, await DraftAsync(old, ct), ct);
                }
                Check(ct); var saved = await store.EditAsync(partition, head.Id, edit, nextVersion, restore, ct);
                await NotifyAsync(ct); return DesktopLocalTools.Result(Summary(saved, affected));
            }
            catch (InvalidDataException e) { throw new LocalToolException(e.Message); }
        }
        finally { gate.Release(); }
    }, ct);
    private async Task NotifyAsync(CancellationToken ct)
    {
        Check(ct); try { await changed(ct); }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { }
    }
}
