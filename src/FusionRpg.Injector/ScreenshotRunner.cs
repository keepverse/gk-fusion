using FusionRpg.Injector.Host;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FusionRpg.Injector;

/// <summary>
/// End-of-frame screenshot runner (live-probe module <c>lawn-screenshot</c>).
///
/// Design history, kept so nobody re-tries the two dead ends: (1) capturing inline in the
/// cheat drain is mid-frame — Unity calls that undefined; (2) a dedicated hidden
/// <c>MonoBehaviour</c> with an engine-driven coroutine does not survive this game's IL2CPP
/// interop — <c>StartCoroutine(IEnumerator)</c> is absent and a string-driven coroutine's
/// managed enumerator is rejected at injection time. What remains is the path the game
/// already owns: both hosts call <c>VfxDirector.Draw()</c> from <c>OnGUI</c>, and the Repaint
/// pass runs after every Camera + overlay Canvas has rendered. A backbuffer
/// <c>ReadPixels</c> there captures the presented frame (uGUI overlays included; only IMGUI
/// drawn after the read is missed — our own debug floaters, negligible for an eyeball
/// check). No GameObject, no coroutine, no per-host code — both shims stay thin.
/// </summary>
public static class ScreenshotRunner
{
    static int _busy;
    static string? _pendingTag;
    static long _armedAtMs;
    static long _repaintsSeen;

    /// <summary>live-probe 2026-09-15: a capture armed on a host whose OnGUI never delivers a Repaint
    /// (observed live on MelonLoader pvzrh-3.9 — every request stuck "already in flight" for the whole
    /// session) used to wedge the latch forever. After this long with no Repaint, <see cref="TickFallback"/>
    /// captures through the camera render-to-texture path instead, which is legal outside Repaint.
    /// Structural (not tunable): long enough for a normal Repaint (one frame), short enough for a probe.</summary>
    const long RepaintGraceMs = 2000;

    // PNG signature — proves the emitted bytes are a real PNG without viewing them.
    static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <returns>False when a capture is already armed or in flight. Never throws.</returns>
    public static bool Arm(string tag)
    {
        try
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                CheatState.Note("debug screenshot already in flight");
                return false;
            }
            Volatile.Write(ref _armedAtMs, Environment.TickCount64);
            Volatile.Write(ref _pendingTag, tag);
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _busy, 0);
            CheatState.Error("debug.screenshot arm: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Called from <c>VfxDirector.Draw()</c> on the Repaint pass only. Consumes one armed
    /// request; a second arm while this runs is refused by <see cref="Arm"/>.
    /// </summary>
    public static void CaptureOnRepaint()
    {
        Interlocked.Increment(ref _repaintsSeen);
        var tag = Interlocked.Exchange(ref _pendingTag, null);
        if (tag == null) return;
        Capture(tag, fromRepaint: true);
    }

    /// <summary>Called every <c>InjectorLoop.Tick</c>. Takes over an armed capture that no Repaint has
    /// consumed within <see cref="RepaintGraceMs"/> — camera render-to-texture only (the backbuffer is not
    /// readable mid-frame), so uGUI overlays may be missing; the emit says which primitive ran.</summary>
    public static void TickFallback()
    {
        if (Volatile.Read(ref _pendingTag) == null) return;
        if (Environment.TickCount64 - Volatile.Read(ref _armedAtMs) < RepaintGraceMs) return;
        var tag = Interlocked.Exchange(ref _pendingTag, null);
        if (tag == null) return;
        Capture(tag, fromRepaint: false);
    }

    static void Capture(string tag, bool fromRepaint)
    {
        try
        {
            string primitive;
            int width, height;
            byte[]? png;
            if (fromRepaint)
                png = CapturePng(out width, out height, out primitive);
            else
            {
                primitive = "camera-fallback-no-repaint";
                png = CaptureCameraFallback(out width, out height);
            }
            if (png == null || png.Length < 32 || !HasPngSignature(png))
            {
                CheatState.Error("debug.screenshot: capture produced no PNG");
                return;
            }
            RpgHost.Client?.EnqueueScreenshot(png, tag);
            DebugRuntime.Emit("debug.screenshot.ready", new Dictionary<string, object>
            {
                ["tag"] = tag,
                ["width"] = width,
                ["height"] = height,
                ["bytes"] = png.Length,
                ["validPng"] = true,
                ["primitive"] = primitive,
                ["repaintsSeen"] = Interlocked.Read(ref _repaintsSeen)
            });
            CheatState.Note($"debug screenshot {tag} {width}x{height} {png.Length}B via {primitive}");
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.screenshot capture: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    static byte[]? CapturePng(out int width, out int height, out string primitive)
    {
        width = 0;
        height = 0;
        var png = ScreenReadback(out width, out height);
        if (png != null)
        {
            primitive = "screen-readback";
            return png;
        }
        // Backbuffer unreadable (odd resolution, lost device) — single-camera render.
        // Misses UI overlays; the emit says so.
        primitive = "camera-fallback";
        return CaptureCameraFallback(out width, out height);
    }

    /// <summary>Backbuffer readback (final presented frame, UI included).</summary>
    static byte[]? ScreenReadback(out int width, out int height)
    {
        width = Screen.width;
        height = Screen.height;
        if (width <= 0 || height <= 0) return null;
        Texture2D? tex = null;
        try
        {
            tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            if (width <= ScreenshotCapture.MaxCaptureWidth)
                return ImageConversion.EncodeToPNG(tex);
            return DownscaleEncode(tex, out width, out height);
        }
        catch (Exception ex)
        {
            try { RpgHost.Log.Warning("[screenshot] readback failed: " + ex.Message); } catch { }
            return null;
        }
        finally
        {
            if (tex != null) UObject.Destroy(tex);
        }
    }

    static byte[]? DownscaleEncode(Texture2D src, out int width, out int height)
    {
        var scale = (float)ScreenshotCapture.MaxCaptureWidth / src.width;
        var w = Math.Max(1, Mathf.FloorToInt(src.width * scale));
        var h = Math.Max(1, Mathf.FloorToInt(src.height * scale));
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.Default, RenderTextureReadWrite.sRGB);
        var prev = RenderTexture.active;
        var prevSrgbWrite = GL.sRGBWrite;
        Texture2D? small = null;
        try
        {
            // Graphics.Blit does not guarantee the sRGB write state. In a Linear project,
            // leaving it disabled stores linear values in the sRGB target and makes midtones
            // appear too dark in the emitted PNG. Match Unity's active color space explicitly.
            GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            small = new Texture2D(w, h, TextureFormat.RGBA32, false);
            small.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            small.Apply();
            width = w;
            height = h;
            return ImageConversion.EncodeToPNG(small);
        }
        finally
        {
            GL.sRGBWrite = prevSrgbWrite;
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            if (small != null) UObject.Destroy(small);
        }
    }

    static byte[]? CaptureCameraFallback(out int width, out int height)
    {
        width = 0;
        height = 0;
        Camera? cam = null;
        try { cam = Camera.main; } catch { cam = null; }
        if (cam == null)
        {
            CheatState.Error("debug.screenshot: no backbuffer and no Camera.main");
            return null;
        }
        var w = Math.Min(cam.pixelWidth, ScreenshotCapture.MaxCaptureWidth);
        if (w <= 0)
        {
            CheatState.Error("debug.screenshot: camera has no size (outside a live board?)");
            return null;
        }
        var scale = (float)w / cam.pixelWidth;
        var h = Math.Max(1, Mathf.FloorToInt(cam.pixelHeight * scale));
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.Default, RenderTextureReadWrite.sRGB);
        var prevTarget = cam.targetTexture;
        var prevActive = RenderTexture.active;
        var prevSrgbWrite = GL.sRGBWrite;
        Texture2D? tex = null;
        try
        {
            GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            width = w;
            height = h;
            return ImageConversion.EncodeToPNG(tex);
        }
        catch (Exception ex)
        {
            CheatState.Error("debug.screenshot fallback: " + ex.Message);
            return null;
        }
        finally
        {
            try { cam.targetTexture = prevTarget; } catch { }
            GL.sRGBWrite = prevSrgbWrite;
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);
            if (tex != null) UObject.Destroy(tex);
        }
    }

    static bool HasPngSignature(byte[] png)
    {
        if (png.Length < PngSignature.Length) return false;
        for (var i = 0; i < PngSignature.Length; i++)
            if (png[i] != PngSignature[i]) return false;
        return true;
    }
}
