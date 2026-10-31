using System.Globalization;
using System.Resources;

namespace TestStation.SimulatedPlugins;

internal static class DeviceText
{
    private static readonly ResourceManager Resources = new("TestStation.SimulatedPlugins.DeviceText", typeof(DeviceText).Assembly);
    public static string Get(string key) => Resources.GetString(key, CultureInfo.InvariantCulture)
        ?? throw new MissingManifestResourceException($"Missing device text: {key}");
    public static string Format(string key, params object?[] values) => string.Format(CultureInfo.CurrentCulture, Get(key), values);
}
