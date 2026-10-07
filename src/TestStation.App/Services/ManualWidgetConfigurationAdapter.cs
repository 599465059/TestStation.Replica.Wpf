using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Industrial.Controls.Instruments;
using TestStation.App.ViewModels;

namespace TestStation.App.Services;

/// <summary>Adapts the opaque canvas configuration snapshot to a library control.</summary>
public sealed class ManualWidgetConfigurationAdapter
{
    public void Apply(InstrumentControl control, ManualWidgetViewModel widget)
    {
        var settings = control.ExportSettings();
        InstrumentFiles.ConfigurationFile? stored = null;
        if (widget.Model.ConfigurationJson is { Length: > 2 })
        {
            try
            {
                stored = JsonSerializer.Deserialize<InstrumentFiles.ConfigurationFile>(widget.Model.ConfigurationJson);
                if (stored is not null && stored.Type == control.GetType().Name)
                    settings = stored.Settings;
            }
            catch (JsonException) { /* invalid snapshots use library defaults */ }
        }

        settings.Name = widget.Name;
        settings.Channel = string.IsNullOrWhiteSpace(settings.Channel) ? widget.Channel : settings.Channel;
        settings.Unit = widget.Unit;
        control.ApplySettings(settings);
        ApplyStoredProperties(control, stored?.Properties);
    }

    public void ApplyStoredProperties(InstrumentControl control, Dictionary<string, string>? values)
    {
        if (values is null) return;
        foreach (var (key, value) in values)
        {
            try
            {
                if (control is SerialDataDisplay serial && key == "EncodingName") { serial.SetEncoding(value); continue; }
                if (control is SerialDataDisplay serialMode && key == "DisplayMode") { serialMode.SetDisplayMode(Enum.Parse<SerialDisplayMode>(value)); continue; }
                var property = control.GetType().GetProperty(key, BindingFlags.Instance | BindingFlags.Public);
                if (property is null || !property.CanWrite) continue;
                object converted = property.PropertyType.IsEnum
                    ? Enum.Parse(property.PropertyType, value)
                    : Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture)!;
                property.SetValue(control, converted);
            }
            catch (Exception) { /* optional library properties are best effort */ }
        }
    }

    public string Capture(InstrumentControl control) => InstrumentFiles.SerializeConfiguration(control);
}
