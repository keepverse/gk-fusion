using FusionRpg.Core.Hud;
using UnityEngine;
using UnityEngine.UI;

namespace FusionRpg.Injector.Hud;

/// <summary>Identity row, laid out below the resource/status rows in Canvas pixels.</summary>
static class ActorHudRowIdentity
{
    public static bool HasContent(ActorHudIdentity identity) =>
        !string.IsNullOrEmpty(ActorHudDisplayTokens.TierLetter(identity.Tier))
        || identity.LevelBand is int
        || !string.Equals(identity.Role, "vanilla", StringComparison.OrdinalIgnoreCase)
        || identity.Flags.Count > 0;

    public static bool Sync(
        ActorHudPool.HudSlot slot,
        ActorHudIdentity identity,
        ActorHudElements? elements,
        float hudWidth,
        float rowY,
        float primaryElementSize,
        float secondaryElementSize,
        float elementGap)
    {
        var letter = ActorHudDisplayTokens.TierLetter(identity.Tier);
        var hasLetter = !string.IsNullOrEmpty(letter);
        var hasBand = identity.LevelBand is int;
        var roleOn = !string.Equals(identity.Role, "vanilla", StringComparison.OrdinalIgnoreCase);
        var hasElements = ActorHudRowElements.TryResolve(elements, out var resolved);
        if (!HasContent(identity) && !hasElements)
        {
            SetActive(slot.TierFrame, false);
            SetActive(slot.LevelBadge, false);
            SetActive(slot.RolePip, false);
            ActorHudPool.HideLabel(slot.TierLabel);
            ActorHudPool.HideLabel(slot.LevelLabel);
            ActorHudRowElements.Hide(slot.ElementGlyphs[0]);
            ActorHudRowElements.Hide(slot.ElementGlyphs[1]);
            return false;
        }

        var size = Mathf.Clamp(hudWidth * 0.22f, 12f, 28f);
        var pipSize = size * 0.75f;
        var badgeWidth = size * 1.1f;
        var gap = size * 0.15f;
        var hasSecondary = resolved?.Secondary is not null;
        var itemCount = (hasLetter ? 1 : 0) + (roleOn ? 1 : 0) + (hasBand ? 1 : 0) + (hasElements ? 1 : 0) + (hasSecondary ? 1 : 0);
        var itemWidths = (hasLetter ? size : 0f) + (roleOn ? pipSize : 0f) + (hasBand ? badgeWidth : 0f)
                         + (hasElements ? primaryElementSize : 0f) + (hasSecondary ? secondaryElementSize : 0f);
        var left = ActorHudScreenLayoutMath.CenteredPackedStart(itemWidths, itemCount, elementGap);
        var tierCenter = hasLetter ? ActorHudScreenLayoutMath.TakeItemCenter(ref left, size, elementGap) : 0f;
        var roleCenter = roleOn ? ActorHudScreenLayoutMath.TakeItemCenter(ref left, pipSize, elementGap) : 0f;
        var badgeCenter = hasBand ? ActorHudScreenLayoutMath.TakeItemCenter(ref left, badgeWidth, elementGap) : 0f;
        var primaryCenter = hasElements ? ActorHudScreenLayoutMath.TakeItemCenter(ref left, primaryElementSize, elementGap) : 0f;
        var secondaryCenter = hasSecondary ? ActorHudScreenLayoutMath.TakeItemCenter(ref left, secondaryElementSize, elementGap) : 0f;

        if (hasLetter)
        {
            var color = identity.Tier switch
            {
                ActorHudTier.Unique => new Color(1f, 0.78f, 0.32f, 0.95f),
                ActorHudTier.Elite => new Color(0.72f, 0.55f, 0.86f, 0.95f),
                ActorHudTier.Boss => new Color(0.64f, 0.24f, 0.24f, 0.95f),
                _ => new Color(0.55f, 0.55f, 0.6f, 0.75f),
            };
            var labeled = ActorHudPool.PlaceLabel(slot.TierLabel, tierCenter, rowY, letter, size * 0.62f, Color.white);
            SetActive(slot.TierFrame, labeled);
            if (labeled) ActorHudPool.PlaceQuad(slot.TierFrame, tierCenter - size * 0.5f, rowY, size, size, color);
        }
        else
        {
            SetActive(slot.TierFrame, false);
            ActorHudPool.HideLabel(slot.TierLabel);
        }

        if (identity.LevelBand is int band)
        {
            var labeled = ActorHudPool.PlaceLabel(slot.LevelLabel, badgeCenter, rowY, band.ToString(), size * 0.52f, Color.white);
            SetActive(slot.LevelBadge, labeled);
            if (labeled) ActorHudPool.PlaceQuad(slot.LevelBadge, badgeCenter - badgeWidth * 0.5f, rowY, badgeWidth, size * 0.8f, new Color(0.15f, 0.15f, 0.2f, 0.9f));
        }
        else
        {
            SetActive(slot.LevelBadge, false);
            ActorHudPool.HideLabel(slot.LevelLabel);
        }

        SetActive(slot.RolePip, roleOn);
        if (roleOn)
        {
            ActorHudPool.PlaceQuad(slot.RolePip, roleCenter - pipSize * 0.5f, rowY, pipSize, pipSize, new Color(0.35f, 0.65f, 0.95f, 0.9f));
        }

        if (resolved is not null)
        {
            ActorHudRowElements.Draw(slot.ElementGlyphs[0], resolved.Primary, primaryCenter, rowY, primaryElementSize);
            if (resolved.Secondary is not null)
                ActorHudRowElements.Draw(slot.ElementGlyphs[1], resolved.Secondary, secondaryCenter, rowY, secondaryElementSize);
            else
                ActorHudRowElements.Hide(slot.ElementGlyphs[1]);
        }
        else
        {
            ActorHudRowElements.Hide(slot.ElementGlyphs[0]);
            ActorHudRowElements.Hide(slot.ElementGlyphs[1]);
        }

        return true;
    }

    static void SetActive(Image? image, bool on)
    {
        if (image == null) return;
        try { image.gameObject.SetActive(on); } catch { }
    }
}
