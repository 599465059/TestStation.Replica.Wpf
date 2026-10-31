using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TestStation.VirtualPowerSupply;

Console.OutputEncoding = new UTF8Encoding(false);
var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--help" or "-h")
    {
        Console.WriteLine("Virtual SCPI PSU: --port 5025 --max-voltage 60 --max-current 10 --load-ohms 10 --response-delay-ms 0 --log <path>");
        return;
    }
    if (i + 1 >= args.Length || args[i] is not ("--port" or "--max-voltage" or "--max-current" or "--load-ohms" or "--response-delay-ms" or "--log"))
        throw new ArgumentException($"Unknown/incomplete argument: {args[i]}");
    if (!values.TryAdd(args[i], args[++i])) throw new ArgumentException("Duplicate argument");
}

double ReadNumber(string name, double fallback) => values.TryGetValue(name, out var value)
    ? double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture) : fallback;
var portValue = ReadNumber("--port", 5025);
var voltage = ReadNumber("--max-voltage", 60);
var current = ReadNumber("--max-current", 10);
var resistance = ReadNumber("--load-ohms", 10);
var delay = ReadNumber("--response-delay-ms", 0);
if (!double.IsFinite(portValue) || portValue != Math.Truncate(portValue) || portValue is < 1 or > 65535 ||
    !double.IsFinite(voltage) || voltage is <= 0 or > 1e6 || !double.IsFinite(current) || current is <= 0 or > 1e6 ||
    !double.IsFinite(resistance) || resistance is < 1e-6 or > 1e12 || !double.IsFinite(delay) || delay is < 0 or > 60000)
    throw new ArgumentException("Invalid instrument parameters");
var port = (int)portValue;
var instrument = new ScpiInstrument(voltage, current, resistance);
var logPath = Path.GetFullPath(values.GetValueOrDefault("--log") ?? Path.Combine(AppContext.BaseDirectory, "logs", "scpi-traffic.log"));
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
using var log = new StreamWriter(logPath, append: true, new UTF8Encoding(false)) { AutoFlush = true };
var logSync = new object();
void Log(string text)
{
    var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {text}";
    lock (logSync) { Console.WriteLine(line); log.WriteLine(line); }
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var clients = new ConcurrentDictionary<int, Task>();
var listener = new TcpListener(IPAddress.Loopback, port);

async Task Serve(TcpClient client, int id)
{
    using (client)
    {
        Log($"[{id}] CONNECT {client.Client.RemoteEndPoint}");
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var buffer = new byte[2048];
            var message = new StringBuilder();
            while (!stop.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(buffer, stop.Token);
                if (count == 0) break;
                for (var i = 0; i < count; i++)
                {
                    var b = buffer[i];
                    if (b == '\n')
                    {
                        var command = message.ToString().TrimEnd('\r'); message.Clear();
                        if (command.Length == 0) continue;
                        Log($"[{id}] RX {command}");
                        var response = instrument.Execute(command);
                        Log($"[{id}] STATE {instrument.State}");
                        if (response is not null)
                        {
                            if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), stop.Token);
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "\n"), stop.Token);
                            Log($"[{id}] TX {response}");
                        }
                    }
                    else
                    {
                        if (b > 127 || b < 32 && b is not (9 or 13) || message.Length >= 4096)
                            throw new InvalidDataException("Invalid ASCII/overlong SCPI message; closing session");
                        message.Append((char)b);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or SocketException) { Log($"[{id}] ERROR {error.Message}"); }
        finally { Log($"[{id}] DISCONNECT"); }
    }
}

try
{
    listener.Start();
    Console.Title = $"Virtual SCPI PSU — 127.0.0.1:{port}";
    Log($"LISTEN 127.0.0.1:{port} | {voltage} V / {current} A | R={resistance} ohm | Ctrl+C to stop");
    Log("Public SCPI subset. Not an ITECH model emulator. Writes have no ACK. Queries end with LF.");
    Log($"TRAFFIC LOG {logPath}");
    var id = 0;
    while (!stop.IsCancellationRequested)
    {
        var client = await listener.AcceptTcpClientAsync(stop.Token);
        var key = ++id;
        clients[key] = Serve(client, key);
        foreach (var completed in clients.Where(p => p.Value.IsCompleted).Select(p => p.Key)) clients.TryRemove(completed, out _);
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
catch (SocketException error) { Log($"START FAILED: {error.Message}"); Environment.ExitCode = 1; }
finally { stop.Cancel(); listener.Stop(); await Task.WhenAll(clients.Values); }
