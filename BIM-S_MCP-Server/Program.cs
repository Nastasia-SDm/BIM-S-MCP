using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

if (args.Length == 1 && args[0] == "--watch-worker")
{
    ModelWatch.RunWorker();
    return;
}

var options = new McpServerOptions
{
    ServerInfo = new() { Name = "BIM-S_MCP-Server", Version = "1.0.0" },
    ToolCollection =
    [
        McpServerTool.Create(GetModelAsync, new() { Name = "get-model", Description = "Последовательно получает элементы, параметры и HTML-отчёт" }),
        McpServerTool.Create(ModelElementsReport.CreateAsync, new() { Name = "create-model-elements-report", Description = "Создаёт HTML из сохранённой выгрузки параметров" }),
        McpServerTool.Create(ModelParametersExport.ExportAsync, new() { Name = "get-model-elements-parameters", Description = "Сохраняет встроенные параметры элементов и типов в JSON" }),
        McpServerTool.Create(ModelWatch.StartAsync, new() { Name = "start-model-watch", Description = "Начать фоновое наблюдение модели" }),
        McpServerTool.Create(ModelWatch.StopAsync, new() { Name = "stop-model-watch", Description = "Остановить фоновое наблюдение" }),
        McpServerTool.Create(ModelWatch.SummaryAsync, new() { Name = "get-model-summary", Description = "Сводка сохранённого снимка модели" }),
      McpServerTool.Create(
    GetModelElementsAsync,
    new()
    {
        Name = "get-model-elements",
        Description = "Получает ElementId, Category и Name элементов текущего документа Revit.",
    }),
        McpServerTool.Create(
            RevitPingAsync,
            new()
            {
                Name = "revit-ping",
                Description = "Отправляет ping в локальный Revit-мост BIMS_REVIT_BRIDGE и возвращает его ответ.",
            }),
    ],
};

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

// stdout is reserved for MCP messages; no logging provider is configured.
await using var server = McpServer.Create(new StdioServerTransport(options), options);

try
{
    await server.RunAsync(shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    // Normal shutdown on Ctrl+C.
}
static Task<CallToolResult> GetModelAsync(CancellationToken cancellationToken) =>
    ModelPipeline.RunAsync(GetModelElementsAsync, ModelParametersExport.ExportAsync,
        ModelElementsReport.CreateAsync, cancellationToken);

static async Task<CallToolResult> GetModelElementsAsync(
    CancellationToken cancellationToken)
{
object Property(string key, string label, string identifier = "", string method = "parameter",
    string source = "instance", string? classFilter = null, string? fallback = null, string? note = null)
    => new { key, label, identifier, method, source, classFilter, fallback, note };
var volume = Property("volume", "Объём", "HOST_VOLUME_COMPUTED");
var floorThickness = Property("thickness", "Толщина", "FLOOR_ATTR_THICKNESS_PARAM", source: "instanceThenType",
    note: "Значение не описывает толщину во всех точках перекрытия с изменённой формой.");
var noDimensions = "Для произвольной формы нет универсального встроенного размера; геометрическая замена не выполняется.";
var properties = new Dictionary<string, object[]>
{
    ["OST_StructuralFraming"] = [volume],
    ["OST_Roofs"] = [volume],
    ["OST_Stairs"] = [volume],
    ["OST_Rebar"] = [
        Property("totalLength", "Полная длина стержня", "REBAR_ELEM_TOTAL_LENGTH", fallback: "Rebar.TotalLength",
            note: "Для набора — суммарная длина набора, не отдельного стержня."),
        Property("volume", "Объём арматуры", "REINFORCEMENT_VOLUME", fallback: "Rebar.Volume",
            note: "Для набора — суммарный объём набора, не отдельного стержня.")],
    ["OST_StructuralColumns"] = [volume],
    ["OST_GenericModel"] = [Property("level", "Уровень", "Element.LevelId", method: "api")],
    ["OST_Floors"] = [Property("length", "Длина", method: "unsupported", note: noDimensions),
        Property("width", "Ширина", method: "unsupported", note: noDimensions), floorThickness, volume],
    ["OST_StructConnections"] = [Property("length", "Длина", method: "unsupported", note: noDimensions),
        Property("workPlane", "Рабочая плоскость", "SKETCH_PLANE_PARAM")],
    ["OST_Walls"] = [Property("length", "Длина", "CURVE_ELEM_LENGTH"), Property("area", "Площадь", "HOST_AREA_COMPUTED")],
    ["OST_Toposolid"] = [volume],
    ["OST_StructuralFoundation"] = [
        Property("thickness", "Толщина", "FLOOR_ATTR_THICKNESS_PARAM", source: "instanceThenType", classFilter: "Floor"),
        Property("thickness", "Толщина", "STRUCTURAL_FOUNDATION_THICKNESS", source: "instanceThenType", classFilter: "notFloor"), volume],
    ["OST_AreaRein"] = []
};
var request = System.Text.Json.JsonSerializer.Serialize(new
{
    includeDocumentSession = true,
    collection = "elements",
    fields = new[] { "ElementId", "Category", "Name", "FamilyName", "TypeName", "SystemProperties" },
    propertyRequests = properties,
    scope = "activeView",
    categories = new[]
    {
        "OST_StructuralFraming", "OST_Roofs", "OST_Stairs", "OST_Rebar",
        "OST_StructuralColumns", "OST_GenericModel", "OST_Floors",
        "OST_StructConnections", "OST_Walls", "OST_Toposolid",
        "OST_StructuralFoundation", "OST_AreaRein"
    }
});
var response = await RevitBridgeClient.SendAsync(request, cancellationToken);
using var document = System.Text.Json.JsonDocument.Parse(response);
var root = document.RootElement;
if (root.TryGetProperty("error", out var error))
    return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = error.GetString() ?? "Ошибка Revit" }] };
if (!root.TryGetProperty("documentSession", out var session) ||
    !Guid.TryParseExact(session.GetString(), "N", out _) ||
    !root.TryGetProperty("elements", out var elements) || elements.ValueKind != System.Text.Json.JsonValueKind.Array)
    throw new ModelContextProtocol.McpException("Bridge не вернул элементы с documentSession. Обновите RevitAddin.");
return new CallToolResult
{
    Content = [new TextContentBlock { Text = elements.GetRawText() }],
    StructuredContent = root.Clone()
};
}
static async Task<string> RevitPingAsync(CancellationToken cancellationToken)
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(5));

    try
    {
        await using var pipe = new System.IO.Pipes.NamedPipeClientStream(
            ".", "BIMS_REVIT_BRIDGE", System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);

        using var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false), leaveOpen: true);
        using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync("ping".AsMemory(), timeout.Token);
        await writer.FlushAsync(timeout.Token);

        var response = await reader.ReadLineAsync(timeout.Token);
        if (string.IsNullOrWhiteSpace(response))
        {
            throw new ModelContextProtocol.McpException("Revit-мост закрыл соединение или вернул пустой ответ.");
        }

        return response;
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        throw new ModelContextProtocol.McpException("Revit-мост BIMS_REVIT_BRIDGE не ответил за 5 секунд.");
    }
    catch (IOException)
    {
        throw new ModelContextProtocol.McpException("Ошибка соединения с Revit-мостом BIMS_REVIT_BRIDGE.");
    }
}

public sealed record RevitElement(
    [property: System.Text.Json.Serialization.JsonPropertyName("ElementId")] long ElementId,
    [property: System.Text.Json.Serialization.JsonPropertyName("Category")] string Category,
    [property: System.Text.Json.Serialization.JsonPropertyName("Name")] string Name);
