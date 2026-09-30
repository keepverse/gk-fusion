#if FUSIONRPG_MELON
using FusionRpg.Injector.Host;
using UnityEngine;

namespace FusionRpg.Injector.Hud;

/// <summary>
/// Turns the authored Rift art into a <see cref="Sprite"/> for a uGUI <see cref="UnityEngine.UI.Image"/>
/// (map Decision 18: the menu is one rendering system — uGUI).
///
/// The PNG→<c>Texture2D</c> half already ships (the <c>ImageConversion.LoadImage</c> shape the retired
/// IMGUI paintery used); the genuinely new call is <c>Sprite.Create</c>, which appears nowhere else in
/// this injector. <c>Sprite.Create</c> is done once and cached: the texture is loaded with
/// <c>markNonReadable: true</c>, which is exactly what a Sprite needs and what keeps the pixel copy off
/// the managed heap for the life of the session.
///
/// Failure is always a named warning and a null sprite — never a throw into the menu, never a silent
/// no-op that leaves an invisible button.
/// </summary>
static class RiftMenuArt
{
    const string MainAssetRelativePath = "assets/onboarding/rift-portal-pvz-style.png";

    static Texture2D? _texture;
    static Sprite? _sprite;
    static bool _loadAttempted;


    /// <summary>The Rift sprite, or null when the art is missing/unreadable (a warning is logged once).</summary>
    internal static Sprite? MainSprite()
    {
        if (_sprite != null) return _sprite;
        if (_loadAttempted) return null;
        _loadAttempted = true;

        try
        {
            _texture = LoadTexture();
            if (_texture == null) return null;

            // The one genuinely new call for this feature. pivot 0.5/0.5 so the RectTransform anchors
            // behave predictably; pixelsPerUnit 100 is the Unity default and does not matter because the
            // node is sized by its RectTransform, not by world units.
            _sprite = Sprite.Create(
                _texture,
                new Rect(0f, 0f, _texture.width, _texture.height),
                new Vector2(0.5f, 0.5f),
                100f);
            _sprite.name = "FusionRpgRiftMenuSprite";
            _sprite.hideFlags = HideFlags.HideAndDontSave;
            return _sprite;
        }
        catch (Exception ex)
        {
            RpgHost.Log.Warning("[rift] menu sprite creation failed: " + ex.Message);
            return null;
        }
    }

    static Texture2D? LoadTexture()
    {
        var path = Path.Combine(RpgHost.PluginDir, MainAssetRelativePath);
        if (!File.Exists(path))
        {
            RpgHost.Log.Warning($"[rift] menu art unavailable: {path}");
            return null;
        }

        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
        {
            name = "FusionRpgRiftMenuTexture",
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
        };

        if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), markNonReadable: true))
        {
            UnityEngine.Object.Destroy(texture);
            RpgHost.Log.Warning($"[rift] menu art is not a readable PNG: {path}");
            return null;
        }

        return texture;
    }

}
#endif
