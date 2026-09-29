using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Vfx;

namespace FusionRpg.Injector.Fx;

/// <summary>
/// Phase-1 bridge — vfx-ssot.md §13. The Funnel keeps presenting IDamageFxSink;
/// this adapter turns presents into cues for the director. Deleted when producers emit cues directly.
/// </summary>
public static class DamageFxCueAdapter
{
    public static readonly IDamageFxSink Sink = new Adapter();

    sealed class Adapter : IDamageFxSink
    {
        public void Show(DamageFxDto fx)
        {
            if (fx == null) return;
            VfxDirector.Play(VfxCueMapper.FromDamageFx(fx));
        }
    }
}
