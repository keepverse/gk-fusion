using FusionRpg.Core.Vfx;
using UnityEngine;

namespace FusionRpg.Injector.Fx;

/// <summary>
/// Sustained sprite tints — vfx-v3 M4 (SPEC §3 rules). One layer stack per SpriteRenderer:
/// base captured at the first custom tint, composite via pure VfxTintMath, restored when the
/// stack empties. A periodic re-assert detects external color writes (vanilla hurt-flash,
/// transient Flash restore) and ADOPTS them as the new base — vanilla always wins the base,
/// we only shade it. Never renderer.material (§8.5 ban).
/// </summary>
static class TintCompositor
{
    // Config-backed (tunables-ssot.md T1) — gk-core/data/tuning/vfx.v1.json's render.tintReassertSeconds.
    public static float ReassertSeconds => (float)VfxTuningHub.Tuning.Render.TintReassertSeconds;

    sealed class Entry
    {
        public SpriteRenderer? Sr;
        public Color Base;
        public Color LastWritten;
        public readonly List<Layer> Stack = new();
        public float ReassertAccum;
    }

    public sealed class Layer
    {
        public (byte R, byte G, byte B) Rgb;
        public float Strength;
    }

    static readonly List<Entry> Entries = new();

    /// <summary>Add a tint layer; returns the owner token used to remove it.</summary>
    public static Layer? Apply(SpriteRenderer? sr, (byte R, byte G, byte B) rgb, float strength)
    {
        if (sr == null) return null;
        var entry = Find(sr);
        if (entry == null)
        {
            Color baseColor;
            try { baseColor = sr.color; } catch { return null; }
            entry = new Entry { Sr = sr, Base = baseColor, LastWritten = baseColor };
            Entries.Add(entry);
        }

        var layer = new Layer { Rgb = rgb, Strength = strength };
        entry.Stack.Add(layer);
        Write(entry);
        return layer;
    }

    public static void Remove(Layer? layer)
    {
        if (layer == null) return;
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            var e = Entries[i];
            if (!e.Stack.Remove(layer)) continue;
            if (e.Stack.Count == 0)
            {
                Restore(e);
                Entries.RemoveAt(i);
            }
            else
            {
                Write(e);
            }

            return;
        }
    }

    /// <summary>Periodic re-assert — adopt external writes as the new base, then re-shade.</summary>
    public static void Tick(float dt)
    {
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            var e = Entries[i];
            if (e.Sr == null)
            {
                Entries.RemoveAt(i);
                continue;
            }

            e.ReassertAccum += dt;
            if (e.ReassertAccum < ReassertSeconds) continue;
            e.ReassertAccum = 0f;
            Color current;
            try { current = e.Sr.color; }
            catch
            {
                Entries.RemoveAt(i);
                continue;
            }

            if (Differs(current, e.LastWritten))
                e.Base = current; // vanilla (or a flash restore) wrote — adopt, never fight
            Write(e);
        }
    }

    /// <summary>Restore every base — match teardown / master toggle off.</summary>
    public static void Clear()
    {
        foreach (var e in Entries) Restore(e);
        Entries.Clear();
    }

    static Entry? Find(SpriteRenderer sr)
    {
        foreach (var e in Entries)
        {
            if (e.Sr == sr) return e;
        }

        return null;
    }

    static void Write(Entry e)
    {
        var baseRgb = ((byte)(e.Base.r * 255f), (byte)(e.Base.g * 255f), (byte)(e.Base.b * 255f));
        var (r, g, b) = VfxTintMath.Composite(
            baseRgb, e.Stack.Select(l => (l.Rgb, l.Strength)));
        var color = new Color(r / 255f, g / 255f, b / 255f, e.Base.a);
        try
        {
            e.Sr!.color = color;
            e.LastWritten = color;
        }
        catch { }
    }

    static void Restore(Entry e)
    {
        try
        {
            if (e.Sr != null) e.Sr.color = e.Base;
        }
        catch { }
    }

    static bool Differs(Color a, Color b) =>
        Mathf.Abs(a.r - b.r) > 0.004f || Mathf.Abs(a.g - b.g) > 0.004f
        || Mathf.Abs(a.b - b.b) > 0.004f || Mathf.Abs(a.a - b.a) > 0.004f;
}
