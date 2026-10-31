using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TestStation.Infrastructure.Transports.Can;

/// <summary>
/// Describes how a vendor CAN adapter is deployed.  Bundled adapters are
/// resolved from the application directory; installed adapters are resolved
/// through the vendor's normal installation locations and PATH.
/// </summary>
public sealed record CanAdapterManifest(
    string Id,
    string DisplayName,
    string LoadMode,
    string Library,
    IReadOnlyList<string> InstalledNames,
    IReadOnlyList<string> SearchPaths)
{
    public bool IsBundled => LoadMode.Equals("bundled", StringComparison.OrdinalIgnoreCase);
}

public sealed record CanNativeLibraryStatus(
    string AdapterId,
    string LoadMode,
    string? ResolvedPath,
    bool IsLoaded,
    string? Error);

/// <summary>Loads native CAN libraries using an explicit, versioned adapter layout.</summary>
public static class CanNativeLibraryLoader
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, CanNativeLibraryStatus> Statuses = new(StringComparer.OrdinalIgnoreCase);

    public static void InstallPcanResolver()
    {
        // The resolver is installed once for the Infrastructure assembly.  It
        // is intentionally kept here instead of using the process DLL search
        // path, so a bundled vendor DLL cannot be shadowed by another version.
        try { NativeLibrary.SetDllImportResolver(typeof(CanNativeLibraryLoader).Assembly, Resolve); }
        catch (InvalidOperationException) { /* another adapter already installed it */ }
    }

    public static CanNativeLibraryStatus GetStatus(string adapterId)
    {
        lock (Sync)
        {
            return Statuses.TryGetValue(adapterId, out var status)
                ? status
                : ResolveStatus(adapterId, null, load: true);
        }
    }

    public static string? ResolvePath(string adapterId, string? nativeName = null)
    {
        lock (Sync) return ResolveStatus(adapterId, nativeName, load: false).ResolvedPath;
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals("PCANBasic.dll", StringComparison.OrdinalIgnoreCase)) return 0;
        lock (Sync)
        {
            var manifest = LoadManifest("pcan");
            var status = ResolveStatus("pcan", libraryName, load: true);
            if (status.IsLoaded) return NativeLibrary.Load(status.ResolvedPath!);

            // A bundled adapter must never silently fall back to a different
            // version found in PATH or System32. Installed adapters may use
            // the operating system's vendor search path as a last resort.
            if (manifest.IsBundled)
                throw new DllNotFoundException(status.Error ?? $"未找到 {manifest.Library}");

            if (NativeLibrary.TryLoad(libraryName, out var installedHandle))
            {
                SetStatus(status with { IsLoaded = true, ResolvedPath = libraryName, Error = null });
                return installedHandle;
            }
            return 0;
        }
    }

    private static CanNativeLibraryStatus ResolveStatus(string adapterId, string? nativeName, bool load)
    {
        var manifest = LoadManifest(adapterId);
        var candidates = CandidatePaths(manifest, nativeName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            if (!load)
            {
                var available = new CanNativeLibraryStatus(adapterId, manifest.LoadMode, candidate, false, null);
                SetStatus(available);
                return available;
            }

            try
            {
                // Loading by absolute path makes the selected adapter directory
                // deterministic while still allowing the native library to
                // resolve its own side-by-side dependencies.
                var handle = NativeLibrary.Load(candidate);
                NativeLibrary.Free(handle);
                var loaded = new CanNativeLibraryStatus(adapterId, manifest.LoadMode, candidate, true, null);
                SetStatus(loaded);
                return loaded;
            }
            catch (Exception error)
            {
                var failed = new CanNativeLibraryStatus(adapterId, manifest.LoadMode, candidate, false, error.Message);
                SetStatus(failed);
                return failed;
            }
        }

        var missing = new CanNativeLibraryStatus(adapterId, manifest.LoadMode, null, false,
            manifest.IsBundled
                ? $"未找到随软件发布的 {manifest.Library}"
                : $"未找到已安装的 {string.Join(", ", manifest.InstalledNames)}");
        SetStatus(missing);
        return missing;
    }

    private static IEnumerable<string> CandidatePaths(CanAdapterManifest manifest, string? nativeName)
    {
        var architecture = Environment.Is64BitProcess ? "x64" : "x86";
        var rootOverride = Environment.GetEnvironmentVariable("TESTSTATION_DRIVER_ROOT");
        var adapterRoot = Path.Combine(rootOverride ?? AppContext.BaseDirectory, "Drivers", "CAN", manifest.Id);

        if (manifest.IsBundled)
        {
            var relative = manifest.Library.Replace("/", Path.DirectorySeparatorChar.ToString());
            if (relative.StartsWith("x64" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("x86" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                relative = Path.Combine(architecture, relative[(relative.IndexOf(Path.DirectorySeparatorChar) + 1)..]);
            yield return Path.GetFullPath(Path.Combine(adapterRoot, relative));
            yield break;
        }

        var names = manifest.InstalledNames.Count == 0
            ? [nativeName ?? Path.GetFileName(manifest.Library)]
            : manifest.InstalledNames;
        foreach (var configured in manifest.SearchPaths)
        {
            var expanded = Environment.ExpandEnvironmentVariables(configured)
                .Replace("{arch}", architecture, StringComparison.OrdinalIgnoreCase);
            if (Path.IsPathRooted(expanded)) foreach (var name in names) yield return Path.Combine(expanded, name);
            else foreach (var name in names) yield return Path.Combine(adapterRoot, expanded, name);
        }

        foreach (var name in names)
        {
            yield return Path.Combine(AppContext.BaseDirectory, name);
            yield return name; // NativeLibrary.Load can ask the OS installed search path.
        }
    }

    private static CanAdapterManifest LoadManifest(string adapterId)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Drivers", "CAN", adapterId, "manifest.json");
        try
        {
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                return new CanAdapterManifest(
                    root.GetProperty("id").GetString() ?? adapterId,
                    root.GetProperty("displayName").GetString() ?? adapterId,
                    root.GetProperty("loadMode").GetString() ?? "installed",
                    root.GetProperty("library").GetString() ?? "",
                    ReadArray(root, "installedNames"), ReadArray(root, "searchPaths"));
            }
        }
        catch (Exception)
        {
            return new CanAdapterManifest(adapterId, adapterId, "installed", "", ["PCANBasic.dll"], []) with { };
        }

        return adapterId.Equals("pcan", StringComparison.OrdinalIgnoreCase)
            ? new CanAdapterManifest("pcan", "PEAK PCAN-Basic", "bundled", "x64/PCANBasic.dll", ["PCANBasic.dll"], [])
            : new CanAdapterManifest(adapterId, adapterId, "installed", "", [], []);
    }

    private static IReadOnlyList<string> ReadArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
            : [];

    private static void SetStatus(CanNativeLibraryStatus status) => Statuses[status.AdapterId] = status;
}
