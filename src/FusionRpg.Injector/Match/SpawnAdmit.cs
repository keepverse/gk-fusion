using FusionRpg.Core.Match;

namespace FusionRpg.Injector.Match;

/// <summary>
/// Injector Admit bridge (W2-B): gates our Create via MatchRuntime.TryAdmitSpawn.
/// Caps live on MatchRuntime; Config setter pushes ConfigureCaps.
/// </summary>
public static class SpawnAdmit
{
    static readonly object Gate = new();
    static CapPolicyConfig _config = CapPolicyConfig.Defaults();

    public static CapPolicyConfig Config
    {
        get
        {
            lock (Gate)
            {
                return new CapPolicyConfig
                {
                    MaxLivingPlants = _config.MaxLivingPlants,
                    MaxLivingZombies = _config.MaxLivingZombies,
                    MaxLivingBullets = _config.MaxLivingBullets
                };
            }
        }
        set
        {
            CapPolicyConfig copy;
            lock (Gate)
            {
                var src = value ?? CapPolicyConfig.Defaults();
                _config = new CapPolicyConfig
                {
                    MaxLivingPlants = src.MaxLivingPlants,
                    MaxLivingZombies = src.MaxLivingZombies,
                    MaxLivingBullets = src.MaxLivingBullets
                };
                copy = Config;
            }

            try { MatchHost.Runtime.ConfigureCaps(copy); } catch { }
        }
    }

    public static bool TryAdmit(string side, out GateResult result)
    {
        EnsureMatchReadyForDebugSpawn();

        CapPolicyConfig cfg;
        lock (Gate)
        {
            cfg = new CapPolicyConfig
            {
                MaxLivingPlants = _config.MaxLivingPlants,
                MaxLivingZombies = _config.MaxLivingZombies,
                MaxLivingBullets = _config.MaxLivingBullets
            };
        }

        // Keep MatchRuntime Caps aligned before Admit.
        try { MatchHost.Runtime.ConfigureCaps(cfg); } catch { }

        var ok = MatchHost.TryAdmitSpawn(side, out result);
        if (ok) return true;

        var snap = MatchHost.Runtime.ToSnapshot();
        CheatState.Note(
            $"spawn.admit reject side={side} reason={result.Reason} plants={snap.PlantCount} zombies={snap.ZombieCount}");
        try
        {
            DebugRuntime.Emit("debug.run.cap", new Dictionary<string, object>
            {
                ["side"] = side ?? "",
                ["reason"] = result.Reason,
                ["plantCount"] = snap.PlantCount,
                ["zombieCount"] = snap.ZombieCount,
                ["bulletCount"] = snap.BulletCount,
                ["maxPlants"] = cfg.MaxLivingPlants,
                ["maxZombies"] = cfg.MaxLivingZombies,
                ["maxBullets"] = cfg.MaxLivingBullets,
                ["matchKey"] = GameHooks.MatchKey ?? "",
                ["phase"] = snap.Phase.ToString()
            });
        }
        catch { }

        return false;
    }

    /// <summary>
    /// Debug Create can run while Unity <see cref="GameHooks.Board"/> is live but MatchRuntime
    /// was left Idle (missed board.start fold, or board.end without Board clear). Re-sync so Admit works.
    /// </summary>
    public static void EnsureMatchReadyForDebugSpawn()
    {
        try
        {
            if (GameHooks.Board == null) return;

            var phase = MatchHost.Runtime.Phase;
            if (phase == MatchPhase.InMatch) return;

            if (phase == MatchPhase.Paused)
            {
                MatchHost.NotifyPaused(false);
                return;
            }

            var key = GameHooks.MatchKey;
            if (string.IsNullOrWhiteSpace(key))
                key = Guid.NewGuid().ToString();

            MatchHost.Apply("board.start", new Dictionary<string, object>
            {
                ["matchKey"] = key
            });
        }
        catch (Exception ex)
        {
            try { CheatState.Error("SpawnAdmit.EnsureMatchReady: " + ex.Message); } catch { }
        }
    }
}
