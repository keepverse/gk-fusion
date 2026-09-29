using FusionRpg.Core.Vfx;
using UnityEngine;

namespace FusionRpg.Injector.Fx;

/// <summary>Shared bounded renderer pool for the Earth source charge and shard travel phases.</summary>
static class EarthPhasePool
{
    sealed class Slot { public GameObject? Go; public SpriteRenderer? Renderer; public Transform? Source; public Transform? Target; public Color Tint; public float Age; public float Life; public bool Travel; public bool Live; }
    static readonly List<Slot> Slots = new();

    public static bool SpawnCharge(Transform source, (byte R, byte G, byte B) rgb, out string reason) => Spawn("charge", source, null, false, rgb, out reason);
    public static bool SpawnTravel(Transform source, Transform target, (byte R, byte G, byte B) rgb, out string reason) => Spawn("travel", source, target, true, rgb, out reason);

    static bool Spawn(string phase, Transform source, Transform? target, bool travel, (byte R, byte G, byte B) rgb, out string reason)
    {
        reason = "";
        var tuning = VfxTuningHub.Tuning.EarthPhase;
        if (tuning == null || tuning.PoolCap <= 0 || tuning.ChargeLifeSeconds <= 0d || tuning.TravelLifeSeconds <= 0d)
        {
            reason = VfxSkipReasons.ParticleFail;
            return false;
        }
        if (!FxResources.TryGetEarthPhase(phase, out var sprite, out var material)) { reason = VfxSkipReasons.AssetMissing; return false; }
        var slot = Slots.FirstOrDefault(s => s.Go != null && !s.Live) ?? (Slots.Count < tuning.PoolCap ? Create(sprite, material) : Slots.Where(s => s.Live).OrderByDescending(s => s.Age).FirstOrDefault());
        if (slot == null || slot.Go == null || slot.Renderer == null) { reason = VfxSkipReasons.ParticleFail; return false; }
        slot.Renderer.sprite = sprite; slot.Renderer.sharedMaterial = material; slot.Source = source; slot.Target = target; slot.Travel = travel;
        slot.Tint = new Color32(rgb.R, rgb.G, rgb.B, byte.MaxValue);
        slot.Age = travel ? -(float)tuning.TravelDelaySeconds : 0f; slot.Life = (float)(travel ? tuning.TravelLifeSeconds : tuning.ChargeLifeSeconds); slot.Live = true;
        if (slot.Age < 0f) { slot.Go.SetActive(false); return true; }
        Apply(slot); slot.Go.SetActive(true); return true;
    }

    public static void Tick(float dt) { foreach (var s in Slots) { if (!s.Live || s.Go == null) continue; s.Age += Mathf.Max(0f, dt); if (s.Age < 0f) continue; if (s.Age >= s.Life || s.Source == null || (s.Travel && s.Target == null)) { Quiet(s); continue; } Apply(s); try { if (!s.Go.activeSelf) s.Go.SetActive(true); } catch { Quiet(s); } } }
    public static void StopAll() { foreach (var s in Slots) Quiet(s); }
    public static int LiveCount() => Slots.Count(s => s.Live);

    static void Apply(Slot s)
    {
        try {
            var tuning = VfxTuningHub.Tuning.EarthPhase;
            if (tuning == null) { Quiet(s); return; }
            var sourceFrame = UnitFrameResolver.Resolve(s.Source!);
            var from = sourceFrame.World(VfxAnchorKind.Body);
            var targetFrame = s.Target == null ? sourceFrame : UnitFrameResolver.Resolve(s.Target);
            var to = s.Target == null ? from : targetFrame.World(VfxAnchorKind.Body);
            var t = Mathf.Clamp01(s.Age / s.Life); var p = s.Travel ? Vector3.Lerp(from, to, t) : from;
            var sprite = s.Renderer!.sprite;
            var bounds = sprite?.bounds.size ?? Vector3.zero;
            var span = UnitFrameResolver.SpriteSpan(sprite);
            if (span <= 0f || bounds.x <= 0f || bounds.y <= 0f) { Quiet(s); return; }
            s.Go!.transform.position = p;
            if (s.Travel)
            {
                var delta = to - from;
                var length = Mathf.Max(sourceFrame.Span((float)tuning.TravelLengthScale), delta.magnitude * (float)tuning.TravelLengthScale);
                var thickness = sourceFrame.Span((float)tuning.TravelThicknessScale);
                s.Go.transform.localScale = new Vector3(length / bounds.x, thickness / bounds.y, 1f);
                s.Go.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            }
            else s.Go.transform.localScale = Vector3.one * (sourceFrame.Span((float)tuning.ChargeSpanScale) / span);
            s.Renderer.color = new Color(s.Tint.r, s.Tint.g, s.Tint.b,
                Mathf.Lerp((float)tuning.StartAlpha, (float)tuning.EndAlpha, t));
            s.Renderer.sortingOrder = targetFrame.ParticleSortingOrder + tuning.SortOffset;
        } catch { Quiet(s); }
    }
    static Slot? Create(Sprite sprite, Material material) { try { var go = new GameObject("FusionRpgEarthPhase") { hideFlags = HideFlags.HideAndDontSave }; var r = go.AddComponent<SpriteRenderer>(); r.sprite = sprite; r.sharedMaterial = material; go.SetActive(false); var s = new Slot { Go = go, Renderer = r }; Slots.Add(s); return s; } catch { return null; } }
    static void Quiet(Slot s) { try { if (s.Go != null) s.Go.SetActive(false); } catch { } s.Source = s.Target = null; s.Age = s.Life = 0f; s.Live = false; }
}
