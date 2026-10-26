using TestStation.Domain.Can;

namespace TestStation.Application.Can;

public interface ICanSimulationService
{
    event EventHandler<CanMessageSnapshot>? MessageReceived;
    IReadOnlyList<CanSignalValue> Signals { get; }
    IReadOnlyList<CanMessageSnapshot> Messages { get; }
    void Start();
    void Stop();
    void Send(string id, int dlc, string data);
}

public sealed class SimulatedCanService : ICanSimulationService, IDisposable
{
    private readonly List<CanSignalValue> _signals = [];
    private readonly List<CanMessageSnapshot> _messages = [];
    private readonly System.Timers.Timer _timer = new(250);
    private double _angle;
    public event EventHandler<CanMessageSnapshot>? MessageReceived;
    public IReadOnlyList<CanSignalValue> Signals => _signals;
    public IReadOnlyList<CanMessageSnapshot> Messages => _messages;
    public SimulatedCanService() { _timer.Elapsed += (_, _) => PublishSample(); PublishSample(); }
    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
    public void Send(string id, int dlc, string data)
    {
        var item = new CanMessageSnapshot(id.Trim(), "手动发送", Math.Clamp(dlc, 0, 64), data.Trim(), DateTimeOffset.Now);
        _messages.Insert(0, item); if (_messages.Count > 100) _messages.RemoveAt(_messages.Count - 1); MessageReceived?.Invoke(this, item);
    }
    private void PublishSample()
    {
        _angle += .12; var now = DateTimeOffset.Now; var speed = 1200 + Math.Sin(_angle) * 80;
        _signals.Clear();
        _signals.Add(new("Axis1_Cmd_MsgCnt", "", (120 + Math.Sin(_angle) * .5).ToString("F3"), now));
        _signals.Add(new("Axis1_Cmd_JointAngle", "deg", (Math.Sin(_angle) * 30).ToString("F3"), now));
        _signals.Add(new("Axis1_Cmd_MotionTime", "ms", speed.ToString("F0"), now));
        var message = new CanMessageSnapshot("0x011", "Axis1_Cmd", 8, "00 F9 15 84 03 00 00 00", now);
        _messages.Insert(0, message); if (_messages.Count > 100) _messages.RemoveAt(_messages.Count - 1); MessageReceived?.Invoke(this, message);
    }
    public void Dispose() => _timer.Dispose();
}
