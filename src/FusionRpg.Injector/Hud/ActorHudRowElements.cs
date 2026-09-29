using FusionRpg.Core.ActorSurface;
using FusionRpg.Core.Hud;
using UnityEngine;

namespace FusionRpg.Injector.Hud;

/// <summary>
/// Compact catalog-driven element glyphs. Species typing arrives as ids on the HUD snapshot; this
/// class owns only the fixed visual vocabulary declared by each catalog row's <c>hudGlyph</c>.
/// </summary>
static class ActorHudRowElements
{
    public sealed record ResolvedElements(ElementSurfaceEntry Primary, ElementSurfaceEntry? Secondary);

    public static bool TryResolve(ActorHudElements? elements, out ResolvedElements? resolved)
    {
        resolved = null;
        if (elements is null || string.IsNullOrWhiteSpace(elements.Primary))
            return false;

        ElementSurfaceEntry? primary;
        ElementSurfaceEntry? secondary = null;
        try
        {
            var catalog = ElementSurfaceCatalogHub.Catalog;
            primary = catalog.Entries.FirstOrDefault(e => string.Equals(e.Id, elements.Primary, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(elements.Secondary))
                secondary = catalog.Entries.FirstOrDefault(e => string.Equals(e.Id, elements.Secondary, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }

        if (!CanDraw(primary) || (!string.IsNullOrWhiteSpace(elements.Secondary) && !CanDraw(secondary)))
            return false;

        resolved = new ResolvedElements(primary!, secondary);
        return true;
    }

    static bool CanDraw(ElementSurfaceEntry? entry) =>
        entry is not null &&
        !entry.PresentationOnly &&
        !string.IsNullOrWhiteSpace(entry.HudGlyph) &&
        ActorHudElementArt.GetSprite(entry.HudGlyph) is not null;

    public static void Draw(ActorHudPool.HudSlot.ElementGlyph glyph, ElementSurfaceEntry entry, float centerX, float centerY, float size)
    {
        Hide(glyph);
        if (glyph.Icon is not { } image || ActorHudElementArt.GetSprite(entry.HudGlyph) is not { } sprite)
            return;
        image.sprite = sprite;
        ActorHudPool.PlaceQuad(image, centerX - size * 0.5f, centerY, size, size, Color.white);
    }

    public static void Hide(ActorHudPool.HudSlot.ElementGlyph glyph)
    {
        if (glyph.Icon != null)
            try { glyph.Icon.gameObject.SetActive(false); } catch { }
    }
}
