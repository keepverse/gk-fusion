using FusionRpg.Core.Effects;
using FusionRpg.Injector.Hud;

namespace FusionRpg.Injector.Effects;

/// <summary>
/// E41 (spec-ui-attach-point.md §2b): the live <see cref="IUiPresentSink"/> — wired onto
/// <c>EffectRuntime.Bag.UiPresent</c> the same way <c>DamageFxCueAdapter.Sink</c> is wired onto the
/// Funnel's own <c>IDamageFxSink</c>.
///
/// <para><c>SetMeter</c> is the real, live path: write the override, mark the ptr dirty so
/// <c>ActorHudCache</c>'s existing observe/delta machinery picks it up on the next read — no new
/// polling loop, no second HUD (§3's own "do not build a second HUD" rule).</para>
///
/// <para><c>ShowBanner</c> is a debug-telemetry placeholder, not a Unity present: no in-game banner
/// renderer exists in this tree yet (§2b.1's own "criteria-stated task" — whether the HUD renderer can
/// resolve a catalog key at all — needs the injector build and an owner-run lawn look, explicitly not
/// this module's to attempt). Emitting it as a `debug.effect.*`-shaped event keeps the call live and
/// observable without inventing a Unity-side consumer this session cannot verify.</para>
/// </summary>
public sealed class InjectorUiPresentSink : IUiPresentSink
{
    public static readonly InjectorUiPresentSink Instance = new();

    public void SetMeter(string targetPtr, string meterId, double ratio)
    {
        ActorHudMeterOverride.Set(targetPtr, meterId, ratio);
        ActorHudCache.MarkDirty(targetPtr);
    }

    public void ShowBanner(string bannerId, int? durationMs)
    {
        DebugRuntime.Emit("pvz.ui.banner", new Dictionary<string, object>
        {
            ["bannerId"] = bannerId,
            ["durationMs"] = durationMs ?? 0,
        });
    }
}
