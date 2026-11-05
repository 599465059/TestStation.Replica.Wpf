using TestStation.Application.Hardware;

namespace TestStation.Infrastructure.Transports;

internal static class ByteResponseReader
{
    public static async Task<ReadOnlyMemory<byte>> ReadAsync(Stream stream, ByteResponseFraming framing,
        int maximumLength, CancellationToken token)
    {
        if (framing.HeaderLength <= 0 || framing.HeaderLength > maximumLength)
            throw new InvalidDataException("响应头长度超出传输限制");
        var header = new byte[framing.HeaderLength];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = framing.GetFrameLength(header);
        if (length < header.Length || length > maximumLength)
            throw new InvalidDataException("响应帧长度无效或超出传输限制");
        var frame = new byte[length];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(header.Length), token).ConfigureAwait(false);
        return frame;
    }
}
