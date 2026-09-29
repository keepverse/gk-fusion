namespace FusionRpg.Injector.Host;

/// <summary>Host-agnostic config — BepInEx Config.Bind or MelonPreferences / fusionrpg.cfg.</summary>
public interface IRpgConfig
{
    string ServerUrl { get; }

    /// <summary>
    /// True when <see cref="ServerUrl"/> is a DEFAULT rather than a configured value (no cfg key, no env
    /// override, or the built-in default). The injector warns loudly in that case: a pooled game -- its own
    /// slot, its own server port -- that silently falls back to the OWNER's :5088 is exactly the accident the
    /// pool exists to prevent (2026-09-22).
    /// </summary>
    bool ServerUrlFromFallback { get; }

    /// <summary>"launcher" (default) or "injector". FUSIONRPG_OVERLAY_HOST wins over this.</summary>
    string OverlayHost { get; }
    bool PersistCheats { get; }
    bool EnableUnsafeHitPatches { get; }
}
