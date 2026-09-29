using FusionRpg.Core.Vfx;
using FusionRpg.Injector.Host;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FusionRpg.Injector.Fx;

/// <summary>
/// Shared shader / material / texture cache — vfx-ssot.md §10. Wraps <see cref="OverlayShaderProbe"/>.
/// Texture is ALWAYS the generated soft disc: the v1 "steal a vanilla particle texture" idea
/// grabbed arbitrary scene imagery (electric/lightning sheets — LIVE finding 2026-08-21) and is gone.
/// </summary>
public static class FxResources
{
    /// <summary>Render ordering for overlay particles — one named home for the old magic 80.
    /// Config-backed (tunables-ssot.md T1) — gk-core/data/tuning/vfx.v1.json's render.particleSortingOrder.</summary>
    public static int ParticleSortingOrder =>
        VfxTuningHub.Tuning.Render.ParticleSortingOrder + VfxTuningHub.Tuning.Render.SortOffsetAboveUnit;

    static Material? _particleMat;
    static Texture2D? _softDisc;
    static string _matShaderName = "";
    static readonly Dictionary<Core.Vfx.VfxMarkerShape, Material> MarkerMats = new();
    static readonly Dictionary<Core.Vfx.VfxMarkerShape, Texture2D> MarkerTextures = new();
    static string _markerShaderName = "";
    const string EarthImpactStampResource = "FusionRpg.Injector.Assets.ElementalActionVfx.Earth.V2.impact-256x256.png";
    static Sprite? _earthImpactStamp;
    static Material? _earthImpactStampMat;
    static string _earthImpactStampShaderName = "";
    static bool _earthImpactStampLoadAttempted;
    static readonly Dictionary<string, Sprite> EarthPhaseSprites = new(StringComparer.Ordinal);
    static readonly Dictionary<string, Material> EarthPhaseMaterials = new(StringComparer.Ordinal);
    static readonly HashSet<string> EarthPhaseAttempts = new(StringComparer.Ordinal);

    /// <summary>Loads an approved Earth V1 phase once; render pools only consume the cached result.</summary>
    public static bool TryGetEarthPhase(string phase, out Sprite sprite, out Material material)
    {
        sprite = null!; material = null!;
        var normalized = phase is "charge" or "travel" ? phase : "";
        if (normalized.Length == 0) return false;
        if (!EarthPhaseSprites.TryGetValue(normalized, out var loaded) && EarthPhaseAttempts.Add(normalized))
        {
            try
            {
                var resource = "FusionRpg.Injector.Assets.ElementalActionVfx.Earth." + normalized +
                    (normalized == "travel" ? "-256x64.png" : "-256x256.png");
                using var stream = typeof(FxResources).Assembly.GetManifestResourceStream(resource);
                if (stream == null) throw new InvalidDataException("embedded Earth " + normalized + " is missing");
                using var bytes = new MemoryStream(); stream.CopyTo(bytes);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                if (!ImageConversion.LoadImage(texture, bytes.ToArray(), markNonReadable: true)) throw new InvalidDataException("invalid Earth " + normalized + " PNG");
                loaded = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 256f);
                loaded.hideFlags = HideFlags.HideAndDontSave;
                EarthPhaseSprites[normalized] = loaded;
            }
            catch (Exception ex) { try { RpgHost.Log.Warning("[vfx.earth] " + ex.Message); } catch { } }
        }
        if (loaded == null) return false;
        var shader = OverlayShaderProbe.DrawShader();
        if (shader == null) return false;
        if (!EarthPhaseMaterials.TryGetValue(normalized, out var cached))
        {
            cached = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, mainTexture = loaded.texture };
            EarthPhaseMaterials[normalized] = cached;
        }
        sprite = loaded; material = cached; return true;
    }

    /// <summary>Loads the embedded Earth V2 stamp once, then shares one sprite/material across the
    /// bounded stamp pool. The resource is part of every injector host, never read from the game
    /// directory and never decoded on a cue path after its first successful load.</summary>
    public static bool TryGetEarthImpactStamp(out Sprite sprite, out Material material)
    {
        sprite = null!;
        material = null!;
        if (_earthImpactStamp == null && !_earthImpactStampLoadAttempted)
        {
            _earthImpactStampLoadAttempted = true;
            try
            {
                using var stream = typeof(FxResources).Assembly.GetManifestResourceStream(EarthImpactStampResource);
                if (stream == null) throw new InvalidDataException("embedded Earth stamp is missing");
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    name = "FusionRpgEarthImpactStamp",
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                if (!ImageConversion.LoadImage(texture, bytes.ToArray(), markNonReadable: true)
                    || texture.width != 256 || texture.height != 256)
                {
                    UObject.Destroy(texture);
                    throw new InvalidDataException("embedded Earth stamp must be a readable 256x256 PNG");
                }

                _earthImpactStamp = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f), 256f);
                _earthImpactStamp.name = "FusionRpgEarthImpactStamp";
                _earthImpactStamp.hideFlags = HideFlags.HideAndDontSave;
            }
            catch (Exception ex)
            {
                try { RpgHost.Log.Warning("[vfx.stamp] asset: " + ex.Message); } catch { }
            }
        }

        if (_earthImpactStamp == null) return false;
        var shader = OverlayShaderProbe.DrawShader();
        var shaderName = OverlayShaderProbe.DrawShaderName();
        if (shader == null || string.IsNullOrEmpty(shaderName)) return false;
        if (_earthImpactStampMat == null || !string.Equals(_earthImpactStampShaderName, shaderName, StringComparison.Ordinal))
        {
            try
            {
                _earthImpactStampMat = new Material(shader)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    mainTexture = _earthImpactStamp.texture
                };
                try { _earthImpactStampMat.SetColor("_Color", Color.white); } catch { }
                _earthImpactStampShaderName = shaderName;
            }
            catch { return false; }
        }

        sprite = _earthImpactStamp;
        material = _earthImpactStampMat;
        return true;
    }

    /// <summary>Cached particle material, or null when no shipped shader survives stripping.</summary>
    public static Material? ParticleMaterial()
    {
        var shader = OverlayShaderProbe.DrawShader();
        var name = OverlayShaderProbe.DrawShaderName();
        if (shader == null || string.IsNullOrEmpty(name))
        {
            _particleMat = null;
            _matShaderName = "";
            return null;
        }

        if (_particleMat != null && string.Equals(_matShaderName, name, StringComparison.Ordinal))
            return _particleMat;

        try
        {
            _particleMat = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave,
                mainTexture = SoftDisc()
            };
            try { _particleMat.SetColor("_Color", Color.white); } catch { }
            try { _particleMat.SetColor("_TintColor", new Color(1f, 1f, 1f, 0.6f)); } catch { }
            _matShaderName = name;
            return _particleMat;
        }
        catch
        {
            _particleMat = null;
            _matShaderName = "";
            return null;
        }
    }

    /// <summary>Cached material for a procedurally generated marker shape (vfx-v3 M5).</summary>
    public static Material? MarkerMaterial(Core.Vfx.VfxMarkerShape shape)
    {
        var shader = OverlayShaderProbe.DrawShader();
        var name = OverlayShaderProbe.DrawShaderName();
        if (shader == null || string.IsNullOrEmpty(name)) return null;
        if (!string.Equals(_markerShaderName, name, StringComparison.Ordinal))
        {
            MarkerMats.Clear();
            _markerShaderName = name;
        }

        if (MarkerMats.TryGetValue(shape, out var cached) && cached != null) return cached;
        try
        {
            var mat = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave,
                mainTexture = MarkerTexture(shape)
            };
            try { mat.SetColor("_Color", Color.white); } catch { }
            MarkerMats[shape] = mat;
            return mat;
        }
        catch
        {
            return null;
        }
    }

    static Texture2D MarkerTexture(Core.Vfx.VfxMarkerShape shape)
    {
        if (MarkerTextures.TryGetValue(shape, out var cached) && cached != null) return cached;
        // Config-backed (tunables-ssot.md T1) — gk-core/data/tuning/vfx.v1.json's render.particleTextureSize.
        var size = VfxTuningHub.Tuning.Render.ParticleTextureSize;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = "FusionRpgMarker" + shape
        };
        var c = (size - 1) * 0.5f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var nx = (x - c) / c; // -1..1
                var ny = (y - c) / c;
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, ShapeAlpha(shape, nx, ny)));
            }
        }

        tex.Apply(false, true);
        MarkerTextures[shape] = tex;
        return tex;
    }

    static float ShapeAlpha(Core.Vfx.VfxMarkerShape shape, float nx, float ny)
    {
        var edge = (float)VfxTuningHub.Tuning.Render.MarkerEdgeSoftness;
        var glow = (float)VfxTuningHub.Tuning.Render.MarkerGlowStrength;
        var core = shape switch
        {
            Core.Vfx.VfxMarkerShape.Diamond => DiamondAlpha(nx, ny, edge),
            Core.Vfx.VfxMarkerShape.TriangleDown => ChevronAlpha(nx, ny, edge),
            Core.Vfx.VfxMarkerShape.Cross => CrossAlpha(nx, ny, edge),
            _ => RingAlpha(nx, ny, edge)
        };
        return Mathf.Clamp01(core + glow * OuterHalo(nx, ny, shape, edge));
    }

    static float OuterHalo(float nx, float ny, Core.Vfx.VfxMarkerShape shape, float edge)
    {
        var d = MathF.Sqrt(nx * nx + ny * ny);
        return shape switch
        {
            Core.Vfx.VfxMarkerShape.Diamond => SoftDiamond(nx, ny, 0.92f, edge * 2.4f) * 0.55f,
            Core.Vfx.VfxMarkerShape.TriangleDown => ChevronAlpha(nx, ny, edge * 2.2f) * 0.45f,
            Core.Vfx.VfxMarkerShape.Cross => CrossAlpha(nx, ny, edge * 2f) * 0.4f,
            _ => Mathf.Clamp01((0.92f - d) / (edge * 2.5f)) * 0.35f
        };
    }

    static float SoftRing(float nx, float ny, float radius, float thickness, float edge) =>
        RingStroke(MathF.Sqrt(nx * nx + ny * ny), radius, thickness, edge);

    static float RingStroke(float d, float radius, float thickness, float edge) =>
        Mathf.Clamp01((thickness * 0.5f - MathF.Abs(d - radius)) / edge);

    static float SoftDiamond(float nx, float ny, float radius, float edge)
    {
        var d = MathF.Abs(nx) + MathF.Abs(ny);
        return Mathf.Clamp01((radius - d) / edge);
    }

    static float RingAlpha(float nx, float ny, float edge)
    {
        var d = MathF.Sqrt(nx * nx + ny * ny);
        var outer = RingStroke(d, 0.62f, 0.11f, edge);
        var inner = RingStroke(d, 0.42f, 0.07f, edge) * 0.55f;
        var center = Mathf.Clamp01((0.18f - d) / (edge * 1.8f)) * 0.35f;
        var sparkle = RingStroke(d, 0.72f, 0.05f, edge * 1.4f) * 0.4f;
        return Mathf.Clamp01(outer + inner + center + sparkle);
    }

    static float DiamondAlpha(float nx, float ny, float edge)
    {
        var outer = SoftDiamond(nx, ny, 0.78f, edge);
        var main = SoftDiamond(nx, ny, 0.52f, edge * 0.85f);
        var inner = SoftDiamond(nx * 1.08f, ny * 1.08f, 0.28f, edge * 0.7f) * 0.85f;
        var spine = Mathf.Clamp01((0.07f - MathF.Abs(nx)) / (edge * 0.75f)) *
                    SoftDiamond(nx, ny, 0.55f, edge * 1.2f) * 0.5f;
        return Mathf.Clamp01(outer * 0.55f + main + inner + spine);
    }

    static float ChevronAlpha(float nx, float ny, float edge)
    {
        // Structural, not tunable (tunables-ssot T2): the geometry of the chevron sprite in normalised
        // [-1, 1] texture space (apex, top edge, top half-width). A procedural shape, not a gameplay number.
        const float apexY = -0.72f;
        const float topY = 0.48f;
        const float topHalfW = 0.58f;
        if (ny > topY + edge || ny < apexY - edge) return 0f;
        var t = (ny - apexY) / (topY - apexY);
        if (t is < 0f or > 1f) return 0f;
        var halfW = topHalfW * t;
        var body = Mathf.Clamp01((halfW - MathF.Abs(nx)) / edge);
        var spine = Mathf.Clamp01((0.07f - MathF.Abs(nx)) / (edge * 0.8f)) *
                    Mathf.Clamp01((halfW * 0.55f - MathF.Abs(nx)) / (edge * 1.1f)) * 0.65f;
        var tip = Mathf.Clamp01((0.12f - MathF.Sqrt(nx * nx + (ny - apexY) * (ny - apexY))) / (edge * 0.9f)) * 0.75f;
        return Mathf.Clamp01(body + spine + tip);
    }

    static float CrossAlpha(float nx, float ny, float edge)
    {
        if (MathF.Abs(nx) > 0.82f || MathF.Abs(ny) > 0.82f) return 0f;
        var arm = MathF.Min(MathF.Abs(nx), MathF.Abs(ny));
        var core = Mathf.Clamp01((0.16f - arm) / edge);
        var diagonal = Mathf.Clamp01((0.11f - MathF.Abs(MathF.Abs(nx) - MathF.Abs(ny))) / (edge * 1.2f)) * 0.45f;
        return Mathf.Clamp01(core + diagonal);
    }

    static Texture2D SoftDisc()
    {
        if (_softDisc != null) return _softDisc;
        // Config-backed (tunables-ssot.md T1) — gk-core/data/tuning/vfx.v1.json's render.particleTextureSize.
        var size = VfxTuningHub.Tuning.Render.ParticleTextureSize;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = "FusionRpgSoftDisc"
        };
        var cx = (size - 1) * 0.5f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = (x - cx) / cx;
                var dy = (y - cx) / cx;
                var d = Mathf.Sqrt(dx * dx + dy * dy);
                var a = Mathf.Clamp01(1f - d);
                a *= a;
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }

        tex.Apply(false, true);
        _softDisc = tex;
        return tex;
    }
}
