using ModelContextProtocol.Server;

var options = new McpServerOptions
{
    ServerInfo = new() { Name = "BIM-S_MCP-Server", Version = "1.0.0" },
    // Register future tools here with McpServerTool.Create(...).
    ToolCollection = [],
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
