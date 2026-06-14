using System.Text.Json;

namespace macViz;

internal static class CameraSourceRegistry
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly List<int> AvailableDeviceIndices = [];
    private static readonly HashSet<int> EnabledDeviceIndices = [];
    private static readonly Dictionary<int, CameraInput> SharedInputs = [];

    private static bool _initialized;
    private static string _settingsPath = "camera-sources.json";

    public static void Initialize(string settingsPath = "camera-sources.json", bool warmupEnabledSources = true)
    {
        lock (Sync)
        {
            _settingsPath = settingsPath;
            if (_initialized)
            {
                return;
            }

            RefreshAvailableDevicesNoLock();
            LoadSettingsNoLock();
            ReconcileEnabledSetNoLock();

            Console.WriteLine($"[Camera] Available devices: {FormatIndices(AvailableDeviceIndices)}");
            Console.WriteLine($"[Camera] Enabled devices:   {FormatIndices(EnabledDeviceIndices.OrderBy(x => x))}");

            if (warmupEnabledSources)
            {
                WarmupEnabledInputsNoLock();
            }

            _initialized = true;
        }
    }

    public static void RefreshDevicesAndWarmup()
    {
        EnsureInitialized();

        lock (Sync)
        {
            RefreshAvailableDevicesNoLock();
            ReconcileEnabledSetNoLock();
            DisposeUnavailableOrDisabledInputsNoLock();
            WarmupEnabledInputsNoLock();
            SaveSettingsNoLock();
        }
    }

    public static IReadOnlyList<int> GetAvailableDeviceIndices()
    {
        EnsureInitialized();
        lock (Sync)
        {
            return [.. AvailableDeviceIndices];
        }
    }

    public static IReadOnlyList<int> GetEnabledAvailableDeviceIndices()
    {
        EnsureInitialized();
        lock (Sync)
        {
            return [.. AvailableDeviceIndices.Where(EnabledDeviceIndices.Contains)];
        }
    }

    public static bool IsDeviceEnabled(int deviceIndex)
    {
        EnsureInitialized();
        lock (Sync)
        {
            return EnabledDeviceIndices.Contains(deviceIndex);
        }
    }

    public static void SetDeviceEnabled(int deviceIndex, bool enabled)
    {
        EnsureInitialized();

        lock (Sync)
        {
            if (enabled)
            {
                EnabledDeviceIndices.Add(deviceIndex);
            }
            else
            {
                EnabledDeviceIndices.Remove(deviceIndex);
                if (SharedInputs.Remove(deviceIndex, out var input))
                {
                    input.Dispose();
                }
            }

            if (enabled)
            {
                _ = TryWarmupDeviceNoLock(deviceIndex, out _);
            }

            SaveSettingsNoLock();
        }
    }

    public static bool TryResolveDevice(int preferredDeviceIndex, out int resolvedDeviceIndex, out string? warning)
    {
        EnsureInitialized();

        lock (Sync)
        {
            var eligible = AvailableDeviceIndices.Where(EnabledDeviceIndices.Contains).ToList();
            if (eligible.Count == 0)
            {
                resolvedDeviceIndex = -1;
                warning = "No enabled camera devices are available.";
                return false;
            }

            if (eligible.Contains(preferredDeviceIndex))
            {
                resolvedDeviceIndex = preferredDeviceIndex;
                warning = null;
                return true;
            }

            resolvedDeviceIndex = eligible[0];

            var unavailableReason = AvailableDeviceIndices.Contains(preferredDeviceIndex)
                ? $"device {preferredDeviceIndex} is disabled"
                : $"device {preferredDeviceIndex} is unavailable";
            warning = $"Preset requested {unavailableReason}; using device {resolvedDeviceIndex}.";
            return true;
        }
    }

    public static bool TryGetOrCreateInput(int deviceIndex, out CameraInput? input, out string? error)
    {
        EnsureInitialized();

        lock (Sync)
        {
            input = null;
            error = null;

            if (!AvailableDeviceIndices.Contains(deviceIndex))
            {
                error = $"Device {deviceIndex} is not available.";
                return false;
            }

            if (!EnabledDeviceIndices.Contains(deviceIndex))
            {
                error = $"Device {deviceIndex} is disabled in settings.";
                return false;
            }

            if (SharedInputs.TryGetValue(deviceIndex, out var existing))
            {
                input = existing;
                return true;
            }

            try
            {
                var created = new CameraInput(deviceIndex);
                SharedInputs[deviceIndex] = created;
                input = created;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    public static void DisposeAll()
    {
        lock (Sync)
        {
            foreach (var source in SharedInputs.Values)
            {
                source.Dispose();
            }

            SharedInputs.Clear();
            _initialized = false;
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        Initialize();
    }

    private static void RefreshAvailableDevicesNoLock()
    {
        AvailableDeviceIndices.Clear();
        AvailableDeviceIndices.AddRange(CameraInput.EnumerateDeviceIndices());
    }

    private static void LoadSettingsNoLock()
    {
        if (!File.Exists(_settingsPath))
        {
            EnabledDeviceIndices.Clear();
            foreach (var device in AvailableDeviceIndices)
            {
                EnabledDeviceIndices.Add(device);
            }

            SaveSettingsNoLock();
            return;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<CameraSourceSettings>(json) ?? new CameraSourceSettings();

            EnabledDeviceIndices.Clear();
            foreach (var deviceIndex in settings.EnabledDeviceIndices)
            {
                EnabledDeviceIndices.Add(deviceIndex);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Camera] Failed to load settings '{_settingsPath}': {ex.Message}");
            EnabledDeviceIndices.Clear();
            foreach (var device in AvailableDeviceIndices)
            {
                EnabledDeviceIndices.Add(device);
            }
        }
    }

    private static void SaveSettingsNoLock()
    {
        try
        {
            var settings = new CameraSourceSettings
            {
                EnabledDeviceIndices = [.. EnabledDeviceIndices.OrderBy(x => x)]
            };
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Camera] Failed to save settings '{_settingsPath}': {ex.Message}");
        }
    }

    private static void ReconcileEnabledSetNoLock()
    {
        EnabledDeviceIndices.RemoveWhere(index => index < 0);
    }

    private static void DisposeUnavailableOrDisabledInputsNoLock()
    {
        var liveKeys = SharedInputs.Keys.ToList();
        foreach (var deviceIndex in liveKeys)
        {
            if (AvailableDeviceIndices.Contains(deviceIndex) && EnabledDeviceIndices.Contains(deviceIndex))
            {
                continue;
            }

            if (SharedInputs.Remove(deviceIndex, out var input))
            {
                input.Dispose();
            }
        }
    }

    private static void WarmupEnabledInputsNoLock()
    {
        foreach (var deviceIndex in AvailableDeviceIndices)
        {
            if (!EnabledDeviceIndices.Contains(deviceIndex))
            {
                continue;
            }

            _ = TryWarmupDeviceNoLock(deviceIndex, out var error);
            if (!string.IsNullOrWhiteSpace(error))
            {
                Console.WriteLine($"[Camera] Warmup failed for device {deviceIndex}: {error}");
            }
        }
    }

    private static bool TryWarmupDeviceNoLock(int deviceIndex, out string? error)
    {
        error = null;

        if (!AvailableDeviceIndices.Contains(deviceIndex) || !EnabledDeviceIndices.Contains(deviceIndex))
        {
            return false;
        }

        if (SharedInputs.ContainsKey(deviceIndex))
        {
            return true;
        }

        try
        {
            SharedInputs[deviceIndex] = new CameraInput(deviceIndex);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string FormatIndices(IEnumerable<int> indices)
    {
        var materialized = indices.ToArray();
        return materialized.Length == 0 ? "(none)" : string.Join(", ", materialized);
    }
}
