using FusionRpg.Core.Vfx;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FusionRpg.Injector.Fx;

/// <summary>
/// Bounded, world-space Earth stamp renderers. A cue leases a renderer; ticks only update its
/// follow anchor, scale and alpha. Slots are reused and a full pool steals the oldest live slot.
/// </summary>
static class ImpactStampPool
{
    sealed class Slot
    {
        public GameObject? Go;
        public SpriteRenderer? Renderer;
        public Transform? Follow;
        public Vector3 World;
        public float Age;
        public float Life;
        public float Span;
        public VfxImpactStampTuning? Tuning;
        public bool Live;
    }

    static readonly List<Slot> Slots = new();

    public static bool Spawn(Vector3 world, VfxUnitFrame frame, Transform? follow,
        VfxImpactStampTuning? tuning, out string failReason)
    {
        failReason = "";
        if (tuning == null || tuning.PoolCap <= 0 || tuning.LifeSeconds <= 0d)
        {
            failReason = VfxSkipReasons.ParticleFail;
            return false;
        }
        if (!FxResources.TryGetEarthImpactStamp(out var sprite, out var material))
        {
            failReason = VfxSkipReasons.AssetMissing;
            return false;
        }

        var slot = TakeSlot(tuning.PoolCap, sprite, material);
        if (slot?.Go == null || slot.Renderer == null)
        {
            failReason = VfxSkipReasons.ParticleFail;
            return false;
        }

        slot.Follow = follow;
        slot.World = world;
        slot.Age = -(float)tuning.DelaySeconds;
        slot.Life = (float)tuning.LifeSeconds;
        slot.Span = frame.Span((float)tuning.SpanScale);
        slot.Tuning = tuning;
        slot.Live = true;
        if (slot.Age >= 0f)
        {
            Apply(slot, frame, sprite);
            try { slot.Go.SetActive(true); } catch { Quiet(slot); failReason = VfxSkipReasons.ParticleFail; return false; }
        }
        return true;
    }

    public static void Tick(float dt)
    {
        if (dt < 0f) dt = 0f;
        foreach (var slot in Slots)
        {
            if (!slot.Live) continue;
            slot.Age += dt;
            if (slot.Age < 0f) continue;
            if (slot.Age >= slot.Life || slot.Go == null || slot.Renderer == null || slot.Tuning == null)
            {
                Quiet(slot);
                continue;
            }

            if (slot.Follow != null)
            {
                try
                {
                    var frame = UnitFrameResolver.Resolve(slot.Follow);
                    slot.World = frame.World(VfxAnchorKind.Body);
                    slot.Span = frame.Span((float)slot.Tuning.SpanScale);
                    Apply(slot, frame, slot.Renderer.sprite);
                    if (!slot.Go.activeSelf) slot.Go.SetActive(true);
                }
                catch { Quiet(slot); }
            }
            else
            {
                Apply(slot, default, slot.Renderer.sprite);
                if (!slot.Go.activeSelf) slot.Go.SetActive(true);
            }
        }
    }

    public static void StopAll()
    {
        foreach (var slot in Slots) Quiet(slot);
    }

    public static int LiveCount() => Slots.Count(s => s.Live);

    static void Apply(Slot slot, VfxUnitFrame frame, Sprite? sprite)
    {
        if (slot.Go == null || slot.Renderer == null || slot.Tuning == null || sprite == null) return;
        var t = slot.Life > 0f ? Mathf.Clamp01(slot.Age / slot.Life) : 1f;
        var alpha = Mathf.Lerp((float)slot.Tuning.StartAlpha, (float)slot.Tuning.EndAlpha, t);
        var scale = Mathf.Lerp((float)slot.Tuning.StartScale, (float)slot.Tuning.EndScale, t);
        var spriteSpan = UnitFrameResolver.SpriteSpan(sprite);
        var size = spriteSpan > 0f ? slot.Span / spriteSpan * scale : 0f;
        try
        {
            slot.Go.transform.position = slot.World;
            slot.Go.transform.localScale = new Vector3(size, size, 1f);
            slot.Renderer.color = new Color(1f, 1f, 1f, alpha);
            slot.Renderer.sortingOrder = frame.ParticleSortingOrder + slot.Tuning.SortOffset;
        }
        catch { Quiet(slot); }
    }

    static void Quiet(Slot slot)
    {
        slot.Live = false;
        slot.Follow = null;
        slot.Tuning = null;
        slot.Age = 0f;
        try { if (slot.Go != null) slot.Go.SetActive(false); } catch { }
    }

    static Slot? TakeSlot(int cap, Sprite sprite, Material material)
    {
        Slots.RemoveAll(slot => slot.Go == null && !slot.Live);
        foreach (var slot in Slots)
        {
            if (!slot.Live && slot.Go != null) return slot;
        }
        if (Slots.Count < cap)
        {
            var created = Create(sprite, material);
            if (created != null) Slots.Add(created);
            return created;
        }

        // Pool full: steal the oldest live stamp, matching the VFX drop-oldest policy.
        var oldest = Slots.Where(slot => slot.Live && slot.Go != null).OrderByDescending(slot => slot.Age).FirstOrDefault();
        if (oldest != null) Quiet(oldest);
        return oldest;
    }

    static Slot? Create(Sprite sprite, Material material)
    {
        try
        {
            var go = new GameObject("FusionRpgEarthImpactStamp") { hideFlags = HideFlags.HideAndDontSave };
            var renderer = go.AddComponent<SpriteRenderer>();
            if (renderer == null)
            {
                UObject.Destroy(go);
                return null;
            }
            renderer.sprite = sprite;
            renderer.sharedMaterial = material;
            go.SetActive(false);
            return new Slot { Go = go, Renderer = renderer };
        }
        catch { return null; }
    }
}
