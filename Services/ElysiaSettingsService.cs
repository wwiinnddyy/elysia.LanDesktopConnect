using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Elysia.LanDesktopConnect.Services;

public sealed class ElysiaSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public bool IsAutoPort { get; set; } = true;
    public int ManualPort { get; set; } = 34567;
    public bool AutoRestart { get; set; } = true;
    public string LogLevel { get; set; } = "info";
    public bool DebugMode { get; set; }

    public static ElysiaSettings Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
            return new ElysiaSettings();

        try
        {
            var json = File.ReadAllText(filePath).TrimStart('\uFEFF');
            return JsonSerializer.Deserialize<ElysiaSettings>(json, JsonOptions) ?? new ElysiaSettings();
        }
        catch
        {
            return new ElysiaSettings();
        }
    }

    public void Save(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(filePath, json);
        }
        catch
        {
        }
    }
}

public sealed class ElysiaSettingsService
{
    private readonly string _settingsPath;
    private ElysiaSettings _settings;
    private readonly object _lock = new();

    public event EventHandler<ElysiaSettings>? SettingsChanged;

    public ElysiaSettingsService(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _settingsPath = Path.Combine(dataDirectory, "settings.json");
        _settings = ElysiaSettings.Load(_settingsPath);
    }

    public ElysiaSettings GetSettings()
    {
        lock (_lock)
        {
            return _settings;
        }
    }

    public void UpdateSettings(Action<ElysiaSettings> updateAction)
    {
        ArgumentNullException.ThrowIfNull(updateAction);

        lock (_lock)
        {
            updateAction(_settings);
            _settings.Save(_settingsPath);
        }

        SettingsChanged?.Invoke(this, _settings);
    }

    public T GetValue<T>(string key, T defaultValue)
    {
        lock (_lock)
        {
            return key switch
            {
                "ipc.isAutoPort" when _settings.IsAutoPort is T v => v,
                "ipc.manualPort" when _settings.ManualPort is T v => v,
                "ipc.autoRestart" when _settings.AutoRestart is T v => v,
                "ipc.logLevel" when _settings.LogLevel is T v => v,
                "ipc.debugMode" when _settings.DebugMode is T v => v,
                _ => defaultValue
            };
        }
    }

    public void SetValue<T>(string key, T value)
    {
        UpdateSettings(s =>
        {
            switch (key)
            {
                case "ipc.isAutoPort" when value is bool b: s.IsAutoPort = b; break;
                case "ipc.manualPort" when value is int i: s.ManualPort = i; break;
                case "ipc.autoRestart" when value is bool b: s.AutoRestart = b; break;
                case "ipc.logLevel" when value is string str: s.LogLevel = str; break;
                case "ipc.debugMode" when value is bool b: s.DebugMode = b; break;
            }
        });
    }
}
