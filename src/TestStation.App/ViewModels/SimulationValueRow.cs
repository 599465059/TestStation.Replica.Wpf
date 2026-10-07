using System.Text.Json;

namespace TestStation.App.ViewModels;

public sealed record SimulationValueRow(string Channel, JsonElement Value)
{
    public string DisplayValue => Value.GetRawText();
}
