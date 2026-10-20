using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIHappey.Desktop.Core;

public delegate Task LocalImageGenerate(ImagePreferences preferences, ChatTarget model, string prompt, IReadOnlyList<ComposerAttachment> inputs,
    ComposerAttachment? mask, Func<ImageGenerationBatch, Task> completed, CancellationToken ct);

public sealed class LocalImageTools(ImageLibraryStore library, LocalToolFiles files, ImagePreferences defaults,
    LocalImageGenerate generate, Func<JsonObject, string, CancellationToken, Task<JsonElement>> ask,
    Func<bool> isCurrent, Func<CancellationToken, Task>? changed = null)
{
    private sealed record Reference(string source, string id, int? imageIndex = null);
    private void Check(CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (!isCurrent()) throw new OperationCanceledException(ct); }
    private static int Integer(JsonElement input, string key, int fallback, int min, int max)
    {
        if (!input.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var n) || n < min || n > max)
            throw new LocalToolException(key + " is outside the supported integer range.");
        return n;
    }
    private static string Required(JsonElement input, string key)
    {
        var text = DesktopLocalTools.Required(input, key).Trim();
        return text.Length > 0 ? text : throw new LocalToolException("Missing " + key + ".");
    }
    private static Reference Parse(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new LocalToolException("Expected a saved image reference.");
        var source = Required(input, "source"); var id = Required(input, "id");
        if (source is not ("files" or "images")) throw new LocalToolException("Image source must be 'images' or 'files'.");
        var index = Integer(input, "imageIndex", 0, 0, int.MaxValue);
        if (source == "files" && index != 0) throw new LocalToolException("Local file imageIndex must be null or 0.");
        return new(source, id, source == "images" ? index : null);
    }
    private static Reference[] References(JsonElement input, string key, bool required)
    {
        if ((!input.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) && !required) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 16 || required && value.GetArrayLength() == 0)
            throw new LocalToolException(key + " must contain " + (required ? "1–16" : "0–16") + " saved image references.");
        return value.EnumerateArray().Select(Parse).ToArray();
    }
    private async Task<ComposerAttachment> ResolveAsync(Reference reference, CancellationToken ct)
    {
        Check(ct); ComposerAttachment image;
        if (reference.source == "files") image = await files.ReadAsync(reference.id, false, ct);
        else
        {
            var images = await library.ListAsync(ct);
            var item = images.FirstOrDefault(i => i.Generation.Id == reference.id && i.ImageIndex == reference.imageIndex)
                ?? throw new LocalToolException("Saved image or imageIndex was not found.");
            image = await files.ReadPathAsync(item.Path, item.Output.File, ct);
            image = ComposerAttachment.Local(image.Name, item.Output.MediaType, image.Content);
        }
        if (!image.MediaType.StartsWith("image/", StringComparison.Ordinal)) throw new LocalToolException("Local file is not an image.");
        Check(ct); return image;
    }
    private static JsonObject Image(ComposerAttachment image) => new() { ["type"] = "image", ["mimeType"] = image.MediaType,
        ["data"] = Convert.ToBase64String(image.Content.Span) };
    private static JsonElement Content(string text, object metadata, IEnumerable<ComposerAttachment> images, bool error = false)
    {
        var result = JsonNode.Parse(DesktopLocalTools.Result(metadata, new[] { DesktopLocalTools.Text(text) }.Concat(images.Select(Image)).ToArray()).GetRawText())!.AsObject();
        result["isError"] = error; return JsonSerializer.SerializeToElement(result);
    }
    public Task<JsonElement> CallAsync(string name, JsonElement input, CancellationToken ct) => DesktopLocalTools.SafeAsync(async () =>
    {
        Check(ct);
        if (name == "local_mask_create")
        {
            var filename = Required(input, "name"); var bytes = LocalImageMask.Create(input, ct);
            Check(ct); var saved = await files.SaveAsync(filename, bytes, ct);
            return DesktopLocalTools.Result(new { image = new Reference("files", saved.Id), name = filename },
                DesktopLocalTools.Text("Mask created: " + filename + ". File ID: " + saved.Id));
        }
        if (name == "local_images_list")
        {
            var offset = Integer(input, "offset", 0, 0, int.MaxValue); var limit = Integer(input, "limit", 50, 1, 500);
            var items = (await library.ListAsync(ct)).Select(i => new { source = "images", id = i.Generation.Id,
                imageIndex = i.ImageIndex, model = i.Model, createdAt = i.Generation.CreatedAt }).ToArray();
            Check(ct); return DesktopLocalTools.Result(new { total = items.Length, offset, items = items.Skip(offset).Take(limit) });
        }
        if (name == "local_images_read")
        {
            if (!input.TryGetProperty("image", out var value)) throw new LocalToolException("Missing image.");
            var reference = Parse(value); var image = await ResolveAsync(reference, ct);
            return Content("Done. Read saved image.", new { image = reference }, new[] { image });
        }
        if (name is not ("local_images_generate" or "local_images_ask")) throw new LocalToolException("Unsupported image tool.");
        var model = Required(input, "model");
        if (!Regex.IsMatch(model, @"^[^/\s]+/.+")) throw new LocalToolException("Use an exact provider-qualified model ID from local_ai_models_search.");
        if (name == "local_images_ask")
        {
            var question = Required(input, "question"); var refs = References(input, "images", true);
            var content = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = question });
            foreach (var reference in refs)
            {
                var image = await ResolveAsync(reference, ct);
                content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = "data:" + image.MediaType + ";base64," + Convert.ToBase64String(image.Content.Span) });
            }
            var body = new JsonObject { ["model"] = model, ["background"] = false, ["store"] = false, ["stream"] = false,
                ["tools"] = new JsonArray(), ["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }) };
            Check(ct); var response = await ask(body, model, ct); Check(ct);
            if (response.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null
                || CatalogProjection.Text(response, "status") is { } status && status != "completed") throw new LocalToolException("Image question did not complete.");
            var answer = response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array
                ? string.Join("\n", output.EnumerateArray().Where(o => CatalogProjection.Text(o, "type") == "message" && CatalogProjection.Text(o, "role") == "assistant")
                    .Where(o => o.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array).SelectMany(o => o.GetProperty("content").EnumerateArray())
                    .Where(p => CatalogProjection.Text(p, "type") == "output_text").Select(p => CatalogProjection.Text(p, "text")).Where(t => t is not null)).Trim() : "";
            if (answer.Length == 0) throw new LocalToolException("Image question returned no text answer.");
            return DesktopLocalTools.Result(null, DesktopLocalTools.Text(answer));
        }
        var prompt = Required(input, "prompt"); var sourceRefs = References(input, "inputImages", false);
        Reference? maskRef = input.TryGetProperty("mask", out var maskValue) && maskValue.ValueKind != JsonValueKind.Null ? Parse(maskValue) : null;
        if (maskRef is not null && sourceRefs.Length == 0) throw new LocalToolException("A mask requires at least one input image. For NEW images use mask: null and inputImages: [].");
        var preferences = defaults.Clone(); preferences.MaskPath = null;
        var n = Integer(input, "n", preferences.N, 1, 100);
        foreach (var key in new[] { "size", "aspectRatio" })
        {
            if (!input.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.String || !Regex.IsMatch(value.GetString()!, key == "size" ? @"^[1-9]\d*x[1-9]\d*$" : @"^[1-9]\d*:[1-9]\d*$"))
                throw new LocalToolException("Invalid " + key + ".");
            if (key == "size") preferences.Size = value.GetString(); else preferences.AspectRatio = value.GetString();
        }
        preferences.Seed = input.TryGetProperty("seed", out var seed) && seed.ValueKind != JsonValueKind.Null ? Integer(input, "seed", 0, int.MinValue, int.MaxValue) : preferences.Seed;
        var sources = new List<ComposerAttachment>(); foreach (var reference in sourceRefs) sources.Add(await ResolveAsync(reference, ct));
        var mask = maskRef is null ? null : await ResolveAsync(maskRef, ct);
        if (mask is not null && mask.MediaType != "image/png") throw new LocalToolException("Mask must be a PNG image.");
        var references = new List<Reference>(); var generated = new List<ComposerAttachment>(); var fallback = false;
        // The native API client supports 20 images per response; split larger browser-compatible requests safely.
        var batchLimit = Math.Min(20, preferences.MaxImagesPerCall ?? 20);
        if (batchLimit < 1) throw new LocalToolException("maxImagesPerCall must be a positive integer.");
        var generationFailed = false;
        try
        {
        for (var remaining = n; remaining > 0; remaining -= batchLimit)
        {
            Check(ct); preferences.N = Math.Min(batchLimit, remaining);
            await generate(preferences, new ChatTarget(model, model) { ModelType = "image" }, prompt, sources, mask, async batch =>
            {
                var outputs = batch.Images.Select((data, index) => { var decoded = ImageAttachments.Decode(data);
                    return ComposerAttachment.Local("generated_" + Guid.NewGuid().ToString("N") + "_" + index + ImageAttachments.Extension(decoded.MediaType), decoded.MediaType, decoded.Bytes); }).ToArray();
                generated.AddRange(outputs);
                try
                {
                    var saved = await library.SaveAsync(batch, sources, mask);
                    references.AddRange(saved.Outputs.Select((_, index) => new Reference("images", saved.Id, index)));
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    fallback = true;
                    foreach (var image in outputs)
                    {
                        try { var saved = await files.SaveAsync(image.Name, image.Content.ToArray(), CancellationToken.None); references.Add(new("files", saved.Id)); }
                        catch (Exception failure) when (failure is not OutOfMemoryException) { break; }
                    }
                }
            }, ct);
        }
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException && generated.Count > 0)
        { generationFailed = true; }
        try { if (changed is not null && isCurrent()) await changed(ct); }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException) { }
        var complete = references.Count == generated.Count && !generationFailed;
        var text = generationFailed ? "Some images were generated before a later request failed. Completed outputs are retained; do not automatically regenerate."
            : !complete ? "Images generated, but storage partially failed. Unsaved images remain in this result; do not automatically regenerate."
            : fallback ? "Done. Generated images; Images library save failed, saved to Local files instead." : "Done. Generated images and saved to the Images library.";
        Check(ct);
        return Content(text, new { images = references, model, count = generated.Count, storage = fallback ? "files" : null, savedCount = references.Count }, generated, !complete);
    }, ct);
}
