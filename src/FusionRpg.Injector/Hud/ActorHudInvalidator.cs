using FusionRpg.Core.Combat;
using FusionRpg.Core.Match;
using FusionRpg.Injector.Effects;
using FusionRpg.Injector.Match;

namespace FusionRpg.Injector.Hud;

/// <summary>Wires ActorHudCache invalidation to Hot lifecycle and combat events.</summary>
public static class ActorHudInvalidator
{
    static bool _installed;
    static Action<ScopeMembershipEvent>? _membershipHandler;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        try { EffectRuntime.Ensure(); } catch { /* host may retry */ }

        try
        {
            var status = EffectRuntime.Status;
            var prevApplied = status.OnApplied;
            status.OnApplied = inst =>
            {
                try { ActorHudCache.MarkDirty(inst.HostPtr); } catch { }
                prevApplied?.Invoke(inst);
            };

            var prevEnded = status.OnEnded;
            status.OnEnded = inst =>
            {
                try { ActorHudCache.MarkDirty(inst.HostPtr); } catch { }
                prevEnded?.Invoke(inst);
            };
        }
        catch { }

        _membershipHandler = e =>
        {
            try { ActorHudCache.MarkDirty(e.Ptr); } catch { }
        };

        try { MatchHost.Runtime.MembershipChanged += _membershipHandler; } catch { }

        ActorHudCache.Build = ActorHudBuilder.Build;

        ActorHudCache.DeltaEmit = (ptr, actorHud) =>
        {
            try
            {
                DebugRuntime.Emit("debug.actor-hud", new Dictionary<string, object>
                {
                    ["ptr"] = ptr,
                    ["actorHud"] = actorHud,
                });
            }
            catch { }
        };
    }

    public static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;

        if (_membershipHandler != null)
        {
            try { MatchHost.Runtime.MembershipChanged -= _membershipHandler; } catch { }
            _membershipHandler = null;
        }
    }

    internal static void MarkDirtyFromOwnerKey(string? ownerKey)
    {
        if (string.IsNullOrWhiteSpace(ownerKey)) return;
        var ptr = ownerKey.StartsWith("entity:", StringComparison.Ordinal)
            ? ownerKey.Substring("entity:".Length)
            : ownerKey;
        ActorHudCache.MarkDirty(CombatPtr.Normalize(ptr));
    }
}
