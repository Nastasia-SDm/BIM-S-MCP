using System.IO.Pipes;
using System.Text;

internal static class RevitBridgeClient
{
    internal static readonly string[] Categories = ["OST_StructuralFraming", "OST_Roofs", "OST_Stairs", "OST_Rebar",
        "OST_StructuralColumns", "OST_GenericModel", "OST_Floors", "OST_StructConnections", "OST_Walls",
        "OST_Toposolid", "OST_StructuralFoundation", "OST_AreaRein"];
    internal static async Task<string> SendAsync(string request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await using var pipe = new NamedPipeClientStream(".", "BIMS_REVIT_BRIDGE", PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(request.AsMemory(), timeout.Token);
        await writer.FlushAsync(timeout.Token);
        return await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Revit bridge closed without response.");
    }
}