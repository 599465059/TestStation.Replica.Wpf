using System.Text;
using System.Text.Json;
using TestStation.OpenTapHost;
using TestStation.Contracts;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
// Keep stdout reserved for the JSON-lines protocol, even if a plugin writes to Console.
var output = Console.Out;
Console.SetOut(Console.Error);
using var outputLock = new SemaphoreSlim(1, 1);
Task? activeRun = null;

async Task Publish(HostEvent value)
{
    var json = JsonSerializer.Serialize(value, options);
    await outputLock.WaitAsync();
    try { await output.WriteLineAsync(json); await output.FlushAsync(); }
    finally { outputLock.Release(); }
}

var channelAccess = new HostChannelAccess(Publish);
var runner = new OpenTapRunner(channelAccess);

async Task Execute(HostRunRequest request)
{
    try { await runner.RunAsync(request, Publish); }
    catch (OperationCanceledException) { await Publish(new HostEvent { Type = ExecutionProtocol.Error, RunId = request.RunId, Status = "stopped", Message = "执行已取消" }); }
    catch (Exception error) { await Publish(new HostEvent { Type = ExecutionProtocol.Error, RunId = request.RunId, Status = "failed", Message = error.Message }); }
    finally { await Publish(new HostEvent { Type = ExecutionProtocol.Completed, RunId = request.RunId, Message = "Host 执行任务已结束" }); }
}

while (await Console.In.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    try
    {
        using var document = JsonDocument.Parse(line);
        var command = document.RootElement.TryGetProperty("command", out var value) ? value.GetString() : "run";
        if (command == ExecutionProtocol.ChannelResponse) { channelAccess.Complete(document.RootElement); continue; }
        if (command == ExecutionProtocol.Stop) { await runner.StopAsync(); continue; }
        if (command == ExecutionProtocol.Pause) { await runner.PauseAsync(); continue; }
        if (command == ExecutionProtocol.Resume) { await runner.ResumeAsync(); continue; }
        if (command != ExecutionProtocol.Run) throw new InvalidDataException($"未知命令: {command}");
        if (activeRun is { IsCompleted: false }) throw new InvalidOperationException("Host 正在执行序列");
        var request = JsonSerializer.Deserialize<HostRunRequest>(line, options) ?? throw new InvalidDataException("执行请求为空");
        if (request.ProtocolVersion != ExecutionProtocol.Version) throw new InvalidDataException($"执行协议版本不匹配: {request.ProtocolVersion}");
        activeRun = Execute(request);
    }
    catch (Exception error) { await Publish(new HostEvent { Type = ExecutionProtocol.Error, Status = "failed", Message = error.Message }); }
}
// Losing the parent pipe also cancels a run (for example if WPF exits unexpectedly).
channelAccess.Close();
if (activeRun is { IsCompleted: false }) await runner.StopAsync();
if (activeRun is not null) await activeRun;
