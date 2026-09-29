using FusionRpg.Core.Hud;
using UnityEngine;
using UnityEngine.UI;

namespace FusionRpg.Injector.Hud;

/// <summary>Catalog token strip in screen-space pixels.</summary>
static class ActorHudRowStatuses
{
    public static bool HasContent(IReadOnlyList<ActorHudStatusToken> statuses, int overflow) =>
        statuses.Count > 0 || overflow > 0;

    public static void Sync(
        ActorHudPool.HudSlot slot,
        IReadOnlyList<ActorHudStatusToken> statuses,
        int overflow,
        float hudWidth,
        float rowY,
        int maxTokens)
    {
        var showCount = Math.Min(statuses.Count, Math.Min(maxTokens, slot.StatusTokens.Length));
        var tokenSize = Mathf.Clamp(hudWidth * 0.2f, 12f, 26f);
        var gap = tokenSize * 0.15f;
        var overflowOn = overflow > 0 && slot.OverflowPip != null;
        var itemCount = showCount + (overflowOn ? 1 : 0);
        var itemWidths = tokenSize * showCount + (overflowOn ? tokenSize * 0.9f : 0f);
        var left = itemCount > 0
            ? ActorHudScreenLayoutMath.CenteredPackedStart(itemWidths, itemCount, gap)
            : 0f;
        for (var i = 0; i < slot.StatusTokens.Length; i++)
        {
            var image = slot.StatusTokens[i];
            var label = slot.StatusLabels[i];
            if (i >= showCount)
            {
                SetActive(image, false);
                ActorHudPool.HideLabel(label);
                continue;
            }

            var token = statuses[i];
            var resolved = ActorHudDisplayTokens.ResolveStatus(token.Id);
            var tint = ParseCatalogColor(resolved.Color);
            if (token.Cc) tint = Color.Lerp(tint, new Color(1f, 0.45f, 0.2f), 0.35f);
            var x = ActorHudScreenLayoutMath.TakeItemCenter(ref left, tokenSize, gap);
            var labeled = ActorHudPool.PlaceLabel(label, x, rowY, resolved.HudToken, tokenSize * 0.58f, Color.white);
            SetActive(image, labeled);
            if (labeled) ActorHudPool.PlaceQuad(image, x - tokenSize * 0.5f, rowY, tokenSize, tokenSize, tint);
        }

        if (overflowOn)
        {
            var overflowWidth = tokenSize * 0.9f;
            var x = ActorHudScreenLayoutMath.TakeItemCenter(ref left, overflowWidth, gap);
            var labeled = ActorHudPool.PlaceLabel(slot.OverflowLabel, x, rowY, "+" + overflow, tokenSize * 0.46f, Color.white);
            SetActive(slot.OverflowPip, labeled);
            if (labeled) ActorHudPool.PlaceQuad(slot.OverflowPip, x - overflowWidth * 0.5f, rowY, overflowWidth, tokenSize * 0.7f, new Color(0.2f, 0.2f, 0.25f, 0.9f));
        }
        else
        {
            SetActive(slot.OverflowPip, false);
            ActorHudPool.HideLabel(slot.OverflowLabel);
        }
    }

    static Color ParseCatalogColor(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex) && ColorUtility.TryParseHtmlString(hex.Trim(), out var color))
        {
            color.a = 0.92f;
            return color;
        }
        return new Color(168f / 255f, 152f / 255f, 128f / 255f, 0.92f);
    }

    static void SetActive(Image? image, bool on)
    {
        if (image == null) return;
        try { image.gameObject.SetActive(on); } catch { }
    }
}
