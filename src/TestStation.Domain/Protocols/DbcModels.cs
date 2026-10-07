namespace TestStation.Domain.Protocols;

public sealed record DbcSignal(string Name, int StartBit, int Length, bool LittleEndian, bool Signed,
    double Factor, double Offset, double? Minimum, double? Maximum, string Unit, string[] Receivers)
{
    public ulong Extract(ReadOnlySpan<byte> payload)
    {
        if (Length is < 1 or > 64 || StartBit < 0) throw new ArgumentException($"信号 {Name} 位范围无效");
        ulong raw = 0;
        var bit = StartBit;
        for (var index = 0; index < Length; index++)
        {
            if (bit / 8 >= payload.Length) throw new ArgumentException($"信号 {Name} 超出输入报文长度");
            var set = (payload[bit / 8] >> (bit % 8)) & 1;
            if (LittleEndian) { raw |= (ulong)set << index; bit++; }
            else { raw = (raw << 1) | (uint)set; bit = bit % 8 == 0 ? bit + 15 : bit - 1; }
        }
        return raw;
    }

    public double Decode(ReadOnlySpan<byte> payload)
    {
        var raw = Extract(payload);
        var value = Signed && (raw & (1UL << (Length - 1))) != 0
            ? Length == 64 ? unchecked((long)raw) : (double)raw - Math.Pow(2, Length)
            : (double)raw;
        return value * Factor + Offset;
    }

    public void Encode(double value, Span<byte> payload)
    {
        if (!double.IsFinite(value) || Factor == 0 || !double.IsFinite(Factor)) throw new ArgumentException($"信号 {Name} 值或比例无效");
        if (Minimum is { } minimum && value < minimum || Maximum is { } maximum && value > maximum) throw new ArgumentException($"信号 {Name} 超出量程");
        var raw = Math.Round((value - Offset) / Factor, MidpointRounding.ToEven);
        var min = Signed ? -Math.Pow(2, Length - 1) : 0;
        var exclusiveMax = Math.Pow(2, Signed ? Length - 1 : Length);
        if (raw < min || raw >= exclusiveMax) throw new ArgumentException($"信号 {Name} 超出编码范围");
        // Validate the complete bit range before touching the caller's buffer.
        _ = Extract(payload);
        var bits = raw < 0 ? unchecked((ulong)(long)raw) : (ulong)raw;
        var bit = StartBit;
        for (var index = 0; index < Length; index++)
        {
            var shift = LittleEndian ? index : Length - index - 1;
            var mask = (byte)(1 << (bit % 8));
            payload[bit / 8] = (byte)((payload[bit / 8] & ~mask) | (((bits >> shift) & 1) != 0 ? mask : 0));
            bit = LittleEndian ? bit + 1 : bit % 8 == 0 ? bit + 15 : bit - 1;
        }
    }
}

public sealed record DbcMessage(uint Id, string Name, int Length, string Transmitter, IReadOnlyList<DbcSignal> Signals);
public sealed record DbcDatabase(string Path, IReadOnlyList<DbcMessage> Messages, IReadOnlyList<string> Warnings);
