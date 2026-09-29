using System.Collections.Concurrent;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

using FusionRpg.Injector.Host;

namespace FusionRpg.Injector;

/// <summary>
/// Dumps every sprite/Image layer on an AlmanacCardUI (frame vs art vs kids)
/// so we can compose the real icon later. Uploads once per (side,type) per process
/// when the server does not already have a dump.
/// </summary>
public static class TypeIconCapture
{
    static readonly ConcurrentDictionary<string, byte> Sent = new(StringComparer.OrdinalIgnoreCase);

    public static void TryCaptureAlmanacCard(string side, int typeId, AlmanacCardUI card)
    {
        if (card == null || typeId < 0) return;
        if (side is not ("plant" or "zombie")) return;
        var key = side + ":" + typeId;
        if (!Sent.TryAdd(key, 0)) return;

        try
        {
            var layers = CollectLayers(card);
            if (layers.Count == 0)
            {
                Sent.TryRemove(key, out _);
                try { RpgHost.Log.Warning($"[icon] no layers on {side}/{typeId}"); } catch { }
                return;
            }

            RpgHost.Client?.EnqueueIconDump(side, typeId, layers);
            try { RpgHost.Log.Info($"[icon] dump queued {side}/{typeId} layers={layers.Count}"); } catch { }
        }
        catch (Exception ex)
        {
            Sent.TryRemove(key, out _);
            try { RpgHost.Log.Warning("[icon] dump failed " + side + "/" + typeId + ": " + ex.Message); } catch { }
        }
    }

    public sealed class Layer
    {
        public string Name = "";
        public string Source = "";
        public int Width;
        public int Height;
        public byte[] Png = Array.Empty<byte>();
    }

    static List<Layer> CollectLayers(AlmanacCardUI card)
    {
        var layers = new List<Layer>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddSprite(string label, string source, Sprite? sprite)
        {
            if (sprite == null) return;
            var png = SpriteToPng(sprite);
            if (png == null || png.Length < 32) return;
            var name = UniqueName(Sanitize(label), usedNames);
            var rect = sprite.textureRect;
            layers.Add(new Layer
            {
                Name = name,
                Source = source,
                Width = Mathf.FloorToInt(rect.width),
                Height = Mathf.FloorToInt(rect.height),
                Png = png
            });
        }

        void AddImage(string label, Image? img)
        {
            if (img == null) return;
            try { AddSprite(label, "Image:" + label, img.sprite); } catch { /* IL2CPP */ }
        }

        try { AddSprite("originalSprite", "AlmanacCardUI.originalSprite", card.originalSprite); } catch { }
        try { AddSprite("selectedSprite", "AlmanacCardUI.selectedSprite", card.selectedSprite); } catch { }
        try { AddImage("image", card.image); } catch { }
        try { AddImage("background", card.background); } catch { }
        try { AddImage("shadowMask", card.shadowMask); } catch { }
        // cost is TextMeshProUGUI — skip

        try
        {
            var images = card.GetComponentsInChildren<Image>(true);
            if (images != null)
            {
                var i = 0;
                foreach (var img in images)
                {
                    if (img == null) { i++; continue; }
                    var go = img.gameObject != null ? img.gameObject.name : ("img" + i);
                    AddImage("childImage_" + Sanitize(go) + "_" + i, img);
                    i++;
                }
            }
        }
        catch (Exception ex)
        {
            try { RpgHost.Log.Warning("[icon] child Images: " + ex.Message); } catch { }
        }

        try
        {
            var srs = card.GetComponentsInChildren<SpriteRenderer>(true);
            if (srs != null)
            {
                var i = 0;
                foreach (var sr in srs)
                {
                    if (sr == null) { i++; continue; }
                    var go = sr.gameObject != null ? sr.gameObject.name : ("sr" + i);
                    AddSprite("childSR_" + Sanitize(go) + "_" + i, "SpriteRenderer", sr.sprite);
                    i++;
                }
            }
        }
        catch (Exception ex)
        {
            try { RpgHost.Log.Warning("[icon] child SpriteRenderers: " + ex.Message); } catch { }
        }

        return layers;
    }

    static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "layer";
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-') sb.Append(c);
            else sb.Append('_');
        }
        var s = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(s) ? "layer" : s;
    }

    static string UniqueName(string baseName, HashSet<string> used)
    {
        if (used.Add(baseName)) return baseName;
        for (var i = 2; i < 1000; i++)
        {
            var n = baseName + "_" + i;
            if (used.Add(n)) return n;
        }
        return baseName + "_" + Guid.NewGuid().ToString("N")[..6];
    }

    static byte[]? SpriteToPng(Sprite sprite)
    {
        var src = sprite.texture;
        if (src == null) return null;

        var rect = sprite.textureRect;
        var w = Mathf.FloorToInt(rect.width);
        var h = Mathf.FloorToInt(rect.height);
        if (w <= 0 || h <= 0) return null;

        var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.Default, RenderTextureReadWrite.sRGB);
        var prev = RenderTexture.active;
        var prevSrgbWrite = GL.sRGBWrite;
        Texture2D? full = null;
        Texture2D? crop = null;
        try
        {
            GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            full = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            full.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            full.Apply();

            var x = Mathf.Clamp(Mathf.FloorToInt(rect.x), 0, Math.Max(0, src.width - 1));
            var y = Mathf.Clamp(Mathf.FloorToInt(rect.y), 0, Math.Max(0, src.height - 1));
            w = Mathf.Min(w, src.width - x);
            h = Mathf.Min(h, src.height - y);
            if (w <= 0 || h <= 0) return null;

            var pixels = full.GetPixels(x, y, w, h);
            crop = new Texture2D(w, h, TextureFormat.RGBA32, false);
            crop.SetPixels(pixels);
            crop.Apply();
            return ImageConversion.EncodeToPNG(crop);
        }
        finally
        {
            GL.sRGBWrite = prevSrgbWrite;
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            if (full != null) Object.Destroy(full);
            if (crop != null) Object.Destroy(crop);
        }
    }
}
