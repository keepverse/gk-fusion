using FusionRpg.Core.Hud;
using FusionRpg.Core.Vfx;
using UnityEngine;
using UnityEngine.UI;

namespace FusionRpg.Injector.Hud;

/// <summary>Element-coloured shield segments and pips in screen-space pixels.</summary>
static class ActorHudRowResources
{
    static readonly List<(string? ElementId, long Hp)> ScratchStacks = new(4);
    static readonly List<ShieldBarColor.Stop> ScratchStops = new(4);

    public static bool Sync(
        ActorHudPool.HudSlot slot,
        ActorHudShield? shield,
        float barWidth,
        float barHeight,
        float rowY,
        int maxStackPips)
    {
        if (!OverlaySettings.ShieldBarEnabled || shield == null || shield.Max <= 0 || shield.Hp <= 0)
        {
            HideShield(slot);
            return false;
        }

        ScratchStacks.Clear();
        for (var i = 0; i < shield.Stacks.Count; i++)
            ScratchStacks.Add((shield.Stacks[i].Element, shield.Stacks[i].Hp));
        if (!ShieldBarColor.TryBuildStops(ScratchStacks, ScratchStops))
        {
            HideShield(slot);
            return false;
        }

        SetActive(slot.ShieldTrack, true);
        var barLeft = ActorHudScreenLayoutMath.CenteredPackedStart(barWidth, 1, 0f);
        ActorHudPool.PlaceQuad(slot.ShieldTrack, barLeft, rowY, barWidth, barHeight, new Color(0.08f, 0.08f, 0.1f, 0.85f));
        var fillWidth = barWidth * ShieldBarVisual.DisplayRatio(shield.Hp, shield.Max);
        var left = barLeft;
        for (var i = 0; i < slot.ShieldSegments.Length; i++)
        {
            var segment = slot.ShieldSegments[i];
            if (i >= ScratchStops.Count || fillWidth < 0.01f)
            {
                SetActive(segment, false);
                continue;
            }

            var stop = ScratchStops[i];
            var width = fillWidth * Math.Max(0f, stop.EndU - stop.StartU);
            if (width < 0.01f)
            {
                SetActive(segment, false);
                continue;
            }

            SetActive(segment, true);
            ActorHudPool.PlaceQuad(segment, left, rowY, width, barHeight * 0.85f,
                new Color(stop.R / 255f, stop.G / 255f, stop.B / 255f, 0.95f));
            left += width;
        }

        var pipCap = Math.Clamp(maxStackPips, 0, slot.StackPips.Length);
        var pipCount = Math.Clamp(ScratchStacks.Count, 0, pipCap);
        var pipSize = Mathf.Max(3f, barHeight * 0.8f);
        var gap = pipSize * 1.35f;
        var start = -((pipCount - 1) * gap) * 0.5f;
        for (var i = 0; i < slot.StackPips.Length; i++)
        {
            var pip = slot.StackPips[i];
            if (i >= pipCount)
            {
                SetActive(pip, false);
                continue;
            }

            SetActive(pip, true);
            var stop = ScratchStops[Math.Min(i, ScratchStops.Count - 1)];
            ActorHudPool.PlaceQuad(pip, start + i * gap - pipSize * 0.5f, rowY - barHeight * 0.95f, pipSize, pipSize,
                new Color(stop.R / 255f, stop.G / 255f, stop.B / 255f, 0.95f));
        }

        return true;
    }

    static void HideShield(ActorHudPool.HudSlot slot)
    {
        SetActive(slot.ShieldTrack, false);
        foreach (var segment in slot.ShieldSegments) SetActive(segment, false);
        foreach (var pip in slot.StackPips) SetActive(pip, false);
    }

    static void SetActive(Image? image, bool on)
    {
        if (image == null) return;
        try { image.gameObject.SetActive(on); } catch { }
    }
}
