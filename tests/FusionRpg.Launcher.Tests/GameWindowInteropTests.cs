using System.Windows.Input;
using FusionRpg.Launcher.Services;

namespace FusionRpg.Launcher.Tests;

public class GameWindowInteropTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NotAKey")]
    public void ParseOverlayKey_falls_back_to_F10(string? name)
    {
        Assert.Equal(Key.F10, GameWindowInterop.ParseOverlayKey(name));
    }

    [Theory]
    [InlineData("F9", Key.F9)]
    [InlineData("f10", Key.F10)]
    [InlineData(" F12 ", Key.F12)]
    [InlineData("Pause", Key.Pause)]
    public void ParseOverlayKey_accepts_key_names(string name, Key expected)
    {
        Assert.Equal(expected, GameWindowInterop.ParseOverlayKey(name));
    }

    [Theory]
    [InlineData("None")]
    [InlineData("LeftCtrl")]
    [InlineData("LWin")]
    [InlineData("9999")] // numeric strings parse to undefined enum values — must fall back
    public void ParseOverlayKey_rejects_modifier_only_keys(string name)
    {
        Assert.Equal(Key.F10, GameWindowInterop.ParseOverlayKey(name));
    }

    [Fact]
    public void Quns_constant_is_exclusive_d3d_fullscreen_not_busy()
    {
        // QUERY_USER_NOTIFICATION_STATE: QUNS_BUSY = 2 (any fullscreen, incl. borderless),
        // QUNS_RUNNING_D3D_FULL_SCREEN = 3 (exclusive D3D — the state needing the fallback).
        Assert.Equal(3, GameWindowInterop.QunsRunningD3dFullScreen);
    }
}
