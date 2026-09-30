using FusionRpg.Contracts;
using FusionRpg.Core.Match;
using FusionRpg.Core.Vfx;
using FusionRpg.Injector.Host;
using FusionRpg.Bridge.Hud;
using FusionRpg.Injector.Lawn;
using FusionRpg.Injector.Match;
using UnityEngine;

namespace FusionRpg.Injector.Fx;

/// <summary>
/// The single production IVfxSink — vfx-ssot.md §8. Play() only enqueues and is callable from any
/// thread; all Unity work happens in Tick/Draw on the main thread. Hosts call exactly Tick + Draw.
/// Never throws into the loop; every skip emits debug.fx.skipped with an enumerated reason.
/// </summary>
public static class VfxDirector
{
    static readonly object Gate = new();
    static readonly Queue<VfxCueDto> Pending = new();
    static readonly HashSet<string> Muted = new(StringComparer.OrdinalIgnoreCase);
    static readonly VfxCatalog Catalog = CreateCatalog();
    static readonly VfxAdmission Admission = new(Catalog);
    static readonly List<Floater> Floaters = new();
    static readonly List<Flash> Flashes = new();
    static readonly VfxStateTracker Sustained = new();
    static double _now;
    static Camera? _cam;

    /// <summary>Player preference for cosmetic effects. The world actor HUD has its own setting.</summary>
    public static bool VisualEffectsEnabled { get; private set; } = true;

    public static readonly IVfxSink Sink = new SinkAdapter();

    sealed class SinkAdapter : IVfxSink
    {
        public void Play(VfxCueDto cue) => VfxDirector.Play(cue);
    }

    static VfxCatalog CreateCatalog()
    {
        var c = new VfxCatalog();
        c.ReplaceAll(VfxSeedCatalog.CreateAll());
        return c;
    }

    public static IReadOnlyList<string> CueIds => Catalog.Ids;

    public static IReadOnlyList<string> MutedIds
    {
        get
        {
            lock (Gate) return Muted.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public static void SetMuted(string cueId, bool muted)
    {
        if (string.IsNullOrWhiteSpace(cueId)) return;
        lock (Gate)
        {
            if (muted) Muted.Add(cueId);
            else Muted.Remove(cueId);
        }
    }

    /// <summary>Main-thread setting application. Disabling immediately releases every live cosmetic.</summary>
    public static void SetVisualEffectsEnabled(bool enabled)
    {
        if (VisualEffectsEnabled == enabled) return;
        VisualEffectsEnabled = enabled;
        if (!enabled) ClearVisuals(VfxStateEndReasons.Disabled);
    }

    /// <summary>Thread-safe enqueue only — no Unity API calls here (vfx-ssot.md §5).</summary>
    public static void Play(VfxCueDto? cue)
    {
        if (cue == null || !VisualEffectsEnabled) return;
        lock (Gate)
        {
            while (Pending.Count >= VfxRules.CueQueueCap) Pending.Dequeue();
            Pending.Enqueue(cue);
        }
    }

    public static void Tick(float dt)
    {
        if (dt < 0f) dt = 0f;
        _now += dt;
        if (!VisualEffectsEnabled)
        {
            // The HUD is a separate player preference even though this director owns its main-thread tick.
            try { ActorHudDirector.TickSync(); } catch { }
            return;
        }
        // Idle early-out: with nothing queued or live the frame costs one lock + four counts.
        bool queued;
        lock (Gate) queued = Pending.Count > 0;
        var hudWake = false;
        try
        {
            hudWake = ActorHudPool.WorldBars > 0
                || (Effects.EffectRuntime.Bag.ShieldGate?.Runtime?.HasAnyInstances() == true);
            // The phase wake exists so the world HUD can appear on an actor that has drawn nothing
            // yet. It is therefore worth exactly nothing when the world HUD is off -- and before
            // 2026-09-17 it still forced the whole drain/tick chain every frame of every match, so
            // turning the HUD off skipped the DRAW and kept paying for the WAKE. Gating it means
            // WorldHudEnabled=false now actually buys back the idle early-out, which is the whole
            // point of having the switch.
            if (!hudWake && ActorHudPool.WorldHudEnabled)
            {
                var phase = MatchHost.Runtime.Phase;
                hudWake = phase is MatchPhase.InMatch or MatchPhase.Paused
                    or MatchPhase.Starting;
            }
        }
        catch { }

        if (!queued && Floaters.Count == 0 && Flashes.Count == 0 && BurstPool.LiveCount() == 0
            && ImpactStampPool.LiveCount() == 0 && EarthPhasePool.LiveCount() == 0
            && Sustained.LiveCount == 0 && !hudWake)
            return;
        DrainQueue();
        TickFloaters(dt);
        BurstPool.Tick(dt);
        ImpactStampPool.Tick(dt);
        EarthPhasePool.Tick(dt);
        TickFlashes(dt);
        TickSustained(dt);
        try { ActorHudDirector.TickSync(); } catch { }
    }

    /// <summary>Match end / scene teardown — vfx-ssot.md §8.1.</summary>
    public static void ClearAll()
    {
        ClearVisuals(VfxStateEndReasons.MatchEnd);
        try { ActorHudDirector.StopAll(); } catch { }
    }

    static void ClearVisuals(string endReason)
    {
        lock (Gate) Pending.Clear();
        Floaters.Clear();
        RestoreAndClearFlashes();
        BurstPool.StopAll();
        ImpactStampPool.StopAll();
        EarthPhasePool.StopAll();
        Admission.Clear();
        foreach (var set in Sustained.EndAll())
            EndSustainedRender(set, endReason, emit: false);
        AuraPool.StopAll();
        TintCompositor.Clear();
    }

    /// <summary>Live sustained sets snapshot for the debug board (cueId, ptr, statusId).</summary>
    public static List<Dictionary<string, object>> SustainedSnapshot()
    {
        var list = new List<Dictionary<string, object>>();
        foreach (var s in Sustained.Live)
        {
            list.Add(new Dictionary<string, object>
            {
                ["statusId"] = s.StatusId,
                ["ptr"] = s.HostPtr,
                ["cueId"] = s.CueId,
                ["hasMarker"] = s.HasMarker
            });
        }

        return list;
    }

    static void DrainQueue()
    {
        List<VfxCueDto>? drained = null;
        lock (Gate)
        {
            if (Pending.Count > 0)
            {
                drained = new List<VfxCueDto>(Pending);
                Pending.Clear();
            }
        }

        if (drained == null) return;
        Admission.BeginTick(_now);
        var master = CheatState.On("SYS-DAMAGE-FX");
        var elementOn = CheatState.On("SYS-ELEMENT-FX");
        foreach (var cue in drained)
        {
            try
            {
                // Sustained control signals route before admission (not renderable recipes):
                // expire cues end the set; re-applies refresh TTL even if the transient render
                // below gets rate-limited (refresh starvation guard, vfx-v3 plan).
                if (StatusVfxCues.TryParse(cue.CueId, out var statusId, out var isExpire)
                    && !string.IsNullOrWhiteSpace(cue.TargetPtr))
                {
                    if (isExpire)
                    {
                        var endedSet = Sustained.End(cue.TargetPtr!, statusId);
                        if (endedSet != null)
                            EndSustainedRender(endedSet, VfxStateEndReasons.Expired, emit: true);
                        continue;
                    }

                    Sustained.Refresh(cue.TargetPtr!, statusId, cue.DurationMs, _now);
                }

                Spawn(cue, master, elementOn);
            }
            catch (Exception ex)
            {
                EmitSkipped(cue, VfxSkipReasons.ParticleFail);
                try { RpgHost.Log.Warning("[vfx] spawn: " + ex.Message); } catch { }
            }
        }
    }

    static void Spawn(VfxCueDto cue, bool master, bool elementOn)
    {
        bool IsMuted(string id)
        {
            lock (Gate) return Muted.Contains(id);
        }

        var decision = Admission.Decide(cue, _now, master, IsMuted);
        if (!decision.Admitted)
        {
            EmitSkipped(cue, decision.Reason);
            return;
        }

        var plan = VfxColorPlan.For(cue.Tag, cue.Elements, elementOn, cue.Amount);

        Transform? follow = null;
        Transform? source = null;
        if (!string.IsNullOrWhiteSpace(cue.SourcePtr)) source = AnchorResolver.Resolve(cue.SourcePtr);
        var world = Vector3.zero;
        var hasWorld = false;
        int sizeCol = CheatState.SpawnCol, sizeRow = CheatState.SpawnRow;
        if (!string.IsNullOrWhiteSpace(cue.TargetPtr))
        {
            follow = AnchorResolver.Resolve(cue.TargetPtr);
            if (follow == null)
            {
                EmitSkipped(cue, VfxSkipReasons.Missing);
                return;
            }

            try
            {
                var frame = UnitFrameResolver.Resolve(follow);
                world = frame.World(VfxAnchorKind.Body);
                hasWorld = true;
            }
            catch { }
        }
        else if (cue.Col.HasValue && cue.Row.HasValue)
        {
            sizeCol = LawnCoords.ClampCol(cue.Col.Value);
            sizeRow = LawnCoords.ClampRow(cue.Row.Value);
            try
            {
                var c = LawnCoords.CellCenter(sizeCol, sizeRow);
                world = new Vector3(c.x, c.y, 0f);
                hasWorld = true;
            }
            catch
            {
                EmitSkipped(cue, VfxSkipReasons.Missing);
                return;
            }
        }
        else if (cue.WorldX.HasValue && cue.WorldY.HasValue)
        {
            world = new Vector3(cue.WorldX.Value, cue.WorldY.Value, 0f);
            hasWorld = true;
        }
        else
        {
            EmitSkipped(cue, VfxSkipReasons.Missing);
            return;
        }

        // Sustained sets start regardless of which transient specs render below.
        if (decision.Recipe!.HasSustained && follow != null
            && StatusVfxCues.TryParse(cue.CueId, out var sustainedId, out var sustainedExpire)
            && !sustainedExpire)
        {
            var startedSet = Sustained.Start(
                cue.TargetPtr!, sustainedId, cue.CueId, decision.Recipe, cue.DurationMs, _now,
                out var evicted);
            foreach (var ev in evicted)
                EndSustainedRender(ev, VfxStateEndReasons.Evicted, emit: true);
            if (startedSet != null)
                StartSustainedRender(startedSet, plan, follow);
        }

        VfxUnitFrame unitFrame;
        try
        {
            unitFrame = follow != null
                ? UnitFrameResolver.Resolve(follow)
                : UnitFrameResolver.ResolveCell(sizeCol, sizeRow);
        }
        catch
        {
            EmitSkipped(cue, VfxSkipReasons.Missing);
            return;
        }
        var cellSize = unitFrame.CellSize;
        var kinds = new List<string>(decision.SpecIndices.Count);
        string? failReason = null;
        var elementGated = false;
        foreach (var idx in decision.SpecIndices)
        {
            var spec = decision.Recipe!.Primitives[idx];
            if (spec.RequireElement && !plan.ElementColored)
            {
                elementGated = true;
                continue;
            }
            switch (spec.Kind)
            {
                case VfxPrimitiveKind.Floater:
                    if (follow == null || spec.Label == VfxLabelSourceKind.None) break;
                    SpawnFloater(cue, plan, spec, follow);
                    kinds.Add("floater");
                    break;
                case VfxPrimitiveKind.Burst:
                    if (!hasWorld) break;
                    if (BurstPool.Spawn(world, plan, spec, cellSize, cue.LifeMul, cue.ScaleMul, out var reason))
                        kinds.Add("burst");
                    else
                        failReason = reason;
                    break;
                case VfxPrimitiveKind.Flash:
                    if (follow == null) break;
                    if (SpawnFlash(follow, plan, spec)) kinds.Add("flash");
                    break;
                case VfxPrimitiveKind.ImpactStamp:
                    // The Earth pilot is intentionally concrete-only: hybrids retain their existing
                    // mixed-color particles and never borrow authored Earth art.
                    if (!plan.IsConcreteElement("earth")) break;
                    if (ImpactStampPool.Spawn(world, unitFrame, follow, VfxTuningHub.Tuning.ImpactStamp, out var stampReason))
                        kinds.Add("impactStamp");
                    else
                        failReason = stampReason;
                    break;
                case VfxPrimitiveKind.Charge:
                    if (!plan.IsConcreteElement("earth") || source == null) break;
                    if (EarthPhasePool.SpawnCharge(source, plan.Rgb, out var chargeReason)) kinds.Add("charge");
                    else failReason = chargeReason;
                    break;
                case VfxPrimitiveKind.Travel:
                    if (!plan.IsConcreteElement("earth") || source == null || follow == null) break;
                    if (EarthPhasePool.SpawnTravel(source, follow, plan.Rgb, out var travelReason)) kinds.Add("travel");
                    else failReason = travelReason;
                    break;
            }
        }

        if (kinds.Count == 0)
        {
            EmitSkipped(cue, failReason ?? (elementGated ? VfxSkipReasons.NoElement : VfxSkipReasons.ParticleFail));
            return;
        }

        EmitShown(cue, plan, kinds);
    }

    static void SpawnFloater(VfxCueDto cue, VfxColorPlan plan, VfxPrimitiveSpec spec, Transform follow)
    {
        while (Floaters.Count >= VfxRules.FloaterCap) Floaters.RemoveAt(0);
        var label = spec.Label == VfxLabelSourceKind.Fixed ? spec.FixedLabel : plan.Label;
        Floaters.Add(new Floater
        {
            Plan = plan,
            Label = label,
            Follow = follow,
            Age = -Math.Max(0f, spec.DelaySeconds),
            Life = spec.LifeSeconds * (cue.LifeMul > 0f ? cue.LifeMul : 1f)
        });
    }

    static bool SpawnFlash(Transform follow, VfxColorPlan plan, VfxPrimitiveSpec spec)
    {
        try
        {
            var sr = follow.GetComponentInChildren<SpriteRenderer>();
            if (sr == null) return false;
            foreach (var f in Flashes)
            {
                // Re-trigger resets the timer but keeps the originally captured color — §8.5.
                if (f.Sr == sr)
                {
                    f.Age = 0f;
                    f.Life = spec.LifeSeconds;
                    return true;
                }
            }

            var rgb = spec.Color == VfxColorSourceKind.Fixed ? spec.FixedRgb : plan.Rgb;
            var original = sr.color;
            var tint = Color.Lerp(original, new Color(rgb.R / 255f, rgb.G / 255f, rgb.B / 255f, original.a), 0.65f);
            sr.color = tint;
            Flashes.Add(new Flash { Sr = sr, Original = original, Age = 0f, Life = spec.LifeSeconds });
            return true;
        }
        catch
        {
            return false;
        }
    }

    static void TickFloaters(float dt)
    {
        for (var i = Floaters.Count - 1; i >= 0; i--)
        {
            var f = Floaters[i];
            f.Age += dt;
            if (f.Age >= f.Life || f.Follow == null)
                Floaters.RemoveAt(i);
        }
    }

    static void TickSustained(float dt)
    {
        if (Sustained.LiveCount == 0) return;
        if (!CheatState.On("SYS-DAMAGE-FX"))
        {
            foreach (var set in Sustained.EndAll())
                EndSustainedRender(set, VfxStateEndReasons.Disabled, emit: true);
            return;
        }

        foreach (var set in Sustained.SweepTtl(_now))
            EndSustainedRender(set, VfxStateEndReasons.TtlCap, emit: true);

        TintCompositor.Tick(dt);

        List<VfxSustainedSet>? gone = null;
        foreach (var set in Sustained.Live)
        {
            var anchor = AnchorResolver.Resolve(set.HostPtr);
            if (anchor == null)
            {
                (gone ??= new List<VfxSustainedSet>()).Add(set);
                continue;
            }

            TickSustainedRender(set, anchor, dt);
        }

        if (gone == null) return;
        foreach (var set in gone)
        {
            Sustained.End(set.HostPtr, set.StatusId);
            EndSustainedRender(set, VfxStateEndReasons.HostGone, emit: true);
        }
    }

    sealed class SustainedRender
    {
        public AuraPool.Lease? Aura;
        public VfxPrimitiveSpec? AuraSpec;
        public (byte R, byte G, byte B) AuraRgb;
        public TintCompositor.Layer? Tint;
        public AuraPool.Lease? Marker;
        public (byte R, byte G, byte B) MarkerRgb;
    }

    static void StartSustainedRender(VfxSustainedSet set, VfxColorPlan plan, Transform follow)
    {
        var render = new SustainedRender();
        foreach (var spec in set.Recipe.Primitives)
        {
            if (spec.Kind == VfxPrimitiveKind.Aura && render.Aura == null)
            {
                render.AuraSpec = spec;
                render.AuraRgb = spec.Color == VfxColorSourceKind.Fixed ? spec.FixedRgb : plan.Rgb;
                render.Aura = AuraPool.Take(FxResources.ParticleMaterial());
            }
            else if (spec.Kind == VfxPrimitiveKind.Tint && render.Tint == null)
            {
                SpriteRenderer? sr = null;
                try { sr = follow.GetComponentInChildren<SpriteRenderer>(); } catch { }
                var rgb = spec.Color == VfxColorSourceKind.Fixed ? spec.FixedRgb : plan.Rgb;
                render.Tint = TintCompositor.Apply(sr, rgb, spec.TintStrength);
            }
            else if (spec.Kind == VfxPrimitiveKind.Marker && render.Marker == null)
            {
                render.MarkerRgb = spec.Color == VfxColorSourceKind.Fixed ? spec.FixedRgb : plan.Rgb;
                render.Marker = AuraPool.Take(FxResources.MarkerMaterial(spec.MarkerShape));
            }
        }

        set.RenderState = render;
        DebugRuntime.Emit("debug.fx.state.started", new Dictionary<string, object>
        {
            ["cueId"] = set.CueId,
            ["statusId"] = set.StatusId,
            ["ptr"] = set.HostPtr,
            ["live"] = Sustained.LiveCount
        });
    }

    static void TickSustainedRender(VfxSustainedSet set, Transform anchor, float dt)
    {
        if (set.RenderState is not SustainedRender render) return;
        if (render.Aura == null && render.Marker == null) return;
        VfxUnitFrame frame;
        try { frame = UnitFrameResolver.Resolve(anchor); }
        catch { return; }

        if (render.Aura != null && render.AuraSpec != null)
        {
            var kind = VfxAnchorCatalog.AnchorKindFor(render.AuraSpec.AuraStyle);
            var world = frame.World(kind);
            var auraSpan = frame.Span(render.AuraSpec.SizeScale > 0f ? render.AuraSpec.SizeScale : 1f);
            var sortOrder = frame.ParticleSortingOrder;
            AuraPool.Pulse(render.Aura, world, render.AuraSpec.AuraStyle, render.AuraRgb, auraSpan, dt, sortOrder);
        }

        if (render.Marker != null)
        {
            var world = frame.World(VfxAnchorKind.Crown);
            var span = frame.Span();
            var sortOrder = frame.ParticleSortingOrder;
            var renderTuning = VfxTuningHub.Tuning.Render;
            var markerSize = span * (float)renderTuning.MarkerSizeScale;
            var markerLift = span * (float)renderTuning.MarkerYOffsetScale;
            AuraPool.PulseSingle(render.Marker, world, render.MarkerRgb, markerSize, markerLift, dt, sortOrder);
        }
    }

    static void EndSustainedRender(VfxSustainedSet set, string reason, bool emit)
    {
        if (set.RenderState is SustainedRender render)
        {
            AuraPool.Release(render.Aura);
            render.Aura = null;
            TintCompositor.Remove(render.Tint);
            render.Tint = null;
            AuraPool.Release(render.Marker);
            render.Marker = null;
        }

        set.RenderState = null;
        if (!emit) return;
        DebugRuntime.Emit("debug.fx.state.ended", new Dictionary<string, object>
        {
            ["cueId"] = set.CueId,
            ["statusId"] = set.StatusId,
            ["ptr"] = set.HostPtr,
            ["reason"] = reason,
            ["live"] = Sustained.LiveCount
        });
    }

    static void TickFlashes(float dt)
    {
        for (var i = Flashes.Count - 1; i >= 0; i--)
        {
            var f = Flashes[i];
            f.Age += dt;
            if (f.Age < f.Life) continue;
            RestoreFlash(f);
            Flashes.RemoveAt(i);
        }
    }

    static void RestoreAndClearFlashes()
    {
        foreach (var f in Flashes) RestoreFlash(f);
        Flashes.Clear();
    }

    static void RestoreFlash(Flash f)
    {
        try
        {
            if (f.Sr != null) f.Sr.color = f.Original;
        }
        catch { }
    }

    public static void Draw()
    {
        try
        {
            var e = Event.current;
            if (e == null || e.type != EventType.Repaint) return;
        }
        catch
        {
            return;
        }

        // lawn-screenshot: an armed capture runs here — Repaint is after every Camera and
        // overlay Canvas, so a backbuffer read is the presented frame. Never in Tick/drain
        // (mid-frame capture is undefined). One call, consumed by the runner itself.
        try { ScreenshotRunner.CaptureOnRepaint(); } catch { }

        if (!VisualEffectsEnabled) return;
        if (Floaters.Count == 0) return;
        // Camera resolves only here — with live floaters, on Repaint — never in Tick.
        // Re-resolve when the cached camera is destroyed OR merely disabled: scene switches
        // often disable the old MainCamera without destroying it, and Camera.main tracks
        // the enabled one. Stale-but-alive must not stick.
        var stale = _cam == null;
        if (!stale)
        {
            try { stale = !_cam!.isActiveAndEnabled; } catch { stale = true; }
        }
        if (stale)
        {
            try { _cam = Camera.main; } catch { _cam = null; }
        }
        var cam = _cam;
        if (cam == null) return;

        // IL2CPP interop has no GUIStyle copy ctor — mutate the shared label style, restore after.
        var skin = GUI.skin;
        if (skin == null) return;
        var style = skin.label;
        var oldSize = style.fontSize;
        var oldFontStyle = style.fontStyle;
        var oldAlign = style.alignment;
        style.fontStyle = FontStyle.Bold;
        style.alignment = TextAnchor.MiddleCenter;

        try
        {
            foreach (var f in Floaters)
            {
                if (f.Follow == null || f.Age < 0f) continue;
                Vector3 world;
                try
                {
                    var frame = UnitFrameResolver.Resolve(f.Follow);
                    world = frame.World(VfxAnchorKind.Body);
                }
                catch { continue; }
                var t = f.Life > 0f ? Mathf.Clamp01(f.Age / f.Life) : 1f;
                if (!LawnCoords.TryWorldToGui(cam, world, t, out var gui)) continue;

                var alpha = Core.Effects.DamageFxFloaterRules.Alpha(t);
                style.fontSize = (int)Math.Round(20f * f.Plan.FontScaleAt(t));
                var rect = new Rect(gui.x - 80f, gui.y - 16f, 160f, 32f);
                // Shadow pass first — readability on bright lawns (SPEC W3).
                GUI.color = new Color(0f, 0f, 0f, alpha);
                GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), f.Label, style);
                var rgb = f.Plan.ColorAt(t);
                GUI.color = new Color(rgb.R / 255f, rgb.G / 255f, rgb.B / 255f, alpha);
                GUI.Label(rect, f.Label, style);
            }
        }
        finally
        {
            style.fontSize = oldSize;
            style.fontStyle = oldFontStyle;
            style.alignment = oldAlign;
            GUI.color = Color.white;
        }
    }

    static void EmitShown(VfxCueDto cue, VfxColorPlan plan, List<string> kinds)
    {
        DebugRuntime.Emit("debug.fx.shown", new Dictionary<string, object>
        {
            ["cueId"] = cue.CueId,
            ["ptr"] = cue.TargetPtr ?? "",
            ["col"] = cue.Col ?? -1,
            ["row"] = cue.Row ?? -1,
            ["amount"] = cue.Amount,
            ["tag"] = cue.Tag?.ToString() ?? "",
            ["label"] = plan.Label,
            ["rgb"] = RgbHex(plan.Rgb),
            ["hybrid"] = plan.Hybrid,
            ["elements"] = cue.Elements?.Count ?? 0,
            ["primitives"] = kinds,
            ["floaters"] = Floaters.Count,
            ["bursts"] = BurstPool.LiveCount()
        });
    }

    static void EmitSkipped(VfxCueDto cue, string reason)
    {
        DebugRuntime.Emit("debug.fx.skipped", new Dictionary<string, object>
        {
            ["cueId"] = cue.CueId,
            ["ptr"] = cue.TargetPtr ?? "",
            ["col"] = cue.Col ?? -1,
            ["row"] = cue.Row ?? -1,
            ["amount"] = cue.Amount,
            ["tag"] = cue.Tag?.ToString() ?? "",
            ["reason"] = reason
        });
    }

    static string RgbHex((byte R, byte G, byte B) c) =>
        "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

    sealed class Floater
    {
        public VfxColorPlan Plan = new();
        public string Label = "";
        public Transform? Follow;
        public float Age;
        public float Life = VfxRules.FloaterLifeSeconds;
    }

    sealed class Flash
    {
        public SpriteRenderer? Sr;
        public Color Original;
        public float Age;
        public float Life;
    }
}
