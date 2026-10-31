using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TestStation.Application;
using TestStation.Domain;
using TestStation.Contracts;
using TestStation.Application.Drivers;

namespace TestStation.App.Services;

public sealed record OpenTapHostEvent
{
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("run_id")] public string RunId { get; init; } = "";
    [JsonPropertyName("step_id")] public string StepId { get; init; } = "";
    [JsonPropertyName("step_run_id")] public string StepRunId { get; init; } = "";
    [JsonPropertyName("parent_step_run_id")] public string ParentStepRunId { get; init; } = "";
    [JsonPropertyName("sequence_id")] public string SequenceId { get; init; } = "";
    [JsonPropertyName("section")] public string Section { get; init; } = "";
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("payload")] public JsonElement? Payload { get; init; }
}

public sealed record OpenTapHostRun(SequenceRunResult Result, IReadOnlyList<OpenTapHostEvent> Events);

/// <summary>One isolated Host process per run. WPF never loads OpenTAP assemblies.</summary>
public sealed class OpenTapHostClient : ISequenceExecutionHost
{
    private readonly IChannelRuntimeCoordinator? _channelRuntime;
    public OpenTapHostClient(IChannelRuntimeCoordinator? channelRuntime = null) => _channelRuntime = channelRuntime;
    private Session? _session;
    public event Action<OpenTapHostEvent>? EventReceived;

    public async Task<OpenTapHostRun> RunAsync(SequenceDefinition sequence,
        IReadOnlyList<SequenceDefinition> sequences, IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyDictionary<string, object?> simulation,
        IReadOnlyList<SequenceVariable>? globalDefinitions = null, IReadOnlyList<SequenceVariable>? stationDefinitions = null,
        IReadOnlyDictionary<string, JsonElement>? globalValues = null, IReadOnlyDictionary<string, JsonElement>? stationValues = null)
    {
        var session = new Session();
        if (Interlocked.CompareExchange(ref _session, session, null) is not null)
            throw new InvalidOperationException("OpenTAP 正在执行序列");
        var events = new List<OpenTapHostEvent>();
        var runId = Guid.NewGuid().ToString("N");
        var started = DateTimeOffset.UtcNow;
        OpenTapHostEvent? finished = null;
        Exception? failure = null;
        var externalRuntime = _channelRuntime as IExternalChannelRuntime;
        var referenced = sequences.Append(sequence).SelectMany(SequenceEditor.Walk).SelectMany(step =>
            step.Params.TryGetValue("signals", out var rows) ? SequenceSignalOperation.Read(rows).Select(row => row.ChannelId) :
            step.Params.TryGetValue("channel_id", out var id) ? new[] { id.ToString() } : []);
        var externalChannels = (externalRuntime?.ExternalChannelIds ?? []).Concat(referenced.Where(id => externalRuntime?.IsExternalChannel(id) == true)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var externalIds = externalChannels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            session.Process = StartHost();
            var diagnostics = ReadDiagnosticsAsync(session);
            try
            {
                await session.SendAsync(new { command = ExecutionProtocol.Run, protocol_version = ExecutionProtocol.Version, run_id = runId, sequence, sequences, parameters,
                    simulation_values = simulation.Where(p => !externalIds.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value), external_channels = externalChannels, external_channel_types = externalRuntime?.ExternalChannelTypes,
                    global_definitions = globalDefinitions ?? [], station_definitions = stationDefinitions ?? [],
                    global_values = globalValues ?? new Dictionary<string, JsonElement>(), station_values = stationValues ?? new Dictionary<string, JsonElement>() }).ConfigureAwait(false);
                var completed = false;
                while (await session.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var value = JsonSerializer.Deserialize<OpenTapHostEvent>(line)
                        ?? throw new InvalidDataException("OpenTAP Host 返回空事件");
                    if (value.Type.Length == 0) throw new InvalidDataException("OpenTAP Host 返回无效事件");
                    if (value.RunId.Length > 0 && value.RunId != runId)
                        throw new InvalidDataException("OpenTAP Host 返回了其他运行的事件");
                    if (value.Type == ExecutionProtocol.ChannelRequest)
                    {
                        await HandleChannelRequestAsync(session, value, externalIds).ConfigureAwait(false);
                        continue;
                    }
                    events.Add(value);
                    EventReceived?.Invoke(value);
                    if (value.Type == ExecutionProtocol.Finished) finished = value;
                    if (value.Type == ExecutionProtocol.Error) failure = new InvalidOperationException(value.Message);
                    if (value.Type == ExecutionProtocol.Completed) { completed = true; break; }
                }
                session.Process.StandardInput.Close();
                await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await diagnostics.ConfigureAwait(false);
                if (!completed || session.Process.ExitCode != 0 || finished is null)
                    failure ??= new InvalidOperationException($"OpenTAP Host 未正常完成（退出码 {session.Process.ExitCode}）。{session.DiagnosticText}");
            }
            finally
            {
                session.Kill();
                // Always drain stderr, including malformed stdout and startup failures.
                await diagnostics.ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            failure = session.DiagnosticText.Length > 0
                ? new InvalidOperationException(error.Message + Environment.NewLine + session.DiagnosticText, error) : error;
        }
        finally
        {
            session.Kill();
            Interlocked.CompareExchange(ref _session, null, session);
            session.Finished.TrySetResult();
            session.Process?.Dispose();
        }
        var result = ConvertResult(runId, sequence, sequences, parameters, simulation, events, finished, started,
            session.StopRequested, failure);
        return new OpenTapHostRun(result, events);
    }

    public Task PauseAsync() => SendControlAsync(ExecutionProtocol.Pause);
    public Task ResumeAsync() => SendControlAsync(ExecutionProtocol.Resume);

    private async Task HandleChannelRequestAsync(Session session, OpenTapHostEvent request, HashSet<string> allowed)
    {
        var payload = request.Payload ?? throw new InvalidDataException("设备访问请求为空");
        var id = payload.GetProperty("request_id").GetString() ?? throw new InvalidDataException("设备访问请求 ID 为空");
        object? value = null; string? failure = null;
        try
        {
            var channel = payload.GetProperty("channel_id").GetString() ?? "";
            if (_channelRuntime is null || !allowed.Contains(channel)) throw new InvalidOperationException($"设备访问请求未授权：{channel}");
            switch (payload.GetProperty("operation").GetString())
            {
                case "read": value = (await _channelRuntime.ReadOnceAsync(channel, "automatic").ConfigureAwait(false)).Value; break;
                case "write": await _channelRuntime.WriteAsync(channel, payload.GetProperty("value"), "automatic").ConfigureAwait(false); break;
                default: throw new InvalidDataException("未知的设备访问操作");
            }
        }
        catch (Exception error) { failure = error.Message; }
        await session.SendAsync(new { command = ExecutionProtocol.ChannelResponse, request_id = id, value, error = failure }).ConfigureAwait(false);
    }

    private async Task SendControlAsync(string command)
    {
        var session = _session;
        if (session is null) return;
        try { await session.SendAsync(new { command }).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { if (!session.Finished.Task.IsCompleted) throw new IOException("无法向 OpenTAP Host 发送命令", error); }
    }

    public async Task StopAsync()
    {
        var session = _session;
        if (session is null) return;
        session.StopRequested = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            try { await session.SendAsync(new { command = ExecutionProtocol.Stop }, timeout.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or InvalidOperationException) { }
            await session.Finished.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { session.Kill(); }
    }

    private static Process StartHost()
    {
        var path = Environment.GetEnvironmentVariable("TESTSTATION_OPENTAP_HOST");
        if (string.IsNullOrWhiteSpace(path)) path = Path.Combine(AppContext.BaseDirectory, "OpenTapHost", "TestStation.OpenTapHost.dll");
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("未找到 OpenTAP Host，请使用项目根目录的快捷启动文件重新部署。", path);
        var dll = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var dotnet = Path.Combine(string.IsNullOrWhiteSpace(dotnetRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet") : dotnetRoot, "dotnet.exe");
        var info = new ProcessStartInfo
        {
            FileName = dll ? (File.Exists(dotnet) ? dotnet : "dotnet") : path,
            WorkingDirectory = Path.GetDirectoryName(path)!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (dll) info.ArgumentList.Add(path);
        return Process.Start(info) ?? throw new InvalidOperationException("无法启动 OpenTAP Host");
    }

    private static async Task ReadDiagnosticsAsync(Session session)
    {
        while (await session.Process!.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            session.Diagnostics.Enqueue(line.Length > 2000 ? line[..2000] : line);
            while (session.Diagnostics.Count > 12) session.Diagnostics.TryDequeue(out _);
        }
    }

    private static SequenceRunResult ConvertResult(string runId, SequenceDefinition sequence, IReadOnlyList<SequenceDefinition> sequences,
        IReadOnlyDictionary<string, object?> parameters, IReadOnlyDictionary<string, object?> simulation,
        IReadOnlyList<OpenTapHostEvent> events, OpenTapHostEvent? finished, DateTimeOffset started, bool stopped, Exception? failure)
    {
        // IDs are only unique inside a sequence, and loop iterations have distinct step_run_id values.
        var definitions = sequences.GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key,
            group => SequenceEditor.Walk(group.First()).GroupBy(step => step.Id, StringComparer.Ordinal)
                .ToDictionary(steps => steps.Key, steps => steps.First(), StringComparer.Ordinal), StringComparer.Ordinal);
        var results = finished?.Payload is { } payload && payload.TryGetProperty("results", out var array)
            ? JsonSerializer.Deserialize<OpenTapHostEvent[]>(array.GetRawText()) ?? []
            : events.Where(item => item.Type == "step_finished" &&
                !(item.Payload is { } data && data.TryGetProperty("record_result", out var record) && record.ValueKind == JsonValueKind.False)).ToArray();
        var steps = results.Select(item =>
        {
            SequenceStep? step = null;
            if (definitions.TryGetValue(item.SequenceId, out var map)) map.TryGetValue(item.StepId, out step);
            var data = item.Payload;
            var at = data is { } fields && fields.TryGetProperty("started_at", out var time) ? time.GetDateTimeOffset() : started;
            var duration = data is { } fields2 && fields2.TryGetProperty("duration_ms", out var milliseconds) ? (long)milliseconds.GetDouble() : 0;
            return new StepRunResult(item.StepId, step?.Name ?? item.StepId, step?.Kind ?? "", item.Status,
                item.Message, at.ToString("O"), at.AddMilliseconds(duration).ToString("O"), duration, item.SequenceId, item.Section,
                item.StepRunId, item.Path);
        }).ToArray();
        var values = finished?.Payload is { } final && final.TryGetProperty("values", out var snapshot)
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(snapshot.GetRawText()) ?? [] : new Dictionary<string, object?>();
        var status = stopped || finished?.Status == "stopped" ? "stopped" : failure is not null ? "failed" : finished?.Status == "passed" ? "completed" : "failed";
        var finalSimulation = finished?.Payload is { } finalPayload && finalPayload.TryGetProperty("simulation_values", out var finalChannels)
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(finalChannels.GetRawText()) : null;
        return new SequenceRunResult(runId, sequence.Id, sequence.Name, status, started.ToString("O"), DateTimeOffset.UtcNow.ToString("O"),
            steps, values, failure?.Message ?? (status == "failed" ? finished?.Message ?? "执行失败" : ""), parameters, simulation, finalSimulation);
    }

    private sealed class Session
    {
        public Process? Process { get; set; }
        public volatile bool StopRequested;
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Diagnostics { get; } = new();
        public string DiagnosticText => string.Join(Environment.NewLine, Diagnostics);
        private readonly SemaphoreSlim _inputLock = new(1, 1);

        public async Task SendAsync(object request, CancellationToken cancellation = default)
        {
            await _inputLock.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (Process is null) throw new InvalidOperationException("OpenTAP Host 尚未启动");
                await Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cancellation).ConfigureAwait(false);
                await Process.StandardInput.FlushAsync(cancellation).ConfigureAwait(false);
            }
            finally { _inputLock.Release(); }
        }

        public void Kill()
        {
            try { if (Process is { HasExited: false }) Process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
}
