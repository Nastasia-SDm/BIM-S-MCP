using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

internal static class ModelParametersExport
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
    };
    private static readonly string[] Fields =
        ["ElementId", "Category", "FamilyName", "TypeName", "TypeId", "InstanceParameters", "TypeParameters"];

    public static async Task<CallToolResult> ExportAsync(long[] elementIds, string documentSession,
        CancellationToken cancellationToken)
    {
        if (elementIds == null || elementIds.Length == 0 || elementIds.Any(id => id <= 0))
            throw new McpException("elementIds должен содержать положительные целые 64-битные ID.");
        if (!Guid.TryParseExact(documentSession, "N", out _))
            throw new McpException("Передайте documentSession из StructuredContent get-model-elements.");
        var ids = elementIds.Distinct().ToArray();
        var started = DateTime.UtcNow;
        var elements = new List<JsonObject>();
        var errors = new List<object>();
        // Small sequential batches limit the cost of the full BuiltInParameter probe.
        foreach (var batch in ids.Chunk(10))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var request = JsonSerializer.Serialize(new
                {
                    collection = "elementParameters", scope = "document", documentSession,
                    elementIds = batch, fields = Fields
                });
                if (request.Length >= 60000) throw new InvalidDataException("Request too large");
                var response = await RevitBridgeClient.SendAsync(request, cancellationToken);
                using var parsed = JsonDocument.Parse(response);
                var root = parsed.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
                {
                    errors.Add(new { operation = "bridge", message = error.GetString(), elementIds = batch });
                    break; // Never rebind to a different document or continue after a bridge failure.
                }
                var validated = ValidateBatch(root, batch);
                elements.AddRange(validated);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException
                or FormatException or OperationCanceledException)
            {
                errors.Add(new { operation = "batch", message = "Ошибка получения/проверки ответа: " + ex.GetType().Name, elementIds = batch });
                break;
            }
        }
        var returned = elements.Select(e => e["elementId"]!.GetValue<long>()).ToHashSet();
        var remaining = ids.Where(id => !returned.Contains(id)).ToArray();
        var status = errors.Count == 0 && remaining.Length == 0 &&
            elements.All(e => e["status"]!.GetValue<string>() == "ok") ? "complete" : "partial";
        // A completely failed operation must not produce a report resembling a valid empty model.
        if (elements.Count == 0)
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "Выгрузка не выполнена: " + JsonSerializer.Serialize(errors) }]
            };
        var report = new
        {
            schemaVersion = 1, startedAtUtc = started, completedAtUtc = DateTime.UtcNow,
            documentSession, status, requestedElementIds = ids, unprocessedElementIds = remaining, elements, errors
        };
        cancellationToken.ThrowIfCancellationRequested();
        var folder = @"D:\BIM-S-MCP-1_Отчеты_Версии модели";
        Directory.CreateDirectory(folder);

        const string modelKey = "Test_AI-Work";

        var existingVersions = Directory
            .EnumerateFiles(folder, $"{modelKey}_V*_model.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name =>
            {
                var marker = name?.Split("_V", StringSplitOptions.None).LastOrDefault();
                return int.TryParse(marker?.Replace("_model", ""), out var version)
                    ? version
                    : 0;
            })
            .ToArray();

        var nextVersion = existingVersions.Length == 0
            ? 1
            : existingVersions.Max() + 1;

        var versionName = $"V{nextVersion:000}";
        var path = Path.Combine(
            folder,
            $"{modelKey}_{versionName}_model.json");
        var temp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(report, Json), new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"JSON: {path}; обработано: {elements.Count}/{ids.Length}; статус: {status}." }],
            StructuredContent = JsonSerializer.SerializeToElement(new
                { filePath = path, documentSession, processedElementCount = elements.Count, status })
        };
    }

    private static List<JsonObject> ValidateBatch(JsonElement root, long[] batch)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != batch.Length)
            throw new InvalidDataException("Unexpected batch shape");
        var seen = new HashSet<long>();
        var output = new List<JsonObject>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("ElementId", out var idNode) || !idNode.TryGetInt64(out var id) ||
                !batch.Contains(id) || !seen.Add(id) ||
                !item.TryGetProperty("status", out var statusNode) || statusNode.ValueKind != JsonValueKind.String ||
                !new[] { "ok", "partial", "error", "notFound" }.Contains(statusNode.GetString()) ||
                !item.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Invalid element result");
            var status = statusNode.GetString();
            if (status == "ok" && errors.GetArrayLength() != 0) throw new InvalidDataException("Inconsistent status");
            foreach (var field in new[] { "InstanceParameters", "TypeParameters" })
            {
                if (!item.TryGetProperty(field, out var values) || values.ValueKind == JsonValueKind.Null)
                {
                    if (status == "ok") throw new InvalidDataException("Missing parameters");
                    continue;
                }
                if (values.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid parameter array");
                var keys = new HashSet<(long, long)>();
                foreach (var parameter in values.EnumerateArray())
                {
                    if (!parameter.TryGetProperty("parameterId", out var parameterId) || !parameterId.TryGetInt64(out var pid) ||
                        !parameter.TryGetProperty("ownerElementId", out var owner) || !owner.TryGetInt64(out var oid) ||
                        !keys.Add((oid, pid)) ||
                        !parameter.TryGetProperty("source", out var source) ||
                        source.GetString() != (field == "InstanceParameters" ? "instance" : "type") ||
                        !parameter.TryGetProperty("status", out var parameterStatus) ||
                        !new[] { "ok", "noValue", "error", "partial" }.Contains(parameterStatus.GetString()) ||
                        !parameter.TryGetProperty("errors", out var parameterErrors) || parameterErrors.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("Invalid parameter");
                    if (status == "ok" && (parameterStatus.GetString() is "error" or "partial" || parameterErrors.GetArrayLength() != 0))
                        throw new InvalidDataException("Unreported parameter error");
                }
            }
            var row = new JsonObject();
            foreach (var property in item.EnumerateObject())
            {
                var name = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
                row[name] = JsonNode.Parse(property.Value.GetRawText());
            }
            output.Add(row);
        }
        return output;
    }
}