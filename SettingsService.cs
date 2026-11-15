using System.Text.Json;

namespace Win11ProSuite.Services;

/// <summary>Central place for on-disk locations used by the suite.</summary>
public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Win11ProSuite");
            Directory.CreateDirectory(path);
            return path;
        }
    }
}

public sealed class AppSettings
{
    public bool TurboEnabled { get; set; }
    public bool PillEnabled { get; set; }
    public bool DockEnabled { get; set; }

    /// <summary>The user's original SPI "UI effects" state, captured the first time a backup is taken.</summary>
    public bool? OriginalUiEffects { get; set; }
}

public sealed class SettingsService
{
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string SettingsPath => Path.Combine(AppPaths.DataDirectory, "settings.json");

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
                }
            }
            catch
            {
                // Corrupt settings: fall through to defaults.
            }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            try
            {
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
            }
            catch
            {
                // Settings are a convenience; never crash because of them.
            }
        }
    }
}
