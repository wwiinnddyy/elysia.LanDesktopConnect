using System.Text.Json;
using LanMountainDesktop.PluginSdk;

namespace Elysia.LanDesktopConnect.Services;

public sealed class ElysiaSettings
{
    public bool IsAutoPort { get; set; } = true;

    public int ManualPort { get; set; } = 34567;

    public bool AutoRestart { get; set; } = true;

    public string LogLevel { get; set; } = "info";

    public bool DebugMode { get; set; }

    public bool LegacySettingsImported { get; set; }

    public ElysiaSettings Clone() => new()
    {
        IsAutoPort = IsAutoPort,
        ManualPort = ManualPort,
        AutoRestart = AutoRestart,
        LogLevel = LogLevel,
        DebugMode = DebugMode,
        LegacySettingsImported = LegacySettingsImported
    };
}

public sealed class ElysiaSettingsService
{
    public const string SectionId = "IpcBridgeSettingsSection";

    private static readonly JsonSerializerOptions LegacyJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ISettingsService _settingsService;
    private readonly IPluginRuntimeContext _runtimeContext;
    private readonly object _syncRoot = new();
    private ElysiaSettings _settings;

    public ElysiaSettingsService(
        ISettingsService settingsService,
        IPluginRuntimeContext runtimeContext)
    {
        _settingsService = settingsService;
        _runtimeContext = runtimeContext;
        Directory.CreateDirectory(runtimeContext.DataDirectory);

        _settings = LoadFromHost();
        ImportLegacySettingsIfRequired();
    }

    public event EventHandler<ElysiaSettings>? SettingsChanged;

    public ElysiaSettings GetSettings()
    {
        lock (_syncRoot)
        {
            return _settings.Clone();
        }
    }

    public T GetValue<T>(string key, T defaultValue)
    {
        lock (_syncRoot)
        {
            object? value = key switch
            {
                "ipc.isAutoPort" => _settings.IsAutoPort,
                "ipc.manualPort" => _settings.ManualPort,
                "ipc.autoRestart" => _settings.AutoRestart,
                "ipc.logLevel" => _settings.LogLevel,
                "ipc.debugMode" => _settings.DebugMode,
                _ => null
            };

            return value is T typedValue ? typedValue : defaultValue;
        }
    }

    public void UpdateSettings(Action<ElysiaSettings> updateAction, params string[] changedKeys)
    {
        ArgumentNullException.ThrowIfNull(updateAction);

        ElysiaSettings snapshot;
        lock (_syncRoot)
        {
            updateAction(_settings);
            Normalize(_settings);
            snapshot = _settings.Clone();
        }

        SaveToHost(snapshot, changedKeys);
        SettingsChanged?.Invoke(this, snapshot.Clone());
    }

    private ElysiaSettings LoadFromHost()
    {
        var settings = _settingsService.LoadSection<ElysiaSettings>(
            SettingsScope.Plugin,
            _runtimeContext.Manifest.Id,
            SectionId);
        Normalize(settings);
        return settings;
    }

    private void SaveToHost(ElysiaSettings settings, IReadOnlyCollection<string>? changedKeys = null)
    {
        _settingsService.SaveSection(
            SettingsScope.Plugin,
            _runtimeContext.Manifest.Id,
            SectionId,
            settings,
            changedKeys: changedKeys);
    }

    private void ImportLegacySettingsIfRequired()
    {
        if (_settings.LegacySettingsImported)
        {
            return;
        }

        var legacyPath = Path.Combine(_runtimeContext.DataDirectory, "settings.json");
        if (File.Exists(legacyPath))
        {
            try
            {
                var legacyJson = File.ReadAllText(legacyPath).TrimStart('\uFEFF');
                var legacy = JsonSerializer.Deserialize<ElysiaSettings>(legacyJson, LegacyJsonOptions);
                if (legacy is not null)
                {
                    _settings.IsAutoPort = legacy.IsAutoPort;
                    _settings.ManualPort = legacy.ManualPort;
                    _settings.AutoRestart = legacy.AutoRestart;
                    _settings.LogLevel = legacy.LogLevel;
                    _settings.DebugMode = legacy.DebugMode;
                }
            }
            catch
            {
                // A damaged v4 settings file must not prevent the v5 plugin from loading.
            }
        }

        _settings.LegacySettingsImported = true;
        Normalize(_settings);
        SaveToHost(_settings.Clone(),
        [
            nameof(ElysiaSettings.IsAutoPort),
            nameof(ElysiaSettings.ManualPort),
            nameof(ElysiaSettings.AutoRestart),
            nameof(ElysiaSettings.LogLevel),
            nameof(ElysiaSettings.DebugMode),
            nameof(ElysiaSettings.LegacySettingsImported)
        ]);
    }

    private static void Normalize(ElysiaSettings settings)
    {
        settings.ManualPort = Math.Clamp(settings.ManualPort, 1024, 65535);
        settings.LogLevel = settings.LogLevel?.Trim().ToLowerInvariant() switch
        {
            "debug" => "debug",
            "warn" => "warn",
            "error" => "error",
            _ => "info"
        };
    }
}
