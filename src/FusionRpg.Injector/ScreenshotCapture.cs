using System.Text.Json;

namespace FusionRpg.Injector;

/// <summary>
/// Arm side of on-demand lawn screenshots (live-probe module <c>lawn-screenshot</c>).
/// The drain runs mid-frame, and Unity capture mid-frame is undefined, so this only arms a
/// pending request — <see cref="ScreenshotRunner"/> performs the capture at end-of-frame.
/// </summary>
public static class ScreenshotCapture
{
    // Structural (not tunable, not the balance surface): bounds the main-thread stall of
    // ReadPixels + PNG encode and the upload payload. A bigger frame is not "more correct".
    internal const int MaxCaptureWidth = 960;

    // Structural (not tunable): rate limit. A screenshot a second is a stall farm, not a test.
    const int MinCaptureGapMs = 5000;

    static long _lastArmMs;

    /// <returns>True if a capture was armed.</returns>
    public static bool TryArm(JsonElement p)
    {
        var tag = SanitizeTag(p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("tag", out var t)
            && t.ValueKind == JsonValueKind.String ? t.GetString() : null);
        var now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastArmMs) < MinCaptureGapMs)
        {
            CheatState.Note("debug screenshot throttled (one per 5 s)");
            return false;
        }
        if (!ScreenshotRunner.Arm(tag))
            return false;
        Volatile.Write(ref _lastArmMs, now);
        return true;
    }

    internal static string SanitizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "probe";
        var sb = new System.Text.StringBuilder(32);
        foreach (var c in tag.Trim())
        {
            if (sb.Length >= 32) break;
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }
        return sb.Length == 0 ? "probe" : sb.ToString();
    }
}
