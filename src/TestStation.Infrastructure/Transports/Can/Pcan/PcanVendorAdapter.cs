using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using TestStation.Application.Hardware;
using TestStation.Application.Drivers;
using TestStation.Infrastructure.Transports;
using TestStation.Infrastructure.Transports.Can;

namespace TestStation.Infrastructure.Transports.Can.Pcan;

/// <summary>PEAK PCAN-Basic adapter. It addresses every channel exposed by PCAN-Basic.</summary>
public sealed class PcanVendorAdapter : ICanVendorAdapter, ICanHardwareDiscovery
{
    public string BackendId => "pcan";
    public CanNativeLibraryStatus NativeLibrary => CanNativeLibraryLoader.GetStatus(BackendId);
    public string NormalizeChannel(string channel) => $"0x{ParseHandle(channel):X4}";
    internal static ushort ParseHandle(string channel)
    {
        channel = channel.Trim();
        if (TryParseNamedHandle(channel, out var alias)) return alias;
        if (channel.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ushort.TryParse(channel.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) && hex != 0) return hex;
        if (ushort.TryParse(channel, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) && numeric != 0) return numeric;
        throw new ArgumentException("PCAN 通道必须来自 PCAN-Basic 枚举，例如 0x0051、PCAN_USBBUS1 或 PCAN_PCIBUS1；不能使用全部通道句柄 0");
    }

    private static bool TryParseNamedHandle(string value, out ushort handle)
    {
        handle = 0;
        if (!value.StartsWith("PCAN_", StringComparison.OrdinalIgnoreCase)) return false;
        var names = new (string Prefix, int Base, int ExtendedBase, int Max)[]
        {
            ("USBBUS", 0x50, 0x500, 16), ("PCIBUS", 0x40, 0x400, 16),
            ("ISABUS", 0x20, 0x20, 8), ("PCCBUS", 0x60, 0x60, 2), ("LANBUS", 0x800, 0x800, 16)
        };
        foreach (var (prefix, baseValue, extendedBase, max) in names)
        {
            if (!value.StartsWith("PCAN_" + prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var numberText = value[(5 + prefix.Length)..];
            if (!int.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 || number > max) return false;
            handle = checked((ushort)(number <= 8 || prefix is "ISABUS" or "PCCBUS" ? baseValue + number : extendedBase + number));
            return true;
        }
        if (value.Equals("PCAN_DNGBUS1", StringComparison.OrdinalIgnoreCase)) { handle = 0x31; return true; }
        return false;
    }

    public IReadOnlyList<CanHardwareChannel> Enumerate() => WithRuntime(() =>
    {
        PcanNative.Check(PcanNative.GetUInt(0, 42, out var count, 4), "枚举通道数量");
        if (count == 0) return (IReadOnlyList<CanHardwareChannel>)Array.Empty<CanHardwareChannel>();
        if (count > 1024) throw new InvalidDataException("PCAN 返回的通道数量异常");
        var channels = new PcanNative.ChannelInfo[count];
        PcanNative.Check(PcanNative.GetChannels(0, 43, channels, checked(count * (uint)Marshal.SizeOf<PcanNative.ChannelInfo>())), "枚举通道");
        return channels.Select(c => new CanHardwareChannel(BackendId, $"0x{c.Handle:X4}", string.IsNullOrWhiteSpace(c.Name) ? $"PCAN 0x{c.Handle:X4}" : c.Name, c.DeviceId.ToString(CultureInfo.InvariantCulture), c.Controller, (c.Features & 1) != 0, c.Condition == 1)).ToArray();
    });

    Task<IReadOnlyList<CanHardwareChannel>> ICanHardwareDiscovery.EnumerateAsync(string backendId, CancellationToken cancellationToken) => Task.FromResult((IReadOnlyList<CanHardwareChannel>)Enumerate());
    public ICanController Open(CanBusOptions options) => WithRuntime<ICanController>(() => new PcanController(options));

    private static T WithRuntime<T>(Func<T> action)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("PCAN-Basic 后端仅支持 Windows");
        try { return action(); }
        catch (DllNotFoundException e)
        {
            var status = CanNativeLibraryLoader.GetStatus("pcan");
            throw new NotSupportedException(status.Error ?? "PCAN-Basic 运行库不可用；请检查软件 Drivers/CAN/PCAN 目录或安装 PEAK 官方驱动", e);
        }
        catch (BadImageFormatException e) { throw new NotSupportedException("PCANBasic.dll 位数不匹配，请使用与主程序一致的 x64/x86 运行库", e); }
        catch (EntryPointNotFoundException e) { throw new NotSupportedException("PCAN-Basic 版本过旧或不完整，请更新 PEAK 官方运行库", e); }
    }
}

internal sealed class PcanController : ICanController
{
    private readonly ushort _handle;
    private readonly CanBusOptions _options;
    private bool _opened;
    private DateTimeOffset? _timestampOrigin;
    private static readonly int[] DlcLengths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];

    public PcanController(CanBusOptions options)
    {
        _options = options;
        _handle = PcanVendorAdapter.ParseHandle(options.Channel);
        var fdTiming = options.FdEnabled ? BuildFdTiming(options.BitRate, options.DataBitRate) : null;
        var bitrate = options.FdEnabled ? (ushort)0 : ClassicBitrate(options.BitRate);
        PcanNative.Check(PcanNative.GetUInt(_handle, 13, out var condition, 4), "查询通道状态");
        if (condition != 1) throw new IOException($"PCAN 通道 {options.Channel} 不可用或已被占用，请关闭 PCAN-View 等占用程序");
        try
        {
            var result = options.FdEnabled ? PcanNative.InitializeFd(_handle, fdTiming!) : PcanNative.Initialize(_handle, bitrate, 0, 0, 0);
            PcanNative.Check(result, "初始化通道");
            _opened = true;
            uint listen = options.ListenOnly ? 1u : 0u;
            PcanNative.Check(PcanNative.SetUInt(_handle, 8, ref listen, 4), "设置只听模式");
        }
        catch { if (_opened) Dispose(); throw; }
    }

    public void Send(CanFrame frame)
    {
        frame.Validate();
        if (!_opened) throw new IOException("PCAN 通道未打开");
        if (_options.ListenOnly) throw new InvalidOperationException("PCAN 通道处于只听模式，不能发送");
        if (frame.IsFd && !_options.FdEnabled) throw new InvalidOperationException("经典 CAN 会话不能发送 CAN FD 帧");
        var type = (byte)((frame.IsRemote ? 1 : 0) | (frame.IsExtended ? 2 : 0) | (frame.IsFd ? 4 : 0) | (frame.BitRateSwitch ? 8 : 0));
        if (_options.FdEnabled)
        {
            var dlc = frame.IsRemote ? frame.RemoteDataLength : Array.IndexOf(DlcLengths, frame.Data.Length);
            if (dlc < 0) throw new ArgumentException("CAN FD 数据长度不是合法 DLC 长度");
            var message = new PcanNative.MessageFd { Id = frame.Id, Type = type, Dlc = checked((byte)dlc), Data = new byte[64] };
            frame.Data.Span.CopyTo(message.Data);
            PcanNative.Check(PcanNative.WriteFd(_handle, ref message), "发送");
        }
        else
        {
            var message = new PcanNative.Message { Id = frame.Id, Type = type, Length = frame.IsRemote ? frame.RemoteDataLength : checked((byte)frame.Data.Length), Data = new byte[8] };
            frame.Data.Span.CopyTo(message.Data);
            PcanNative.Check(PcanNative.Write(_handle, ref message), "发送");
        }
    }

    public bool TryReceive(out CanFrame? frame)
    {
        frame = null;
        uint status, id; byte type, length; byte[] data; ulong micros;
        if (_options.FdEnabled)
        {
            status = PcanNative.ReadFd(_handle, out var message, out micros);
            id = message.Id; type = message.Type; data = message.Data;
            if (status == 0 && message.Dlc > 15) throw new InvalidDataException("PCAN 返回无效 CAN FD DLC");
            length = status == 0 ? (byte)DlcLengths[message.Dlc] : (byte)0;
        }
        else
        {
            status = PcanNative.Read(_handle, out var message, out var timestamp);
            id = message.Id; type = message.Type; data = message.Data; length = message.Length;
            micros = ((ulong)timestamp.Overflow * 0x100000000UL + timestamp.Millis) * 1000 + timestamp.Micros;
        }
        if (status == PcanNative.QueueEmpty) return false;
        PcanNative.Check(status, "接收");
        if (data is null || length > data.Length) throw new InvalidDataException("PCAN 返回的数据长度无效");
        if ((type & 0xC0) != 0) throw new IOException($"PCAN 总线状态或错误帧：类型 0x{type:X2}");
        var remote = (type & 1) != 0;
        _timestampOrigin ??= DateTimeOffset.UtcNow.AddTicks(-checked((long)micros * 10));
        frame = new CanFrame(id, remote ? ReadOnlyMemory<byte>.Empty : data.AsMemory(0, length), (type & 2) != 0, remote, (type & 4) != 0, (type & 8) != 0, _timestampOrigin.Value.AddTicks(checked((long)micros * 10)), remote ? length : (byte)0);
        frame.Validate();
        return true;
    }

    public void Dispose()
    {
        if (!_opened) return;
        _opened = false;
        try { PcanNative.Check(PcanNative.Uninitialize(_handle), "关闭通道"); } catch { }
    }

    private static ushort ClassicBitrate(int rate) => rate switch
    {
        1000000 => 0x0014, 800000 => 0x0016, 500000 => 0x001C, 250000 => 0x011C, 125000 => 0x031C,
        100000 => 0x432F, 95000 => 0xC34E, 83333 => 0x852B, 50000 => 0x472F, 47619 => 0x1414,
        33333 => 0x8B2F, 20000 => 0x532F, 10000 => 0x672F, 5000 => 0x7F7F,
        _ => throw new ArgumentException($"PCAN 经典 CAN 不支持波特率 {rate}，PCAN-Basic 常用值为 125000、250000、500000、1000000")
    };

    private static string BuildFdTiming(int nominal, int data)
    {
        var n = nominal switch { 125000 => 8, 250000 => 4, 500000 => 2, 1000000 => 1, _ => throw new ArgumentException("PCAN FD 仲裁速率支持 125k、250k、500k、1M") };
        var d = data switch { 500000 => (2, 63, 16), 1000000 => (1, 63, 16), 2000000 => (1, 31, 8), 4000000 => (1, 15, 4), 5000000 => (1, 11, 4), 8000000 => (1, 7, 2), _ => throw new ArgumentException("PCAN FD 数据速率支持 500k、1M、2M、4M、5M、8M") };
        return FormattableString.Invariant($"f_clock=80000000,nom_brp={n},nom_tseg1=63,nom_tseg2=16,nom_sjw=16,data_brp={d.Item1},data_tseg1={d.Item2},data_tseg2={d.Item3},data_sjw={d.Item3}");
    }
}

/// <summary>One native receive loop distributes frames to all async subscribers.</summary>
public sealed class PcanCanBusSession : TransportSessionBase, ICanBusSession
{
    private readonly PcanVendorAdapter _adapter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _subscriptionsSync = new();
    private readonly List<Channel<CanFrame>> _subscriptions = [];
    private ICanController? _controller;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;

    public PcanCanBusSession(PcanVendorAdapter adapter) : base("can") => _adapter = adapter;
    public override async Task OpenAsync(TransportEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResourceId = endpoint.ResourceId; SetState(TransportState.Opening); await CloseCoreAsync().ConfigureAwait(false);
            var settings = endpoint.Settings; var channel = Text(settings, "channel") ?? throw new InvalidDataException("PCAN 资源缺少 channel");
            var options = new CanBusOptions("pcan", _adapter.NormalizeChannel(channel), Number(settings, "bitrate", 500000), Bool(settings, "fd_enabled"), Number(settings, "data_bitrate", 2000000), Bool(settings, "listen_only"));
            try
            {
                _controller = _adapter.Open(options); _receiveCancellation = new CancellationTokenSource(); _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCancellation.Token)); SetState(TransportState.Open);
            }
            catch { _controller?.Dispose(); _controller = null; SetState(TransportState.Faulted); throw; }
        }
        finally { _gate.Release(); }
    }
    public override async Task CloseAsync(CancellationToken cancellationToken = default) { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); try { await CloseCoreAsync().ConfigureAwait(false); } finally { _gate.Release(); } }
    public async Task SendAsync(CanFrame frame, CancellationToken cancellationToken = default) { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); try { (_controller ?? throw new IOException("PCAN 总线尚未打开")).Send(frame); } finally { _gate.Release(); } }
    public async IAsyncEnumerable<CanFrame> ReceiveAsync(CanFrameFilter? filter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_controller is null) throw new IOException("PCAN 总线尚未打开");
        var channel = Channel.CreateUnbounded<CanFrame>(); lock (_subscriptionsSync) _subscriptions.Add(channel);
        try { await foreach (var frame in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) if (filter is null || filter.Matches(frame)) yield return frame; }
        finally { lock (_subscriptionsSync) _subscriptions.Remove(channel); channel.Writer.TryComplete(); }
    }
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_controller!.TryReceive(out var frame) && frame is not null) { Channel<CanFrame>[] subscribers; lock (_subscriptionsSync) subscribers = _subscriptions.ToArray(); foreach (var subscriber in subscribers) subscriber.Writer.TryWrite(frame); }
                else await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { SetState(TransportState.Faulted); lock (_subscriptionsSync) foreach (var subscriber in _subscriptions) subscriber.Writer.TryComplete(error); }
    }
    private async Task CloseCoreAsync()
    {
        _receiveCancellation?.Cancel(); if (_receiveTask is not null) { try { await _receiveTask.ConfigureAwait(false); } catch { } }
        _receiveCancellation?.Dispose(); _receiveCancellation = null; _receiveTask = null; _controller?.Dispose(); _controller = null;
        lock (_subscriptionsSync) { foreach (var subscriber in _subscriptions) subscriber.Writer.TryComplete(); _subscriptions.Clear(); }
        if (State != TransportState.Closed) SetState(TransportState.Closed);
    }
    public override async ValueTask DisposeAsync() { try { await CloseAsync().ConfigureAwait(false); } finally { _gate.Dispose(); } }
    private static string? Text(IReadOnlyDictionary<string, JsonElement> settings, string key) => settings.TryGetValue(key, out var value) ? value.ToString() : null;
    private static int Number(IReadOnlyDictionary<string, JsonElement> settings, string key, int fallback) => settings.TryGetValue(key, out var value) && int.TryParse(value.ToString(), out var result) && result > 0 ? result : fallback;
    private static bool Bool(IReadOnlyDictionary<string, JsonElement> settings, string key) => settings.TryGetValue(key, out var value) && (value.ValueKind == JsonValueKind.True || bool.TryParse(value.ToString(), out var result) && result);
}
