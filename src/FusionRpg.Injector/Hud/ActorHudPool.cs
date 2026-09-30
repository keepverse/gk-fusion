using FusionRpg.Core.Combat;
using FusionRpg.Core.Hud;
using FusionRpg.Core.Vfx;
using FusionRpg.Injector.Effects;
using FusionRpg.Injector.Fx;
using FusionRpg.Injector.Host;
using UnityEngine;
using UnityEngine.UI;
using FusionRpg.Bridge.Hud;

namespace FusionRpg.Injector.Hud;

/// <summary>
/// Screen-space Band B HUD. Snapshot gathering remains in <see cref="ActorHudCache"/>; this pool
/// only projects a real actor silhouette and presents the cached snapshot beneath it.
/// </summary>
public static class ActorHudPool
{
    // Structural pool capacity, not a player-facing/balance limit.
    const int Cap = 96;
    const int MaxShieldSegments = 4;
    const int MaxStatusTokens = 3;
    const int MaxPips = 3;
    const int MaxElementIcons = 2;

    static readonly HashSet<string> SeenThisTick = new(StringComparer.Ordinal);
    static readonly List<HudSlot> Slots = new();
    static readonly Dictionary<IntPtr, string> PtrHex = new();
    const int PtrHexCap = 4096;

    static Canvas? _canvas;
    static int _worldHud;
    static int _shieldBarsDrawn;
    static float _lastAvgRatio;
    static float _lastAvgTrueRatio;
    static bool _canvasReady;
    static string _lastEarly = "never-synced";
    static int _roundRobinCursor;

    public static int WorldBars => _worldHud;
    public static int ShieldBarsDrawn => _shieldBarsDrawn;
    public static bool ShaderOk => _canvasReady;
    public static float LastAvgRatio => _lastAvgRatio;
    public static float LastAvgTrueRatio => _lastAvgTrueRatio;
    public static string LastEarly => _lastEarly;

    public sealed class HudSlot
    {
        public sealed class ElementGlyph
        {
            public Image? Icon;
        }

        public string OwnerKey = "";
        public GameObject? Root;
        public Image? TierFrame;
        public Image? LevelBadge;
        public Image? RolePip;
        public ActorHudLabel? TierLabel;
        public ActorHudLabel? LevelLabel;
        public Image? ShieldTrack;
        public readonly Image?[] ShieldSegments = new Image?[MaxShieldSegments];
        public readonly Image?[] StackPips = new Image?[MaxPips];
        public readonly Image?[] StatusTokens = new Image?[MaxStatusTokens];
        public readonly ActorHudLabel?[] StatusLabels = new ActorHudLabel?[MaxStatusTokens];
        public Image? OverflowPip;
        public ActorHudLabel? OverflowLabel;
        public readonly ElementGlyph[] ElementGlyphs = new[] { new ElementGlyph(), new ElementGlyph() };
        public bool Live;
    }

    /// <summary>
    /// ⚡ Master switch for the world actor-HUD walk (2026-09-16). Default ON, so nothing changes until
    /// something turns it off.
    ///
    /// <para><b>Why this exists.</b> <see cref="TickSync"/> runs once per frame and visits EVERY live
    /// plant and zombie. It used to build a fresh <c>ActorHudSnapshot</c> for each, because
    /// <see cref="ActorHudCache"/> rebuilt on every read — at 300 zombies, ~300 snapshot builds, ~300
    /// string keys and ~300 lock acquisitions per frame. Measured 2026-09-16 at 289 zombies,
    /// <c>vfx.tick</c> was <b>1,904 ms of loop.tick's 2,625 ms per five seconds — 73% of the
    /// frame</b>, after the derived-fold fix had already taken the whole RPG capture path down to
    /// ~770 ms. The cache now honours its dirty set and the walk memoises its ptr strings, so the
    /// per-entity cost is a dictionary hit plus the Unity placement; this switch stays as the
    /// measurement control and the low-end escape hatch. `ShieldBarEnabled` does NOT gate the walk:
    /// it is one input to
    /// <c>ActorHudVisibility.ShouldShow</c>, which returns true for any actor with a level band — which
    /// is all of them — so turning the bar off still pays the full per-entity build and skips only the
    /// draw.</para>
    ///
    /// <para>⚠️ This is a COST switch, not a feature decision. Off means no world HUD at all; the owner's
    /// own framing was "limit it, or disable and make better vfx later". Prefer
    /// <see cref="SyncBudgetPerFrame"/> for the limit, and keep this for measurement and for the
    /// low-end escape hatch.</para>
    /// </summary>
    /// <para><b>DEFAULT FLIPPED TO OFF, 2026-09-17, owner ruling.</b> The measurement that forced it:
    /// <c>vfx.tick</c> was <b>2.513 ms/frame at 80 entities</b> on a live board — on its own more than
    /// the <b>2 ms/frame</b> whole-injector stress budget <c>perf-probe-plan.md</c> §0 locks for 200+
    /// entities, and 91.8% of <c>loop.tick</c>. The owner's instruction was "check it can improve or
    /// else... we will disable a default and add user setting on the web FE": the budget dial below was
    /// already at its measured sweet spot, so this is the else. A player turns it back on from the web
    /// FE, which sends <c>hud.world</c>; <c>FUSIONRPG_ACTOR_HUD=1</c> forces it on at startup for
    /// measurement runs.</para>
    public static bool WorldHudEnabled { get; set; } =
        string.Equals(Environment.GetEnvironmentVariable("FUSIONRPG_ACTOR_HUD"), "1", StringComparison.Ordinal);

    /// <summary>Structural work budget; a positive value round-robins placement across live actors.</summary>
    public static int SyncBudgetPerFrame { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("FUSIONRPG_ACTOR_HUD_BUDGET"), out var value) && value >= 0
            ? value
            : DefaultSyncBudgetPerFrame;

    public const int DefaultSyncBudgetPerFrame = 60;

    static string HexOf(IntPtr ptr)
    {
        if (PtrHex.TryGetValue(ptr, out var hex)) return hex;
        if (PtrHex.Count >= PtrHexCap) PtrHex.Clear();
        hex = CombatPtr.Normalize(ptr.ToString("X"));
        PtrHex[ptr] = hex;
        return hex;
    }

    public static void TickSync()
    {
        if (!WorldHudEnabled)
        {
            StopAll();
            _lastEarly = "world-hud-off";
            return;
        }

        SeenThisTick.Clear();
        _worldHud = 0;
        _shieldBarsDrawn = 0;
        _lastAvgRatio = 0f;
        _lastAvgTrueRatio = 0f;
        ActorHudTuning? tuning;
        try { tuning = ActorHudTuningHub.Tuning; }
        catch
        {
            StopAll();
            _canvasReady = false;
            _lastEarly = "no-tuning";
            return;
        }

        _canvasReady = EnsureCanvas();
        if (!_canvasReady)
        {
            StopAll();
            _lastEarly = "no-canvas";
            return;
        }

        var displaySum = 0f;
        var trueSum = 0f;
        try
        {
            var frame = Time.frameCount;
            if (InjectorEntityRegistry.NeedsResync(frame)) InjectorEntityRegistry.Resync(frame);
            var total = InjectorEntityRegistry.PlantCount + InjectorEntityRegistry.ZombieCount;
            var budget = SyncBudgetPerFrame;
            var unlimited = budget <= 0 || budget >= total;
            var remaining = unlimited ? int.MaxValue : budget;
            var skip = unlimited || total == 0 ? 0 : _roundRobinCursor % total;
            var index = 0;
            void Walk(IntPtr ptr)
            {
                if (remaining <= 0) return;
                if (index++ < skip) return;
                remaining--;
                SyncEntity(HexOf(ptr), tuning, ref displaySum, ref trueSum);
            }

            InjectorEntityRegistry.VisitPlants(p => Walk(p.Pointer));
            InjectorEntityRegistry.VisitZombies(z => Walk(z.Pointer));
            if (!unlimited && total > 0) _roundRobinCursor = (_roundRobinCursor + budget) % total;
        }
        catch { }

        // With a budget, unseen means "not visited in this slice", so deaths release through ReleaseOwner.
        if (SyncBudgetPerFrame <= 0)
            foreach (var slot in Slots)
                if (slot.Live && !SeenThisTick.Contains(slot.OwnerKey)) Release(slot);

        _lastAvgRatio = _shieldBarsDrawn > 0 ? displaySum / _shieldBarsDrawn : 0f;
        _lastAvgTrueRatio = _shieldBarsDrawn > 0 ? trueSum / _shieldBarsDrawn : 0f;
        _lastEarly = _worldHud > 0 ? "ok" : "idle";
    }

    public static void StopAll()
    {
        foreach (var slot in Slots) Release(slot);
        _worldHud = 0;
        _shieldBarsDrawn = 0;
        _lastAvgRatio = 0f;
        _lastAvgTrueRatio = 0f;
    }

    public static void ReleaseOwner(string? ptrHex)
    {
        var key = CombatPtr.Normalize(ptrHex);
        if (string.IsNullOrEmpty(key)) return;
        var slot = FindLive(key);
        if (slot != null) Release(slot);
    }

    static void SyncEntity(string ptrHex, ActorHudTuning tuning, ref float displaySum, ref float trueSum)
    {
        ActorHudSnapshot? snapshot;
        try { snapshot = ActorHudCache.GetOrBuild(ptrHex); }
        catch { return; }
        if (snapshot == null || !ActorHudVisibility.ShouldShow(snapshot, OverlaySettings.ShieldBarEnabled)) return;

        var key = CombatPtr.Normalize(ptrHex);
        if (string.IsNullOrEmpty(key)) return;
        Transform? follow;
        try { follow = AnchorResolver.Resolve(key); }
        catch { return; }
        if (follow == null || !ActorScreenAnchorResolver.TryResolve(follow, tuning, out _, out var anchor)) return;

        var slot = FindLive(key) ?? TakeIdle() ?? CreateSlot();
        if (slot?.Root == null) return;
        slot.OwnerKey = key;
        slot.Live = true;
        SeenThisTick.Add(key);
        try
        {
            slot.Root.SetActive(true);
            var root = slot.Root.GetComponent<RectTransform>();
            if (root == null) return;
            root.anchoredPosition = new Vector2(anchor.CenterX, anchor.TopY);
            root.sizeDelta = new Vector2(anchor.Width, 0f);
        }
        catch { return; }

        var resourceHeight = (float)tuning.ScreenResourceHeightPixels;
        var rowGap = (float)tuning.ScreenRowGapPixels;
        var primaryElementSize = (float)tuning.ScreenIdentityElementPrimaryPixels;
        var secondaryElementSize = (float)tuning.ScreenIdentityElementSecondaryPixels;
        var identityElementGap = (float)tuning.ScreenIdentityElementGapPixels;
        var statusSize = Mathf.Clamp(anchor.Width * 0.2f, 12f, 26f);
        var identitySize = Mathf.Clamp(anchor.Width * 0.22f, 12f, 28f);
        // Start at the visual bottom. Only rows that actually draw advance the cursor: otherwise an
        // identity-only unit inherits invisible shield/status spacing and appears detached from its sprite.
        var cursorY = 0f;
        var identityY = cursorY - identitySize * 0.5f;
        if (ActorHudRowIdentity.Sync(
                slot,
                snapshot.Identity,
                snapshot.Elements,
                anchor.Width,
                identityY,
                primaryElementSize,
                secondaryElementSize,
                identityElementGap))
        {
            cursorY -= identitySize + rowGap;
        }

        var resourceY = cursorY - resourceHeight * 0.5f;
        if (ActorHudRowResources.Sync(slot, snapshot.Resources?.Shield, anchor.Width, resourceHeight, resourceY, tuning.MaxStackPips))
        {
            _shieldBarsDrawn++;
            var shield = snapshot.Resources!.Shield!;
            if (shield.Max > 0)
            {
                displaySum += ShieldBarVisual.DisplayRatio(shield.Hp, shield.Max);
                trueSum += ShieldBarVisual.TrueRatio(shield.Hp, shield.Max);
            }
            cursorY -= resourceHeight + rowGap;
        }

        if (ActorHudRowStatuses.HasContent(snapshot.Statuses, snapshot.Overflow.StatusCount))
        {
            var statusY = cursorY - statusSize * 0.5f;
            ActorHudRowStatuses.Sync(slot, snapshot.Statuses, snapshot.Overflow.StatusCount, anchor.Width, statusY, tuning.StatusStripMax);
            cursorY -= statusSize + rowGap;
        }
        else
        {
            ActorHudRowStatuses.Sync(slot, snapshot.Statuses, snapshot.Overflow.StatusCount, anchor.Width, 0f, tuning.StatusStripMax);
        }

        _worldHud++;
    }

    internal static void PlaceQuad(Image? image, float left, float localY, float width, float height, Color tint)
    {
        if (image == null) return;
        try
        {
            image.gameObject.SetActive(true);
            image.color = tint;
            var rect = image.GetComponent<RectTransform>();
            if (rect == null) return;
            rect.anchoredPosition = new Vector2(left + width * 0.5f, localY);
            rect.sizeDelta = new Vector2(width, height);
        }
        catch { }
    }

    internal static bool PlaceLabel(ActorHudLabel? label, float localX, float localY, string text, float fontSize, Color color) =>
        label != null && label.TryPlace(localX, localY, text, fontSize, color);

    internal static void HideLabel(ActorHudLabel? label) => label?.Hide();

    static HudSlot? FindLive(string ownerKey) => Slots.FirstOrDefault(s => s.Live && string.Equals(s.OwnerKey, ownerKey, StringComparison.Ordinal));
    static HudSlot? TakeIdle() => Slots.FirstOrDefault(s => !s.Live && s.Root != null);

    static void Release(HudSlot slot)
    {
        slot.Live = false;
        slot.OwnerKey = "";
        try { if (slot.Root != null) slot.Root.SetActive(false); } catch { }
    }

    static HudSlot? CreateSlot()
    {
        if (Slots.Count >= Cap || !EnsureCanvas() || _canvas == null) return null;
        try
        {
            var root = new GameObject("FusionRpgActorHud");
            root.hideFlags = HideFlags.HideAndDontSave;
            root.AddComponent<RectTransform>();
            root.transform.SetParent(_canvas.transform, false);
            var rootRect = root.GetComponent<RectTransform>();
            if (rootRect == null) return null;
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.zero;
            rootRect.pivot = new Vector2(0.5f, 1f);
            root.SetActive(false);

            var slot = new HudSlot { Root = root };
            slot.TierFrame = MakeImage(root, "tier");
            slot.LevelBadge = MakeImage(root, "lvl");
            slot.RolePip = MakeImage(root, "role");
            slot.TierLabel = ActorHudLabel.Create(root, "tierLabel");
            slot.LevelLabel = ActorHudLabel.Create(root, "lvlLabel");
            slot.ShieldTrack = MakeImage(root, "shieldTrack");
            for (var i = 0; i < MaxShieldSegments; i++) slot.ShieldSegments[i] = MakeImage(root, "shieldSeg" + i);
            for (var i = 0; i < MaxPips; i++) slot.StackPips[i] = MakeImage(root, "pip" + i);
            for (var i = 0; i < MaxStatusTokens; i++)
            {
                slot.StatusTokens[i] = MakeImage(root, "status" + i);
                slot.StatusLabels[i] = ActorHudLabel.Create(root, "statusLabel" + i);
            }
            slot.OverflowPip = MakeImage(root, "overflow");
            slot.OverflowLabel = ActorHudLabel.Create(root, "overflowLabel");
            for (var icon = 0; icon < slot.ElementGlyphs.Length; icon++)
            {
                var image = MakeImage(root, $"element{icon}");
                if (image != null) image.preserveAspect = true;
                slot.ElementGlyphs[icon].Icon = image;
            }
            Slots.Add(slot);
            return slot;
        }
        catch (Exception ex)
        {
            try { RpgHost.Log.Warning("[actor-hud] screen slot create: " + ex.Message); } catch { }
            return null;
        }
    }

    static Image? MakeImage(GameObject root, string name)
    {
        try
        {
            var go = new GameObject(name);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<RectTransform>();
            go.transform.SetParent(root.transform, false);
            var rect = go.GetComponent<RectTransform>();
            if (rect == null) return null;
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            var image = go.AddComponent<Image>();
            image.raycastTarget = false;
            go.SetActive(false);
            return image;
        }
        catch { return null; }
    }

    static bool EnsureCanvas()
    {
        if (_canvas != null) return true;
        try
        {
            var root = new GameObject("FusionRpgActorHudCanvas");
            root.hideFlags = HideFlags.HideAndDontSave;
            root.AddComponent<RectTransform>();
            _canvas = root.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = short.MaxValue;
            return true;
        }
        catch (Exception ex)
        {
            try { RpgHost.Log.Warning("[actor-hud] screen canvas create: " + ex.Message); } catch { }
            _canvas = null;
            return false;
        }
    }
}
