using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

internal static class ModelWatch
{
    private static readonly string UserId = GetUserId();
    private static string GetUserId()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required.");
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }
    private static readonly string PipeName = "BIMS_MODEL_WATCH_" + UserId;
    private static readonly string Root = @"D:\BIM-S-MCP_Отчеты";
    private static readonly string StatePath = Path.Combine(Root, "current-watch.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) };
    private sealed record State(string Session, string? DocumentSession, string Status, string? Latest, string? LastError);
    private sealed record Row(long Id, string? Family, string? Type);
    private sealed record Group(string? FamilyName, string? TypeName, int Count, long[] ElementIds);
    private sealed record Snapshot(int TotalCount, Group[] Groups);
    private static CancellationTokenSource? collection;
    private static Task? collectionTask;
    private static State? state;

    public static async Task<string> StartAsync(CancellationToken cancellationToken)
    {
        try { return await ControlAsync("start", 500, cancellationToken); }
        catch (TimeoutException) { }
        catch (IOException) { }
        // WMI owns the new process: MCP SDK kills its descendant tree when stdio closes.
        // Creating the worker through WMI keeps it outside that tree, with no inherited stdio.
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var commandLine = "\"" + dotnet + "\" \"" + typeof(ModelWatch).Assembly.Location + "\" --watch-worker";
        static string PsLiteral(string value) => "'" + value.Replace("'", "''") + "'";
        var script = "$ErrorActionPreference='Stop'; $startup=([wmiclass]'Win32_ProcessStartup').CreateInstance(); " +
            "$startup.ShowWindow=0; $r=([wmiclass]'Win32_Process').Create(" + PsLiteral(commandLine) + "," +
            PsLiteral(AppContext.BaseDirectory) + ",$startup); if($r.ReturnValue -ne 0){exit 1}";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-WindowStyle"); start.ArgumentList.Add("Hidden");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start) ?? throw new IOException("Worker launcher failed.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(output, error); // Deliberately do not forward launcher output.
        if (process.ExitCode != 0) return "Не удалось запустить worker через Windows WMI.";
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try { return await ControlAsync("start", 500, cancellationToken); }
            catch (TimeoutException) { }
            catch (IOException) { }
            await Task.Delay(200, cancellationToken);
        }
        return "Не удалось запустить worker наблюдения.";
    }

    public static async Task<string> StopAsync(CancellationToken cancellationToken)
    {
        try { return await ControlAsync("stop", 1500, cancellationToken); }
        catch (TimeoutException) { return "Worker недоступен или уже остановлен."; }
        catch (IOException) { return "Worker недоступен или уже остановлен."; }
    }

    private static async Task<string> ControlAsync(string command, int connectTimeout, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(connectTimeout, timeout.Token);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(command.AsMemory(), timeout.Token);
        return await reader.ReadLineAsync(timeout.Token) ?? "Worker не вернул ответ.";
    }

    internal static void RunWorker()
    {
        // Mutex is acquired and released on this same synchronous thread.
        using var singleton = new Mutex(false, "Local\\" + PipeName);
        bool acquired;
        try { acquired = singleton.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return;
        try { RunControlAsync().GetAwaiter().GetResult(); }
        finally { singleton.ReleaseMutex(); }
    }

    private static async Task RunControlAsync()
    {
        Directory.CreateDirectory(Root);
        if (File.Exists(StatePath))
        {
            try { state = JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(StatePath), Json); }
            catch (JsonException) { }
            if (state != null) { state = state with { Status = "stopped" }; SaveState(); }
        }
        while (true)
        {
            await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var command = await reader.ReadLineAsync(timeout.Token);
                string response;
                if (command == "start")
                {
                    if (collectionTask is { IsCompleted: false }) response = "Наблюдение уже запущено. Интервал — 2 минуты.";
                    else
                    {
                        collection?.Dispose();
                        collection = new CancellationTokenSource();
                        state = new State(Guid.NewGuid().ToString("N"), null, "starting", null, null);
                        Directory.CreateDirectory(SessionDirectory(state));
                        SaveState();
                        collectionTask = CollectAsync(collection.Token);
                        response = "Наблюдение запущено. Первый снимок — после успешного чтения; затем опрос каждые 2 минуты.";
                    }
                }
                else if (command == "stop")
                {
                    collection?.Cancel();
                    if (collectionTask != null) await collectionTask;
                    if (state != null) { state = state with { Status = "stopped" }; SaveState(); }
                    response = "Наблюдение остановлено. Сохранённые снимки доступны.";
                }
                else response = "Неизвестная команда worker.";
                await writer.WriteLineAsync(response.AsMemory(), timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException)
            {
                // Never log IPC payloads or exception messages; a disconnected controller does not stop collection.
            }
        }
    }

    private static async Task CollectAsync(CancellationToken cancellationToken)
    {
        string? previous = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (state!.DocumentSession == null)
                {
                    var identity = await RevitBridgeClient.SendAsync(JsonSerializer.Serialize(new
                        { collection = "document", fields = new[] { "SessionId" }, scope = "document" }), cancellationToken);
                    using var doc = JsonDocument.Parse(identity);
                    if (doc.RootElement.TryGetProperty("error", out _)) throw new InvalidDataException();
                    var session = doc.RootElement.GetProperty("SessionId").GetString();
                    if (!Guid.TryParseExact(session, "N", out _)) throw new InvalidDataException();
                    state = state with { DocumentSession = session };
                    SaveState();
                }
                var request = JsonSerializer.Serialize(new
                {
                    collection = "elements", fields = new[] { "ElementId", "FamilyName", "TypeName" },
                    scope = "document", documentSession = state.DocumentSession, categories = RevitBridgeClient.Categories
                });
                var response = await RevitBridgeClient.SendAsync(request, cancellationToken);
                using var document = JsonDocument.Parse(response);
                if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
                var rows = new Dictionary<long, Row>();
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    var id = element.GetProperty("ElementId").GetInt64();
                    string? Name(string field)
                    {
                        var value = element.GetProperty(field);
                        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
                    }
                    var row = new Row(id, Name("FamilyName"), Name("TypeName"));
                    if (rows.TryGetValue(id, out var old) && old != row) throw new InvalidDataException();
                    rows[id] = row;
                }
                var snapshot = new Snapshot(rows.Count, rows.Values.GroupBy(r => (r.Family, r.Type))
                    .OrderBy(g => g.Key.Family, StringComparer.Ordinal).ThenBy(g => g.Key.Type, StringComparer.Ordinal)
                    .Select(g => new Group(g.Key.Family, g.Key.Type, g.Count(), g.Select(r => r.Id).Order().ToArray())).ToArray());
                var json = JsonSerializer.Serialize(snapshot, Json);
                cancellationToken.ThrowIfCancellationRequested();
                if (json != previous)
                {
                    var folder = SessionDirectory(state);
                    var filename = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss.fffffff'Z'") + ".json";
                    var path = Path.Combine(folder, filename);
                    AtomicWrite(path, json);
                    // HTML is generated from the saved JSON, not from an independent model query.
                    WriteHtml(path);
                    state = state with { Latest = filename, Status = "running", LastError = null };
                    SaveState();
                    previous = json;
                }
                else if (state.Status != "running" || state.LastError != null)
                { state = state with { Status = "running", LastError = null }; SaveState(); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                InvalidOperationException or KeyNotFoundException or FormatException or OperationCanceledException)
            {
                state = state! with { Status = "paused", LastError = "Сбор недоступен: проверьте Revit и исходный активный документ. Последний снимок сохранён." };
                try { SaveState(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(2), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public static Task<string> SummaryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var current = File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath), Json) : null;
            if (current?.Latest == null) return Task.FromResult("Сохранённого снимка ещё нет." + (current?.LastError == null ? "" : " " + current.LastError));
            var path = Path.Combine(SessionDirectory(current), Path.GetFileName(current.Latest));
            var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), Json) ?? throw new JsonException();
            var html = HtmlPath(path);
            if (!File.Exists(html)) WriteHtml(path);
            var text = new StringBuilder().AppendLine($"Всего элементов: {snapshot.TotalCount}");
            foreach (var group in snapshot.Groups)
                text.AppendLine($"{group.FamilyName ?? "не определено"} — {group.TypeName ?? "не определено"} — {group.Count} экземпляра ({string.Join(", ", group.ElementIds)})");
            text.AppendLine("HTML: " + html);
            if (current.LastError != null) text.AppendLine(current.LastError);
            return Task.FromResult(text.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return Task.FromResult("Не удалось прочитать сохранённый снимок."); }
    }

    private static string SessionDirectory(State value)
    {
        if (!Guid.TryParseExact(value.Session, "N", out _)) throw new InvalidDataException();
        return Path.Combine(Root, value.Session);
    }
    private static void SaveState() => AtomicWrite(StatePath, JsonSerializer.Serialize(state, Json));
    private static void AtomicWrite(string path, string content)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, content, new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static string HtmlPath(string snapshotPath) =>
        Path.Combine(Path.GetDirectoryName(snapshotPath)!,
            "summary_" + File.GetLastWriteTime(snapshotPath).ToString("dd.MM.yyyy_HH.mm",
                System.Globalization.CultureInfo.InvariantCulture) + ".html");
    private static void WriteHtml(string snapshotPath)
    {
        var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(snapshotPath), Json) ?? throw new JsonException();
        static string E(string? text) => WebUtility.HtmlEncode(text ?? "не определено");
        var rows = new StringBuilder();
        foreach (var g in snapshot.Groups)
            rows.Append($"<tr><td>{E(g.FamilyName)}</td><td>{E(g.TypeName)}</td><td class='count'>{g.Count}</td><td class='ids'><details><summary>{g.ElementIds.Length} ElementId</summary>{string.Join(", ", g.ElementIds)}</details></td></tr>");
        var stamp = File.GetLastWriteTimeUtc(snapshotPath).ToString("dd.MM.yyyy HH:mm:ss 'UTC'");
        var html = """
            <!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>BIM-S — Сводка модели</title><style>
            *{box-sizing:border-box}body{margin:0;background:#eef2f6;color:#17243a;font:16px/1.5 Segoe UI,Arial,sans-serif}
            main{max-width:1400px;margin:48px auto;padding:0 24px}header{background:#13283f;color:white;padding:32px;border-radius:20px}
            h1{margin:0;font-size:30px}p{color:#b9cadd}.total{font-size:52px;font-weight:700;color:#73e1c0}.total small{font-size:18px;font-weight:400}
            .table{margin-top:24px;background:white;border-radius:16px;overflow:auto;max-height:70vh;box-shadow:0 8px 30px #13283f12}
            table{width:100%;border-collapse:collapse}th{position:sticky;top:0;background:#dfe8f1;text-align:left;z-index:1}
            th,td{padding:16px;border-bottom:1px solid #edf0f4;vertical-align:top}tr:hover{background:#f3faf8}.count{font-weight:700}
            .ids{min-width:200px;max-width:450px;overflow-wrap:anywhere;font:14px/1.7 Consolas,monospace}summary{cursor:pointer;color:#14765e}
            @media print{.table{max-height:none;overflow:visible}header{color:#17243a;background:white}details{display:block}}
            </style><main><header><h1>BIM-S — Сводка модели</h1>
            """ + $"<p>Последнее изменение: {stamp}</p><div class='total'>{snapshot.TotalCount} <small>элементов</small></div></header>" +
            "<div class='table'><table><thead><tr><th>Имя семейства</th><th>Имя типа</th><th>Количество экземпляров</th><th>ElementId</th></tr></thead><tbody>" + rows + "</tbody></table></div></main></html>";
        var path = HtmlPath(snapshotPath);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, html, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}