using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

internal static class WorkerTrace
{
    // Serena Pool launches this observer with its normal worker arguments. Only protocol labels go to the trace.
    public static async Task<int> Run(string[] args)
    {
        var executable = Environment.GetEnvironmentVariable("DOTHERD_P03_WORKER") ?? throw new InvalidOperationException("Missing worker path.");
        var log = Environment.GetEnvironmentVariable("DOTHERD_P03_TRACE") ?? throw new InvalidOperationException("Missing trace path.");
        var utf8 = new UTF8Encoding(false);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            StandardInputEncoding = utf8, StandardOutputEncoding = utf8
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        if (Environment.GetEnvironmentVariable("DOTHERD_P03_SLOW") == "1")
            await Task.Delay(TimeSpan.FromSeconds(7));
        using var worker = Process.Start(start) ?? throw new IOException("Worker did not start.");
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
        var requests = new ConcurrentDictionary<string, string>();
        object sync = new();
        void Record(string label)
        {
            lock (sync) File.AppendAllText(log, label + Environment.NewLine);
        }
        async Task Forward(TextReader source, TextWriter target, bool toWorker)
        {
            while (await source.ReadLineAsync() is { } line)
            {
                var json = JsonNode.Parse(line)!;
                if (toWorker && (string?)json["method"] is { } method)
                {
                    var parameters = json["params"];
                    if (method is "server/discover" or "initialize" or "tools/list" or "tools/call")
                    {
                        string? version = (string?)parameters?["_meta"]?["io.modelcontextprotocol/protocolVersion"]
                            ?? (string?)parameters?["protocolVersion"];
                        Record($"{method} {version ?? "(handshake)"}");
                    }
                    if (method is "server/discover" or "initialize") requests[json["id"]!.ToString()] = method;
                }
                else if (!toWorker && json["id"] is { } id && requests.TryRemove(id.ToString(), out var methodName))
                {
                    Record(json["error"] is { } error ? $"{methodName} error {error["code"]}" : $"{methodName} ok");
                }
                await target.WriteLineAsync(line);
                await target.FlushAsync();
            }
        }
        try
        {
            var incoming = Forward(input, worker.StandardInput, true);
            var outgoing = Forward(worker.StandardOutput, output, false);
            await await Task.WhenAny(incoming, outgoing);
            worker.StandardInput.Close();
            await outgoing;
            await worker.WaitForExitAsync();
            return worker.ExitCode;
        }
        finally
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
        }
    }
}
