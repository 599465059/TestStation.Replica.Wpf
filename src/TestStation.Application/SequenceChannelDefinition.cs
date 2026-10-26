using System.Text.Json.Serialization;

namespace TestStation.Application;

/// <summary>A provider declares channel types independently of live values.</summary>
public sealed record SequenceChannelDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("data_type")] string DataType,
    [property: JsonPropertyName("source")] string Source = "模拟通道",
    [property: JsonPropertyName("can_read")] bool CanRead = true,
    [property: JsonPropertyName("can_write")] bool CanWrite = true);

/// <summary>Registration contract for future device/step plugins.</summary>
public interface ISequenceChannelProvider
{
    IReadOnlyList<SequenceChannelDefinition> GetChannels();
}
