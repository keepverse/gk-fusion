using System.Text.Json;
using System.Text.Json.Serialization;

namespace FusionRpg.Launcher.Services;

public sealed class LauncherSettings
{
    public string? GameFolder { get; set; }
    public int? LastPort { get; set; }
    /// <summary>Optional override for game profile (pvzrh-3.8.1 / pvzrh-3.9). Null = fingerprint detect.</summary>
    public string? GameProfile { get; set; }
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Global hotkey that toggles the game/web overlay (WPF Key name, e.g. "F10"). Null = F10.</summary>
    public string? OverlayHotKey { get; set; }

    /// <summary>User acknowledged unsigned hobby / AV false-positive risk.</summary>
    public bool TrustAcknowledged { get; set; }

    /// <summary>User successfully ran Prepare Windows Security (Defender exclusion).</summary>
    public bool WindowsSecurityPrepared { get; set; }

    /// <summary>
    /// When false, <see cref="Save()"/> is a no-op (unit tests). User loads set this true.
    /// </summary>
    [JsonIgnore]
    public bool PersistToUserStore { get; set; }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FusionRpg",
            "launcher.json");

    public static LauncherSettings Load() => Load(SettingsPath, persistToUserStore: true);

    public static LauncherSettings Load(string path) => Load(path, persistToUserStore: false);

    public static LauncherSettings Load(string path, bool persistToUserStore)
    {
        LauncherSettings settings;
        try
        {
            if (!File.Exists(path))
                settings = new LauncherSettings();
            else
            {
                var json = File.ReadAllText(path);
                settings = JsonSerializer.Deserialize<LauncherSettings>(json, JsonOpts) ?? new LauncherSettings();
            }
        }
        catch
        {
            settings = new LauncherSettings();
        }

        settings.PersistToUserStore = persistToUserStore;
        if (IsEphemeralTestPath(settings.GameFolder))
            settings.GameFolder = null;
        return settings;
    }

    public void Save()
    {
        if (!PersistToUserStore)
            return;
        Save(SettingsPath);
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>True for temp folders left by launcher unit tests (must not stick as the user's game path).</summary>
    public static bool IsEphemeralTestPath(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            var full = Path.GetFullPath(folder);
            var temp = Path.GetFullPath(Path.GetTempPath());
            if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
                return false;
            return full.Contains("FusionRpg", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
