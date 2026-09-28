using System.Text.Json;
using ModelContextProtocol.Protocol;

internal static class ModelPipeline
{
    private const string Folder = @"D:\BIM-S-MCP-1_Отчеты_Версии модели";

    internal static async Task<CallToolResult> RunAsync(
        Func<CancellationToken, Task<CallToolResult>> getElements,
        Func<long[], string, CancellationToken, Task<CallToolResult>> getParameters,
        Func<string, CancellationToken, Task<CallToolResult>> createReport,
        CancellationToken cancellationToken)
    {
        var stage = "get-model-elements";
        string? jsonPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = await getElements(cancellationToken);
            if (first.IsError == true) return Failed(stage, "Инструмент вернул ошибку.");
            var data = Data(first);
            var session = data.GetProperty("documentSession").GetString();
            if (!Guid.TryParseExact(session, "N", out _)) throw new InvalidDataException("Некорректная сессия документа.");
            var ids = data.GetProperty("elements").EnumerateArray()
                .Select(e => e.GetProperty("ElementId").GetInt64()).Distinct().ToArray();
            if (ids.Length == 0) return Failed(stage, "Элементы не найдены; следующие этапы не запускались.");
            if (ids.Any(id => id <= 0)) throw new InvalidDataException("Некорректные ElementId.");

            stage = "get-model-elements-parameters";
            cancellationToken.ThrowIfCancellationRequested();
            var second = await getParameters(ids, session!, cancellationToken);
            if (second.IsError == true) return Failed(stage, "Инструмент вернул ошибку.");
            var exported = Data(second);
            jsonPath = ReportPath(exported.GetProperty("filePath").GetString(), ".json");
            if (exported.GetProperty("documentSession").GetString() != session)
                throw new InvalidDataException("Сессия результата не совпадает с первым этапом.");
            var count = exported.GetProperty("processedElementCount").GetInt32();
            if (exported.GetProperty("status").GetString() != "complete" || count != ids.Length)
                return Failed(stage, "Выгрузка неполная (partial); HTML не создавался. Причины сохранены в JSON.", jsonPath);

            stage = "create-model-elements-report";
            cancellationToken.ThrowIfCancellationRequested();
            var third = await createReport(jsonPath, cancellationToken);
            if (third.IsError == true) return Failed(stage, "Инструмент вернул ошибку.", jsonPath);
            var report = Data(third);
            var htmlPath = ReportPath(report.GetProperty("filePath").GetString(), ".html");
            if (!string.Equals(Path.GetFullPath(report.GetProperty("sourceFilePath").GetString()!),
                jsonPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("HTML создан не из результата второго этапа.");
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Статус: complete; обработано элементов: {count}; JSON: {jsonPath}; HTML: {htmlPath}" }],
                StructuredContent = JsonSerializer.SerializeToElement(new
                    { status = "complete", processedElementCount = count, jsonPath, htmlPath })
            };
        }
        catch (OperationCanceledException)
        {
            return Failed(stage, "Выполнение отменено или истекло время ожидания.", jsonPath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        {
            // Do not forward arbitrary exception messages or payloads.
            return Failed(stage, "Ошибка выполнения или формата результата (" + ex.GetType().Name + ").", jsonPath);
        }
    }

    private static JsonElement Data(CallToolResult result) =>
        result.StructuredContent is { ValueKind: JsonValueKind.Object } data
            ? data : throw new InvalidDataException("Отсутствует StructuredContent.");

    private static string ReportPath(string? path, string extension)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Отсутствует путь.");
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Folder, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(full), extension, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new InvalidDataException("Некорректный путь отчёта.");
        return full;
    }

    private static CallToolResult Failed(string stage, string message, string? jsonPath = null) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"Ошибка этапа {stage}: {message}" + (jsonPath == null ? "" : " JSON: " + jsonPath) }],
        StructuredContent = JsonSerializer.SerializeToElement(new { status = "error", stage, message, jsonPath })
    };
}