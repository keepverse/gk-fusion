using FusionRpg.Launcher.Services;

namespace FusionRpg.Launcher.Tests;

/// <summary>
/// The release ships a launcher that can open the web UI and an injector drop that provides the
/// in-game one. A cloud release builds the injector from a committed fallback drop, so the two can
/// silently drift apart: the release succeeds, and the in-game browser simply is not there.
/// These tests pin the shapes that make that failure loud.
/// </summary>
public class PlayerPackOverlayProbeTests : IDisposable
{
    readonly string _pack;

    public PlayerPackOverlayProbeTests()
    {
        _pack = Path.Combine(Path.GetTempPath(), "fusionrpg-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pack);
    }

    public void Dispose()
    {
        try { Directory.Delete(_pack, recursive: true); } catch { }
    }

    // ---- fixture helpers ----

    void Write(string relative, string content = "x")
    {
        var full = Path.Combine(_pack, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>An injector that carries the in-game overlay, identified by its type names.</summary>
    void WriteInjector(string dropRelative, bool withOverlayCode = true, bool withWebView2 = true)
    {
        var body = withOverlayCode
            ? "OverlayViewHost OverlaySwitchGui"
            : "some older build with none of it";
        Write(Path.Combine(dropRelative, "FusionRpg.Injector.dll"), body);
        if (withWebView2)
        {
            Write(Path.Combine(dropRelative, "Microsoft.Web.WebView2.Core.dll"));
            Write(Path.Combine(dropRelative, "WebView2Loader.dll"));
        }
    }

    void WriteLauncherSide()
    {
        Write("FusionRpg.Launcher.exe");
        Write("Microsoft.Web.WebView2.Core.dll");
        Write("WebView2Loader.dll");
    }

    static PlayerPackProbeStep Step(string pack) => new PlayerPackProbe().ProbeOverlayPayload(pack);

    // ---- the pack we actually want to ship ----

    [Fact]
    public void A_complete_pack_passes()
    {
        WriteLauncherSide();
        WriteInjector(Path.Combine("DropIntoGame", "BepInEx"));
        WriteInjector(Path.Combine("DropIntoGame", "pvzrh-3.9", "MelonLoader"));

        var step = Step(_pack);
        Assert.True(step.Ok, step.Message);
    }

    // ---- the failure that actually shipped ----

    [Fact]
    public void A_stale_injector_without_the_overlay_code_fails()
    {
        // This is the committed CI fallback drop: built before the feature existed, so a cloud
        // release ships a launcher that can open the web UI and an injector that cannot.
        WriteLauncherSide();
        WriteInjector(Path.Combine("DropIntoGame", "BepInEx"), withOverlayCode: false);
        WriteInjector(Path.Combine("DropIntoGame", "pvzrh-3.9", "MelonLoader"));

        var step = Step(_pack);
        Assert.False(step.Ok);
        Assert.Contains("in-game overlay", step.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_injector_drop_without_webview2_beside_it_fails()
    {
        // The installer copies top-level files only, so the loader must sit next to the injector.
        WriteLauncherSide();
        WriteInjector(Path.Combine("DropIntoGame", "BepInEx"), withWebView2: false);
        WriteInjector(Path.Combine("DropIntoGame", "pvzrh-3.9", "MelonLoader"));

        var step = Step(_pack);
        Assert.False(step.Ok);
        Assert.Contains("WebView2", step.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_loader_buried_in_a_subfolder_does_not_count()
    {
        WriteLauncherSide();
        WriteInjector(Path.Combine("DropIntoGame", "BepInEx"), withWebView2: false);
        // The old layout: correct file, wrong place. PluginInstaller never recurses, so the player
        // would end up with the managed DLL and no native loader.
        Write(Path.Combine("DropIntoGame", "BepInEx", "runtimes", "win-x64", "native", "WebView2Loader.dll"));
        Write(Path.Combine("DropIntoGame", "BepInEx", "Microsoft.Web.WebView2.Core.dll"));
        WriteInjector(Path.Combine("DropIntoGame", "pvzrh-3.9", "MelonLoader"));

        var step = Step(_pack);
        Assert.False(step.Ok);
        Assert.Contains("WebView2Loader.dll", step.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pack_with_no_melonloader_drop_fails_unless_acknowledged()
    {
        // Every release so far shipped without it, because publish only builds it when
        // FUSIONRPG_ML_GAMEDIR is set and the workflow never sets it.
        WriteLauncherSide();
        WriteInjector(Path.Combine("DropIntoGame", "BepInEx"));

        var step = Step(_pack);
        Assert.False(step.Ok);
        Assert.Contains("MelonLoader", step.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_launcher_without_its_own_webview2_fails()
    {
        // Without this the F10 overlay cannot open either, so nothing works.
        Write("FusionRpg.Launcher.exe");
        WriteInjector(Path.Combine("DropIntoGame", "BepInEx"));
        WriteInjector(Path.Combine("DropIntoGame", "pvzrh-3.9", "MelonLoader"));

        var step = Step(_pack);
        Assert.False(step.Ok);
        Assert.Contains("launcher", step.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_pack_with_no_injector_drop_at_all_fails()
    {
        WriteLauncherSide();

        var step = Step(_pack);
        Assert.False(step.Ok);
    }
}
