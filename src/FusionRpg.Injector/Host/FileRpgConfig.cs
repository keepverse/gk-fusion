namespace FusionRpg.Injector.Host;

/// <summary>Simple key=value config beside the MelonMod DLL (fusionrpg.cfg).</summary>
public sealed class FileRpgConfig : IRpgConfig
{
    /// <summary>Default when cfg missing — kept local so unit tests need no Harmony/Unity.</summary>
    public const string FallbackServerUrl = "http://127.0.0.1:5088";

    public string ServerUrl { get; private set; }
    public bool ServerUrlFromFallback { get; private set; }
    public string OverlayHost { get; private set; }
    public bool PersistCheats { get; private set; }
    public bool EnableUnsafeHitPatches { get; private set; }

    public FileRpgConfig(string cfgPath)
    {
        ServerUrl = FallbackServerUrl;
        ServerUrlFromFallback = true;
        OverlayHost = "launcher";
        PersistCheats = false;
        EnableUnsafeHitPatches = false;

        if (!File.Exists(cfgPath)) return;

        foreach (var raw in File.ReadAllLines(cfgPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (key.Equals("ServerUrl", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(val)) { ServerUrl = FallbackServerUrl; ServerUrlFromFallback = true; }
                else { ServerUrl = val; ServerUrlFromFallback = false; }
            }
            else if (key.Equals("OverlayHost", StringComparison.OrdinalIgnoreCase))
                OverlayHost = val;
            else if (key.Equals("PersistCheats", StringComparison.OrdinalIgnoreCase)
                     && bool.TryParse(val, out var p))
                PersistCheats = p;
            else if (key.Equals("EnableUnsafeHitPatches", StringComparison.OrdinalIgnoreCase)
                     && bool.TryParse(val, out var u))
                EnableUnsafeHitPatches = u;
        }
    }
}
