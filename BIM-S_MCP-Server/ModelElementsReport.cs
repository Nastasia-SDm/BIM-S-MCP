using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

internal static class ModelElementsReport
{
    private const string Folder = @"D:\BIM-S-MCP-1_Отчеты_Версии модели";
    private const string Css = """

            *{box-sizing:border-box}body{margin:0;background:#eef2f6;color:#17243a;font:16px/1.5 Segoe UI,Arial,sans-serif}
            main{max-width:1400px;margin:48px auto;padding:0 24px}header{background:#13283f;color:white;padding:32px;border-radius:20px}
            h1{margin:0;font-size:30px}p{color:#b9cadd}.total{font-size:52px;font-weight:700;color:#73e1c0}.total small{font-size:18px;font-weight:400}
            .table{margin-top:24px;background:white;border-radius:16px;overflow:auto;max-height:70vh;box-shadow:0 8px 30px #13283f12}
            table{width:100%;border-collapse:collapse}th{position:sticky;top:0;background:#dfe8f1;text-align:left;z-index:1}
            th,td{padding:16px;border-bottom:1px solid #edf0f4;vertical-align:top}tr:hover{background:#f3faf8}.count{font-weight:700}
            .ids{min-width:200px;max-width:450px;overflow-wrap:anywhere;font:14px/1.7 Consolas,monospace}summary{cursor:pointer;color:#14765e}
            @media print{.table{max-height:none;overflow:visible}header{color:#17243a;background:white}details{display:block}}
            
""";
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "не определено");

    public static async Task<CallToolResult> CreateAsync(string filePath, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(filePath);
        if (!string.Equals(Path.GetDirectoryName(path), Folder, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("element-parameters_", StringComparison.Ordinal) ||
            !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new McpException("Передайте filePath JSON-выгрузки get-model-elements-parameters из папки версий модели.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        var html = Render(document.RootElement, File.GetLastWriteTimeUtc(path));
        Directory.CreateDirectory(Folder);
        var output = Path.Combine(Folder, $"model-elements_{DateTime.Now:dd.MM.yyyy_HH.mm.ss}_{Guid.NewGuid():N}.html");
        var temp = output + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, html, new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, output, overwrite: false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = "HTML: " + output }],
            StructuredContent = JsonSerializer.SerializeToElement(new { filePath = output, sourceFilePath = path })
        };
    }

    internal static string Render(JsonElement root, DateTime savedAtUtc)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var number) || number != 1 ||
            !root.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array ||
            elements.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Object))
            throw new McpException("Неверный формат JSON-выгрузки параметров.");
        static string? Text(JsonElement item, string key) =>
            item.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;
        var rows = new StringBuilder();
        foreach (var group in elements.EnumerateArray()
            .GroupBy(e => (Family: Text(e, "familyName"), Type: Text(e, "typeName")))
            .OrderBy(g => g.Key.Family, StringComparer.Ordinal).ThenBy(g => g.Key.Type, StringComparer.Ordinal))
        {
            rows.Append($"<tr><td>{E(group.Key.Family)}</td><td>{E(group.Key.Type)}</td><td class='count'>{group.Count()}</td>");
            rows.Append($"<td class='ids'><details><summary>{group.Count()} ElementId</summary>{string.Join(", ", group.Select(e => E(Text(e, "elementId"))))}</details></td><td>");
            foreach (var element in group)
            {
                rows.Append($"<details><summary>ElementId: {E(Text(element, "elementId"))}</summary>");
                foreach (var field in element.EnumerateObject())
                {
                    if ((field.Name == "instanceParameters" || field.Name == "typeParameters") &&
                        field.Value.ValueKind == JsonValueKind.Array)
                    {
                        rows.Append($"<details><summary>{E(field.Name)} ({field.Value.GetArrayLength()})</summary>");
                        foreach (var parameter in field.Value.EnumerateArray())
                        {
                            string? display = null;
                            string? name = null;
                            if (parameter.ValueKind == JsonValueKind.Object)
                            {
                                name = Text(parameter, "name");
                                display = Text(parameter, "displayValue");
                                if (string.IsNullOrEmpty(display))
                                {
                                    display = Text(parameter, "convertedValue");
                                    if (display != null) display += " " + Text(parameter, "unitTypeId");
                                    else display = Text(parameter, "rawValue");
                                }
                            }
                            rows.Append($"<details><summary>{E(name)} — {E(display)}</summary>");
                            AppendValue(rows, parameter); // All metadata, values, nulls and errors; nothing omitted.
                            rows.Append("</details>");
                        }
                        rows.Append("</details>");
                    }
                    else
                    {
                        rows.Append($"<details><summary>{E(field.Name)}</summary>");
                        AppendValue(rows, field.Value);
                        rows.Append("</details>");
                    }
                }
                rows.Append("</details>");
            }
            rows.Append("</td></tr>");
        }
        var metadata = new StringBuilder("<details><summary>Данные выгрузки</summary>");
        foreach (var field in root.EnumerateObject().Where(p => p.Name != "elements"))
        {
            metadata.Append($"<details><summary>{E(field.Name)}</summary>");
            AppendValue(metadata, field.Value);
            metadata.Append("</details>");
        }
        metadata.Append("</details>");
        var stamp = E(savedAtUtc.ToString("dd.MM.yyyy HH:mm:ss 'UTC'"));
        return "<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<title>BIM-S — Сводка модели</title><style>" + Css + "</style><main><header><h1>BIM-S — Сводка модели</h1>" +
            $"<p>Последнее изменение: {stamp}</p><div class='total'>{elements.GetArrayLength()} <small>элементов</small></div></header>" +
            "<div class='table'><table><thead><tr><th>Family Name</th><th>Type Name</th><th>Count</th><th>ElementId</th><th>Parameters</th></tr></thead><tbody>" +
            rows + "</tbody></table></div>" + metadata + "</main></html>";
    }

    private static void AppendValue(StringBuilder html, JsonElement value)
    {
        // Recursive rendering keeps every saved field, including future fields and errors.
        if (value.ValueKind == JsonValueKind.Object)
        {
            html.Append("<table><tbody>");
            foreach (var property in value.EnumerateObject())
            {
                html.Append("<tr><td>" + E(property.Name) + "</td><td>");
                AppendValue(html, property.Value);
                html.Append("</td></tr>");
            }
            html.Append("</tbody></table>");
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            html.Append("<ol>");
            foreach (var item in value.EnumerateArray())
            {
                html.Append("<li>");
                AppendValue(html, item);
                html.Append("</li>");
            }
            html.Append("</ol>");
            if (value.GetArrayLength() == 0) html.Append("[]");
        }
        else html.Append(E(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()));
    }
}