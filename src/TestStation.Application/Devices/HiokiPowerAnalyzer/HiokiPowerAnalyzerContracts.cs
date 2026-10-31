using System.Text.Json;
using TestStation.Application.Drivers;
using TestStation.Domain.Resources;

namespace TestStation.Application.Devices.HiokiPowerAnalyzer;

public sealed record HiokiPowerAnalyzerConnection(bool Connected = false, string Identity = "", string Error = "");

public sealed record HiokiPowerAnalyzerChannel(
    string Id,
    string DisplayName,
    string Unit,
    int Channel,
    int ValueIndex);

public sealed record HiokiPowerAnalyzerModel(
    string Id = "PW6001",
    string Name = "HIOKI PW6001",
    int ChannelCount = 6,
    bool Tcp = true,
    bool Serial = false);

public interface IHiokiPowerAnalyzerDeviceService : IAutomationChannelBackend, ISequenceChannelChangeSource
{
    IReadOnlyList<DeviceDefinition> Devices { get; }
    IReadOnlyList<TransportResource> Resources { get; }
    bool HasProject { get; }
    bool FamilyEnabled { get; set; }
    event EventHandler? StateChanged;
    event EventHandler<ChannelRuntimeValue>? ValueChanged;
    HiokiPowerAnalyzerConnection ConnectionFor(string deviceId);
    Task ConnectAsync(string deviceId, CancellationToken token = default);
    Task DisconnectDeviceAsync(string deviceId, CancellationToken token = default);
    DeviceDefinition AddDevice(string name);
    void UpdateDevice(string id, string name, string modelId, string resourceId);
    void RemoveDevice(string id);
    void SaveResource(TransportResource resource);
}

public static class HiokiPowerAnalyzerCatalog
{
    public const string FamilyId = "hioki.pw6001";
    public static HiokiPowerAnalyzerModel Model { get; } = new();
    public static IReadOnlyList<HiokiPowerAnalyzerChannel> Channels { get; } =
        Enumerable.Range(1, 6).SelectMany(channel => new[]
        {
            new HiokiPowerAnalyzerChannel($"CH{channel}.VoltageRms", $"通道 {channel} 电压 RMS", "V", channel, 0),
            new HiokiPowerAnalyzerChannel($"CH{channel}.CurrentRms", $"通道 {channel} 电流 RMS", "A", channel, 1),
            new HiokiPowerAnalyzerChannel($"CH{channel}.ActivePower", $"通道 {channel} 有功功率", "W", channel, 2),
            new HiokiPowerAnalyzerChannel($"CH{channel}.ApparentPower", $"通道 {channel} 视在功率", "VA", channel, 3),
            new HiokiPowerAnalyzerChannel($"CH{channel}.ReactivePower", $"通道 {channel} 无功功率", "var", channel, 4),
            new HiokiPowerAnalyzerChannel($"CH{channel}.PowerFactor", $"通道 {channel} 功率因数", "", channel, 5),
            new HiokiPowerAnalyzerChannel($"CH{channel}.Frequency", $"通道 {channel} 频率", "Hz", channel, 7),
            new HiokiPowerAnalyzerChannel($"CH{channel}.Phase", $"通道 {channel} 相位", "deg", channel, 6)
        }).ToArray();
    public static string Query(int channel) => channel is >= 1 and <= 6
        ? $":MEASure? Urms{channel},Irms{channel},P{channel},S{channel},Q{channel},PF{channel},DEG{channel},FREQ{channel}"
        : throw new ArgumentOutOfRangeException(nameof(channel), "PW6001 通道范围为 1 到 6");
    public static double[] Parse(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) throw new FormatException("PW6001 测量响应为空");
        var values = response.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(item => !item.StartsWith("Status", StringComparison.OrdinalIgnoreCase))
            .Select(ParseValue).ToArray();
        if (values.Length < 8) throw new FormatException($"PW6001 测量响应需要 8 个数值，实际收到 {values.Length}");
        return values[^8..];
    }
    private static double ParseValue(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("NAN", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("O.R.", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("S.Err", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("-----", StringComparison.OrdinalIgnoreCase)) return double.NaN;
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)) return value;
        throw new FormatException($"PW6001 响应包含无法解析的数值：{text}");
    }
}
