#if FUSIONRPG_MELON
using FusionRpg.Core.Overlay;
using FusionRpg.Injector.Host;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;

namespace FusionRpg.Bridge.Hud;

/// <summary>
/// The clickable Rift tombstone, **inside the game's own menu hierarchy** (map Decisions 17 + 18).
///
/// Decision 17 — attach, never drive: this builds a real uGUI node under the live <c>MainMenu</c>
/// transform. It never calls a <c>UIMgr</c> navigation method and never changes the engine's menu
/// state; the game's own canvas/layout/raycast system owns hit-testing and z-order.
///
/// Decision 18 — one rendering system: the Rift art rides a <see cref="Sprite"/> on the node's
/// <c>Image</c>, and the same node is the button's graphic. The IMGUI menu painter is retired for the
/// menu (the in-match "RPG" button is match chrome and deliberately stays IMGUI).
///
/// The anchor is resolved from the live <c>MainMenu</c> transform, not a scene path. An earlier
/// version of this header asserted that the reference mod's 3.8.1 <c>Grave/…</c> paths "do not exist
/// in 3.9" — <b>that was wrong</b>, disproven by the live <c>debug.dump-all</c> of 2026-09-16, which
/// lists <c>Canvas/MainMenu(Clone)/Grave/GraveBackground/Flower1</c> and friends. It is recorded here
/// because the claim drove two wrong parent choices before anyone dumped the tree.
/// Every failure logs a named warning and leaves the game untouched.
/// </summary>
static class RiftMenuTombstone
{
    const string NodeName = "FusionRpgRiftTombstone";

    /// <summary>
    /// The container the tombstone joins: the <b>canvas root</b>, i.e. the topmost
    /// <c>RectTransform</c> above the menu.
    ///
    /// <para><b>Why the canvas root and nothing inside the menu.</b> <see cref="RiftMenuPlacement"/>
    /// is defined in <b>screen</b> fractions — its own <c>Resolve(screenW, screenH)</c> takes the
    /// screen size — so the fractions only mean what they say when the parent rect IS the screen.
    /// Measured live 2026-09-16 with <c>POST /api/debug/dump-all</c>, which now emits each
    /// <c>RectTransform</c>'s size and screen bounds:</para>
    /// <code>
    /// Canvas                             1921x1080   &lt;- the screen. The correct parent.
    /// Canvas/MainMenu(Clone)              101x100    &lt;- a logical root. Node resolved to 11x16 px.
    /// Canvas/MainMenu(Clone)/Grave        740x586    &lt;- a panel. Node resolved to 81x94 px, on the grave.
    /// </code>
    /// <para>Both earlier parents produced a node that was correctly built, correctly ordered and
    /// sprite-bearing, and still wrong: too small under the menu root, and sitting on the gravestone
    /// under <c>Grave</c>. The rift gate is an <b>independent</b> affordance on the sea, not menu
    /// furniture, so it is not a child of the game's menu panels at all.</para>
    ///
    /// <para>⚠️ Z is NOT the lever for draw order. Unity has a Z axis, but uGUI sorts a Screen-Space
    /// canvas by sibling index and ignores Z — reaching for <c>localPosition.z</c> is the classic wrong
    /// turn on this exact symptom. Last sibling of the canvas root is what puts it above the menu.</para>
    /// </summary>
    // Structural (not tunable): bounds the parent walk. Not the balance surface.
    const int MaxCanvasDepth = 16;


    static GameObject? _node;

    /// <summary>
    /// Builds the affordance under <paramref name="menu"/>. Idempotent: a rebuild reuses the existing
    /// node rather than stacking a second one (the presence patch can re-run on scene reloads).
    /// </summary>
    internal static void Attach(Transform menu)
    {
        try
        {
            if (menu == null)
            {
                RpgHost.Log.Warning("[rift] tombstone not attached: no menu transform");
                return;
            }

            if (_node != null && _node) return;

            ReportMenuShapeOnce(menu);

            // The canvas root — see MaxCanvasDepth's doc. Falls back to the menu itself if the walk
            // ever finds nothing, so this can never be worse than what shipped.
            var anchor = CanvasRootOf(menu) ?? menu;
            var go = new GameObject(NodeName);
            go.transform.SetParent(anchor, worldPositionStays: false);

            // ⚠️ Do NOT AddComponent<RectTransform>() — a GameObject already owns a Transform, and
            // RectTransform derives from it, so adding one is an illegal second Transform. uGUI's own
            // rule is that a Graphic carries [RequireComponent(typeof(RectTransform))], so adding the
            // Image below installs the RectTransform for us and we read it back. This is also what the
            // reference mod does (darkthemer/PvZF_MainMenuFlowers) — it never adds one by hand.
            var image = go.AddComponent<UnityEngine.UI.Image>();
            var rect = go.GetComponent<RectTransform>();
            var n = RiftMenuPlacement.ResolveNormalized();
            // Stretch-anchored to the menu, sized and centred from the tuning group. Normalized
            // anchoring is what makes the node inherit the game canvas's scaling (Decision 18).
            rect.anchorMin = new Vector2(n.MinX, n.MinY);
            rect.anchorMax = new Vector2(n.MaxX, n.MaxY);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            image.sprite = RiftMenuArt.MainSprite();
            image.preserveAspect = true;
            image.raycastTarget = true;   // the game's own canvas raycasts to us

            // ⛔ NO uGUI Button, and no `onClick` listener. This node used to carry a
            // `UnityEngine.UI.Button`; it never fired, and it never could. A uGUI click needs a
            // `GraphicRaycaster` on the Canvas, and the live dump of 2026-09-16 proves this game has
            // none — BOTH canvases read `RectTransform, Canvas, CanvasScaler, Camera, CanvasGroup` and
            // nothing else. PvZ Fusion's own menu buttons are `BoxCollider2D`/`PolygonCollider2D` plus
            // `UIButton_mainMenu`, i.e. a physics-collider input path, not the EventSystem.
            //
            // Adding a GraphicRaycaster to the game's Canvas would switch uGUI raycasting on for every
            // node under it — that is driving the menu, which Decision 17 forbids. So the affordance
            // owns its own hit test (see Tick), over its own rect only, and the engine is untouched.
            // This corrects Decision 17's line that "hit-testing now belongs to the game's own uGUI
            // rect/raycast": that assumed a raycaster the game does not have.

            // ⛔ No mini companion. `RiftMenuOverlayLayout.MiniCompanion` composed a SECOND, smaller
            // rift sprite beside the main one — authored for the retired IMGUI painter, where it read
            // as one two-piece illustration. As a real uGUI child of an independent menu button it
            // reads as exactly what it is: a duplicate rift floating next to the button. Reported live
            // 2026-09-16 ("we have double rift") with a screenshot. The gate is ONE button.

            _node = go;
            _menu = menu;
            // ⚠️ AFTER the assignment: this used to run one line earlier, while `_node` was still
            // null, so its own `if (_node == null) return;` guard made it a no-op every time and the
            // sibling ordering never applied.
            OrderOnTopOfMenuArt(anchor);
            // Size is the last thing that can make a correctly-parented, correctly-ordered, sprite-bearing
            // node invisible: anchorMin/Max are fractions OF THE PARENT, so a parent rect of zero area
            // yields a child of zero area no matter what the fractions say.
            var parentRect = anchor.GetComponent<RectTransform>();
            RpgHost.Log.Info(
                $"[rift] tombstone attached under '{anchor.name}' — parent {(parentRect != null ? parentRect.rect.width + "x" + parentRect.rect.height : "no-rect")}"
                + $", node {rect.rect.width}x{rect.rect.height}"
                + $", sprite {(image.sprite != null ? "ok" : "NULL")}, anchors {n.MinX},{n.MinY}-{n.MaxX},{n.MaxY}");
        }
        catch (Exception ex)
        {
            RpgHost.Log.Warning("[rift] tombstone attach failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Where the node actually parents. A uGUI <c>Image</c> only renders inside a <c>Canvas</c>, so the
    /// anchor must be a transform that already lives under one — parenting to <c>MainMenu.transform</c>
    /// renders nothing when the menu root is not itself a canvas child.
    ///
    /// <para>⚠️ This file's header used to assert that *"Task 1 verified the reference mod's 3.8.1 paths
    /// (`Grave/…`) do not exist in 3.9"*. That claim is what this method tests instead of trusting:
    /// the reference mod (darkthemer/PvZF_MainMenuFlowers) is a **PvZ Fusion** mod and navigates
    /// <c>Grave/GraveBackground/Flower1</c> and <c>Grave/LowerButtons</c> off <c>MainMenu.transform</c>.
    /// Rather than pick a side on a claim nobody can re-check from source, the candidates are tried in
    /// order and the winner is logged, so one live boot settles it permanently.</para>
    ///
    /// <para>The fallback is the menu root itself — the previous behaviour — so this can only improve on
    /// what shipped, never regress it.</para>
    /// </summary>
    static bool _reportedShape;

    /// <summary>
    /// One-shot dump of the live MainMenu hierarchy — name, depth, rect size, uGUI Image, sibling
    /// index. Written because probing one field per boot is the slow way to answer "where can a node
    /// actually be seen": the whole tree in one log line settles parent choice, draw order and size
    /// together. Read it before moving the tombstone again.
    /// </summary>
    static void ReportMenuShapeOnce(Transform menu)
    {
        if (_reportedShape) return;
        _reportedShape = true;
        try
        {
            var sb = new System.Text.StringBuilder();
            Walk(menu, 0, sb, 0);
            RpgHost.Log.Info("[rift] MainMenu tree:" + Environment.NewLine + sb);
        }
        catch (Exception ex) { RpgHost.Log.Warning("[rift] menu dump failed: " + ex.Message); }
    }

    static void Walk(Transform t, int depth, System.Text.StringBuilder sb, int index)
    {
        if (depth > 3) return;
        var rt = t.GetComponent<RectTransform>();
        var img = t.GetComponent<UnityEngine.UI.Image>() != null ? " IMG" : "";
        var size = rt != null ? $"{rt.rect.width:F0}x{rt.rect.height:F0}" : "no-rect";
        var active = t.gameObject.activeSelf ? "" : " (inactive)";
        sb.Append(new string(' ', depth * 2))
          .Append('[').Append(index).Append("] ")
          .Append(t.name).Append("  ").Append(size).Append(img).Append(active).AppendLine();
        for (var i = 0; i < t.childCount && i < 30; i++)
            Walk(t.GetChild(i), depth + 1, sb, i);
    }

    /// <summary>
    /// The topmost <c>RectTransform</c> above <paramref name="menu"/> — the canvas root, whose rect is
    /// the screen. Walks by rect, not by name, so it does not depend on the root being called "Canvas".
    /// </summary>
    static Transform? CanvasRootOf(Transform menu)
    {
        Transform? root = null;
        var depth = 0;
        for (var p = menu; p != null && depth++ < MaxCanvasDepth; p = p.parent)
        {
            RectTransform? rt = null;
            try { rt = p.GetComponent<RectTransform>(); } catch { }
            if (rt != null) root = p;
        }
        return root;
    }

    /// <summary>
    /// The menu this node was built for. Parenting to the canvas root means the node no longer dies
    /// with the menu, so its lifetime is ours to manage: <see cref="TickLiveness"/> destroys it once
    /// the menu it belongs to is gone. This is the non-virtual clearance path that
    /// <c>InjectorBootstrap.IsRiftMenuVirtualPatch</c> asks for — patching <c>BaseMenu.OnHide</c> /
    /// <c>OnExit</c> crashes boot (virtual Il2Cpp method, vtable trampoline recursion, 0xc00000fd), so
    /// clearance is a liveness READ instead of a hook.
    /// </summary>
    static Transform? _menu;

    /// <summary>
    /// Liveness + the node's own click test, once per frame. Cheap: two null reads in the common case,
    /// and the hit test only runs on a frame where the left button actually went down.
    /// </summary>
    internal static void Tick()
    {
        try
        {
            if (_node == null || !_node) { _node = null; _menu = null; return; }

            var alive = _menu != null && _menu && _menu.gameObject.activeInHierarchy;
            if (!alive)
            {
                UnityEngine.Object.Destroy(_node);
                _node = null;
                _menu = null;
                return;
            }

            if (!Input.GetMouseButtonDown(0)) return;

            // The SAME rect the node is drawn from — RiftMenuPlacement is the one placement source, so
            // the art and the hit box cannot drift (that is why Resolve and ResolveNormalized live side
            // by side). Screen pixels, origin bottom-left, which is also Input.mousePosition's origin.
            var r = RiftMenuPlacement.Resolve(Screen.width, Screen.height);
            var m = Input.mousePosition;
            if (m.x < r.X || m.x > r.Right || m.y < r.Y || m.y > r.Bottom) return;

            // Logged on every hit, not once: "nothing happened" has two very different causes — the hit
            // test never fired, or it fired and the toggle did nothing — and only this line separates
            // them. It costs a log write per deliberate click on our own rect.
            RpgHost.Log.Info($"[rift] gate clicked at {m.x},{m.y} in rect {r.X},{r.Y}-{r.Right},{r.Bottom}");
            OnClick();
        }
        catch (Exception ex) { RpgHost.Log.Warning("[rift] tombstone tick failed: " + ex.Message); }
    }

    /// <summary>Clears the cached node when the menu object goes away, so a later build re-attaches.</summary>
    internal static void OnMenuGone() => _node = null;

    static void OnClick()
    {
        // The SAME request path the in-match button and the hotkey use — no second toggle path.
        try { OverlaySwitch.RequestToggle(); }
        catch (Exception ex) { RpgHost.Log.Warning("[rift] tombstone click failed: " + ex.Message); }
    }

    /// <summary>
    /// Sibling order decides both paint order and raycast priority for an in-hierarchy element.
    ///
    /// <para>⚠️ This used to call <c>SetAsFirstSibling()</c>, reasoning that drawing behind the game's
    /// own buttons keeps their clicks theirs. That reads uGUI backwards: a Canvas paints children in
    /// hierarchy order, so the FIRST sibling paints FIRST and every later sibling paints over it.
    /// First-sibling did not put the tombstone behind the buttons — it put it behind the menu's own
    /// opaque backdrop, which is why the node attached successfully (the log line fires) and nothing
    /// was ever visible on screen.</para>
    ///
    /// <para>Last sibling is correct for an affordance that must be seen and clicked. It does not take
    /// clicks from the game's buttons: uGUI raycasts by rect, and this node occupies its own
    /// <see cref="RiftMenuPlacement"/> rect — a pointer over a game button is not over us. The mini
    /// companion already carries <c>raycastTarget = false</c> so decoration never competes.</para>
    /// </summary>
    static void OrderOnTopOfMenuArt(Transform menu)
    {
        if (_node == null || !_node) return;
        _node.transform.SetAsLastSibling();
    }
}
#endif
