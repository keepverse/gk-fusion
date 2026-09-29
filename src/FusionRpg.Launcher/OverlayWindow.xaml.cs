using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using FusionRpg.Launcher.Services;
using Microsoft.Web.WebView2.Core;

namespace FusionRpg.Launcher;

/// <summary>
/// Borderless topmost WebView2 window that covers the game window and shows the RPG web UI.
/// Hidden (never destroyed) on toggle so the SPA keeps its SignalR session between switches.
/// </summary>
public partial class OverlayWindow : Window
{
    readonly Action _backToGame;
    bool _webReady;
    bool _allowClose;
    string? _currentUrl;

    public OverlayWindow(Action backToGame, string hotKeyName)
    {
        InitializeComponent();
        _backToGame = backToGame;
        BackButton.Content = $"⟵ Back to game ({hotKeyName} / Esc)";
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _backToGame();
            }
        };
    }

    /// <summary>Show the overlay covering the game window (fallback: maximized) and load the UI.</summary>
    public async Task ShowOverGameAsync(string url)
    {
        if (GameWindowInterop.TryGetGameWindowRect(out var rect))
        {
            WindowState = WindowState.Normal;
            Show();
            var hwnd = new WindowInteropHelper(this).Handle;
            GameWindowInterop.MoveWindowOver(hwnd, rect);
        }
        else
        {
            Show();
            WindowState = WindowState.Maximized;
        }

        Activate();
        await EnsureWebAsync(url);
        if (_webReady)
            Web.Focus();
    }

    async Task EnsureWebAsync(string url)
    {
        if (!_webReady)
        {
            try
            {
                // User-data folder must be writable — never next to the exe (Program Files).
                var dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FusionRpg", "webview2");
                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
                await Web.EnsureCoreWebView2Async(env);
                Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webReady = true;
            }
            catch (Exception ex)
            {
                FallbackText.Text =
                    "The Microsoft Edge WebView2 Runtime is not available, so the overlay cannot render the web UI.\n\n" +
                    "Install it from:\nhttps://developer.microsoft.com/microsoft-edge/webview2/\n\n" +
                    "Until then, use “Open RPG UI” in the launcher to open the UI in your normal browser.\n\n" +
                    "Detail: " + ex.Message;
                FallbackText.Visibility = Visibility.Visible;
                return;
            }
        }

        if (!string.Equals(_currentUrl, url, StringComparison.OrdinalIgnoreCase))
        {
            // rift-gate decision 16: mark the visit as embedded, so the page knows Leave can work.
            // The bare URL stays the source; only the navigated form carries the marker.
            //
            // The literal is duplicated on purpose: this project (net8.0-windows WPF) shares no
            // assembly with FusionRpg.Core / the net6.0 injector, exactly like PipeName. The
            // RiftGateEmbedMarkerGuardTests pin the launcher's literal to the Core constant and to
            // the web reader, so a drift fails a test instead of silently disabling Leave.
            _currentUrl = url;
            Web.CoreWebView2.Navigate(AppendEmbedMarker(url));
        }
    }

    /// <summary>
    /// rift-gate decision 16 — the embed marker, duplicated from
    /// <c>FusionRpg.Core.Overlay.OverlayEmbedMarker</c> because this project shares no assembly with
    /// Core (same as <c>OverlayPipeServer.PipeName</c>). A query flag, not a hash fragment: the FE's
    /// HashRouter owns the hash. Guarded by <c>RiftGateEmbedMarkerGuardTests</c>.
    /// </summary>
    const string EmbedQueryKey = "embed";
    const string EmbedQueryValue = "1";

    static string AppendEmbedMarker(string? baseUrl)
    {
        var url = baseUrl ?? "";
        if (url.Length == 0) return url;
        if (url.Contains($"{EmbedQueryKey}={EmbedQueryValue}", StringComparison.Ordinal)) return url;
        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url}{separator}{EmbedQueryKey}={EmbedQueryValue}";
    }

    void Back_Click(object sender, RoutedEventArgs e) => _backToGame();

    /// <summary>Alt+F4 etc. hides instead of destroying, so toggling stays instant.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            _backToGame();
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>Really close (launcher shutdown).</summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }
}
