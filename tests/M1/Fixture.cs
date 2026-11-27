using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

internal static class Fixture
{
    public static async Task<int> Run(string mode)
    {
        if (mode == "sleep") { await Task.Delay(Timeout.Infinite); return 0; }
        int attempt = File.Exists("count") ? int.Parse(File.ReadAllText("count")) + 1 : 1;
        File.WriteAllText("count", attempt.ToString());
        if (mode == "crash" && attempt <= 5 || mode == "stable" && attempt == 1 || mode == "backoff")
            return attempt % 2 == 0 ? 17 : 0;
        if (mode == "stable") { await Task.Delay(Timeout.Infinite); return 0; }
        if (mode == "volume")
        {
            for (int i = 0; i < 1500; i++) Console.WriteLine(new string('x', 2000));
            Console.Error.WriteLine("volume-end");
        }

        string Env(string key) => Environment.GetEnvironmentVariable(key)!;
        using var pipe = new NamedPipeClientStream(".", Env("DOTHERD_CONTROL_PIPE"), PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10000);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        Task Send(object m) => writer.WriteLineAsync(JsonSerializer.Serialize(m));
        await Send(new { type = "hello", version = 1, instance = Env("DOTHERD_INSTANCE"), token = Env("DOTHERD_CONTROL_TOKEN") });
        if (mode == "stubborn")
        {
            using var worker = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "fixture", "sleep" } })!;
            File.WriteAllText("worker", worker.Id.ToString());
        }
        if (mode == "late" && attempt > 1)
            while (!File.Exists("allow-ready")) await Task.Delay(30);
        if (mode != "never" && (mode != "late" || attempt > 1)) await Send(new { type = "ready" });
        Console.WriteLine("control-connected");
        while (await reader.ReadLineAsync() is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.GetProperty("type").GetString() != "shutdown") continue;
            Console.WriteLine("shutdown-received");
            if (mode == "stubborn") continue;
            if (mode == "late" && attempt == 1)
            {
                await Send(new { type = "ready" });
                await Send(new { type = "tray.update", tooltip = "stale" });
                Console.WriteLine("sent-late-ready");
            }
            return 0;
        }
        return 0;
    }
}
