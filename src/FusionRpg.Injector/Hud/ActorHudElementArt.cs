using FusionRpg.Injector.Host;
using UnityEngine;

namespace FusionRpg.Injector.Hud;

/// <summary>
/// Loads the authored element glyph selected by the injected element catalog. The uGUI presenter
/// sizes the resulting sprite from HUD tuning; this cache only owns immutable texture/sprite data.
/// </summary>
static class ActorHudElementArt
{
    const string AssetDirectory = "assets/actor-hud-elements";

    static readonly Dictionary<string, Sprite?> Sprites = new(StringComparer.Ordinal);

    internal static Sprite? GetSprite(string? hudGlyph)
    {
        if (string.IsNullOrWhiteSpace(hudGlyph)) return null;
        if (Sprites.TryGetValue(hudGlyph, out var existing)) return existing;

        Sprite? sprite = null;
        try
        {
            // hudGlyph is catalog data, not a path. Reject separators and extensions before combining it
            // with the game-local asset directory.
            if (!string.Equals(Path.GetFileNameWithoutExtension(hudGlyph), hudGlyph, StringComparison.Ordinal))
                throw new InvalidDataException("glyph key must be a bare filename");

            var path = Path.Combine(RpgHost.PluginDir, AssetDirectory, hudGlyph + ".png");
            if (!File.Exists(path))
                throw new FileNotFoundException("icon asset is missing", path);

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "FusionRpgActorHudElement_" + hudGlyph,
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
            };

            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), markNonReadable: true))
            {
                UnityEngine.Object.Destroy(texture);
                throw new InvalidDataException("icon asset is not a readable PNG");
            }

            sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                64f);
            sprite.name = "FusionRpgActorHudElement_" + hudGlyph;
            sprite.hideFlags = HideFlags.HideAndDontSave;
        }
        catch (Exception ex)
        {
            RpgHost.Log.Warning("[actor-hud] element icon '" + hudGlyph + "' unavailable: " + ex.Message);
        }

        Sprites[hudGlyph] = sprite;
        return sprite;
    }
}
