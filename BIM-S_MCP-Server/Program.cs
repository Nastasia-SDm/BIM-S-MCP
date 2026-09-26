using ModelContextProtocol.Server;

var options = new McpServerOptions
{
    ServerInfo = new() { Name = "BIM-S_MCP-Server", Version = "1.0.0" },
    ToolCollection =
    [
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
static async Task<string> GetModelElementsAsync(
    CancellationToken cancellationToken)
{
var request = System.Text.Json.JsonSerializer.Serialize(new
{
    collection = "elements",
    fields = new[] { "ElementId", "Category", "Name" },
    instancesOnly = true
});
using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
timeout.CancelAfter(TimeSpan.FromSeconds(60));

await using var pipe = new System.IO.Pipes.NamedPipeClientStream(
    ".", "BIMS_REVIT_BRIDGE", System.IO.Pipes.PipeDirection.InOut,
    System.IO.Pipes.PipeOptions.Asynchronous);

await pipe.ConnectAsync(timeout.Token);

using var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false), leaveOpen: true);
using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);

await writer.WriteLineAsync(request.AsMemory(), timeout.Token);
await writer.FlushAsync(timeout.Token);

var response = await reader.ReadLineAsync(timeout.Token);

return response ?? "Revit вернул пустой ответ.";
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
