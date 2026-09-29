using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Threading;
using FusionRpg.Launcher.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace FusionRpg.Launcher;

public partial class MainWindow : FluentWindow
{
    readonly LauncherSettings _settings = LauncherSettings.Load();
    readonly PlaySession _session = new();
    readonly GameLocator _locator = new();
    readonly LoaderProbe _loader = new();
    readonly PluginInstaller _plugins = new();
    readonly OfficialReleaseDownloader _releases = new();
    readonly ModLoaderInstaller _loaders;
    readonly FusionRpgUpdater _updater;
    readonly LoaderManifest _manifest;
    readonly GitHubReleaseClient _github;
    readonly DispatcherTimer _pollTimer;
    readonly string _baseDir;
    bool _forceClose;
    bool _busy;
    bool _stoppedForUpdate;
    CancellationTokenSource? _busyCts;
    NotifyIcon? _tray;
    OverlayWindow? _overlay;
    System.Windows.Interop.HwndSource? _hwndSource;
    System.Windows.Input.Key _overlayKey = GameWindowInterop.DefaultOverlayKey;
    OverlayPipeServer? _overlayPipe;

    static readonly SolidColorBrush Green = new(Color.FromRgb(0x3D, 0xD6, 0x8C));
    static readonly SolidColorBrush Red = new(Color.FromRgb(0xE5, 0x5C, 0x5C));
    static readonly SolidColorBrush Gray = new(Color.FromRgb(0x88, 0x88, 0x88));
    static readonly SolidColorBrush Amber = new(Color.FromRgb(0xE6, 0xB8, 0x4D));

    public MainWindow()
    {
        InitializeComponent();
        _baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _manifest = LoaderManifest.LoadFromLauncherDir(_baseDir);
        _github = GitHubReleaseClient.ForManifest(_manifest.FusionRpg);
        _loaders = new ModLoaderInstaller(_releases);
        _updater = new FusionRpgUpdater(_releases);

        if (!string.IsNullOrWhiteSpace(_settings.GameFolder))
            GameFolderBox.Text = _settings.GameFolder;
        else
        {
            var suggest = _locator.SuggestGameFolder(_baseDir);
            if (suggest != null) GameFolderBox.Text = suggest;
        }

        GameFolderBox.LostFocus += (_, _) => PersistGameFolderFromUi(quiet: true);

        InitTray();

        SourceInitialized += (_, _) => InitOverlayHotKey();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pollTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _pollTimer.Start();

        StateChanged += (_, _) =>
        {
            if (_settings.MinimizeToTray && WindowState == WindowState.Minimized)
            {
                Hide();
                if (_tray != null) _tray.Visible = true;
            }
        };

        Loaded += async (_, _) =>
        {
            AppendLog("FusionRpg Launcher " + LocalVersion());
            AppendLog("AGPL-3.0-or-later — https://github.com/letuhao/plant-vs-zombie-rise-of-summoner");
            AppendLog("Unsigned hobby build — Trust & security explains antivirus false positives.");
            if (!string.IsNullOrWhiteSpace(_settings.GameFolder))
                AppendLog("Restored game folder: " + _settings.GameFolder);
            RefreshTrustStatusUi();
            PersistGameFolderFromUi(quiet: true);
            if (!EnsureTrustAcknowledged())
            {
                Close();
                return;
            }
            await _session.RestoreFromSettingsAsync(_settings);
            if (_session.ActiveUrl != null)
                AppendLog("Restored server session " + _session.ActiveUrl);
            await RefreshStatusAsync();
            await CheckUpdatesAsync();
        };
    }

    void RefreshTrustStatusUi()
    {
        if (_settings.WindowsSecurityPrepared)
            TrustStatusText.Text = "Trust acknowledged. Windows Security prepare recorded (Defender).";
        else if (_settings.TrustAcknowledged)
            TrustStatusText.Text = "Trust acknowledged. Optional: Prepare Windows Security (Defender).";
        else
            TrustStatusText.Text = "Unsigned hobby build — acknowledge trust to Play.";
    }

    /// <summary>Returns false if the user declined (caller should exit).</summary>
    bool EnsureTrustAcknowledged()
    {
        if (_settings.TrustAcknowledged) return true;
        var result = MessageBox.Show(
            this,
            AntivirusGuard.ConsentMessage(),
            "FusionRpg — Trust & security",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            StatusBarText.Text = "Trust declined.";
            return false;
        }

        _settings.TrustAcknowledged = true;
        _settings.Save();
        RefreshTrustStatusUi();
        AppendLog("Trust acknowledged (unsigned hobby OSS). " +
                  "Optional: Prepare Windows Security (Defender only) — never required to play.");

        // Deliberately does NOT offer the Defender prepare here. Chaining it onto first run made
        // the launcher look like it needs administrator rights just to start, and the exclusion is
        // Defender-only: it does nothing at all for players on Bitdefender, Kaspersky and friends.
        // The button stays available for anyone who actually wants it.
        return true;
    }

    void TrustSecurity_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            AntivirusGuard.ConsentMessage() + "\n\n" +
            "Current: TrustAcknowledged=" + _settings.TrustAcknowledged +
            ", WindowsSecurityPrepared=" + _settings.WindowsSecurityPrepared + "\n\n" +
            "Pack folder:\n" + AntivirusGuard.PackRootHint(_baseDir),
            "Trust & security",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        if (!_settings.TrustAcknowledged)
            EnsureTrustAcknowledged();
    }

    void PrepareWindowsSecurity_Click(object sender, RoutedEventArgs e) =>
        _ = RunPrepareWindowsSecurityAsync(alreadyConfirmed: false);

    async Task RunPrepareWindowsSecurityAsync(bool alreadyConfirmed)
    {
        if (!alreadyConfirmed)
        {
            var confirm = MessageBox.Show(
                this,
                WindowsSecurityPrepare.ConfirmDialogText(_baseDir),
                "Prepare Windows Security",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
        }

        StatusBarText.Text = "Waiting for Windows permission (UAC)… Keep this window open.";
        PrepareSecurityButton.IsEnabled = false;
        try
        {
            var (ok, msg) = await Task.Run(() =>
            {
                var success = WindowsSecurityPrepare.TryElevateAddExclusion(_baseDir, out var m);
                return (success, m);
            }).ConfigureAwait(true);

            AppendLog(msg.Replace("\n", " | ", StringComparison.Ordinal));
            StatusBarText.Text = ok ? "Windows Security prepared." : "Windows Security prepare cancelled or failed.";
            if (ok)
            {
                _settings.WindowsSecurityPrepared = true;
                _settings.TrustAcknowledged = true;
                _settings.Save();
                RefreshTrustStatusUi();
            }

            // Bring launcher back after UAC steals focus
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;

            MessageBox.Show(
                this,
                msg,
                ok ? "Windows Security prepared" : "Windows Security prepare",
                MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            PrepareSecurityButton.IsEnabled = true;
        }
    }

    void InitTray()
    {
        _tray = new NotifyIcon
        {
            Text = "FusionRpg Launcher",
            Visible = false,
            Icon = System.Drawing.SystemIcons.Application
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Play", null, (_, _) => Dispatcher.Invoke(() => Play_Click(this, new RoutedEventArgs())));
        menu.Items.Add("Stop all", null, (_, _) => Dispatcher.Invoke(() => StopAll_Click(this, new RoutedEventArgs())));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(() =>
        {
            // Same question as closing the window. This used to stop the server and game outright,
            // with no prompt at all, which killed a match in progress without warning.
            var choice = AskHowToExit();
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.Yes) _session.StopAll();

            _forceClose = true;
            _tray!.Visible = false;
            Close();
        }));
        _tray.ContextMenuStrip = menu;
    }

    void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_tray != null) _tray.Visible = false;
    }

    // ---- Game / web overlay switch (WebView2 window + global hotkey) ----

    void InitOverlayHotKey()
    {
        _overlayKey = GameWindowInterop.ParseOverlayKey(_settings.OverlayHotKey);
        OverlayButton.Content = $"Overlay ({_overlayKey})";

        // Independent of the HWND work below: the in-game button must still reach us when the
        // window handle or the hotkey is unavailable — that is exactly when it is the only way in.
        StartOverlayPipe();

        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        _hwndSource = System.Windows.Interop.HwndSource.FromHwnd(helper.Handle);
        if (_hwndSource == null)
            return;
        _hwndSource.AddHook(OverlayHotKeyHook);

        if (GameWindowInterop.TryRegisterOverlayHotKey(helper.Handle, _overlayKey))
            AppendLog($"Overlay hotkey registered: {_overlayKey} (toggles game ⇄ web UI).");
        else
            AppendLog($"Overlay hotkey {_overlayKey} is taken by another app — use the Overlay button instead " +
                      "(or set overlayHotKey in launcher.json).");
    }

    /// <summary>Listens for the in-game button. Same toggle path as the hotkey — no second behavior.</summary>
    void StartOverlayPipe()
    {
        _overlayPipe = new OverlayPipeServer(
            OnOverlayPipeCommand,
            message => Dispatcher.InvokeAsync(() => AppendLog(message)));
        _overlayPipe.Start();
    }

    void OnOverlayPipeCommand(OverlayPipeCommand command)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            switch (command)
            {
                case OverlayPipeCommand.Toggle:
                    await ToggleOverlayAsync();
                    break;
                case OverlayPipeCommand.Hide:
                    // The SAME hide the Esc key uses (overlay-spec.md: one toggle method, no entry
                    // point gets its own behaviour). Not a toggle: the page asked to close, so it closes.
                    HideOverlayToGame();
                    break;
                case OverlayPipeCommand.Ping:
                    break; // availability probe only — must not move the overlay
            }
        });
    }

    IntPtr OverlayHotKeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == GameWindowInterop.WmHotKey && wParam.ToInt32() == GameWindowInterop.OverlayHotKeyId)
        {
            handled = true;
            _ = Dispatcher.InvokeAsync(ToggleOverlayAsync);
        }
        return IntPtr.Zero;
    }

    bool _overlayBusy;

    async Task ToggleOverlayAsync()
    {
        if (_overlayBusy) return; // rapid re-press while WebView2 is still initializing
        if (_overlay is { IsVisible: true })
        {
            HideOverlayToGame();
            return;
        }

        var url = _session.ActiveUrl
                  ?? (_settings.LastPort is int p ? $"http://127.0.0.1:{p}" : null);
        if (url == null)
        {
            AppendLog("Overlay: no server URL yet — press Play first.");
            return;
        }

        // Exclusive fullscreen hides topmost windows — drop to a plain window switch instead.
        if (GameWindowInterop.IsGameExclusiveFullscreen())
        {
            GameWindowInterop.MinimizeGame();
            AppendLog("Game is exclusive-fullscreen — minimized it for the overlay. " +
                      "Borderless/windowed mode gives a seamless toggle.");
        }

        _overlay ??= new OverlayWindow(HideOverlayToGame, _overlayKey.ToString());
        _overlayBusy = true;
        try
        {
            await _overlay.ShowOverGameAsync(url);
        }
        catch (Exception ex)
        {
            AppendLog("Overlay failed: " + ex.Message);
        }
        finally
        {
            _overlayBusy = false;
        }
    }

    void HideOverlayToGame()
    {
        _overlay?.Hide();
        if (!GameWindowInterop.FocusGame())
            AppendLog("Overlay hidden — game window not found to refocus.");
    }

    async void Overlay_Click(object sender, RoutedEventArgs e) => await ToggleOverlayAsync();

    static string LocalVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var raw = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? asm.GetName().Version?.ToString()
               ?? "1.0.0";
        return GitHubReleaseClient.NormalizeVersion(raw);
    }

    void SetBusyUi(bool busy)
    {
        _busy = busy;
        CancelBusyButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelBusyButton.IsEnabled = busy;
        if (!busy)
        {
            _busyCts?.Dispose();
            _busyCts = null;
        }
    }

    void CancelBusy_Click(object sender, RoutedEventArgs e)
    {
        try { _busyCts?.Cancel(); }
        catch { /* ignore */ }
        AppendLog("Cancel requested…");
        StatusBarText.Text = "Cancelling…";
    }

    void AppendLog(string line)
    {
        // The clock seam does not reach this assembly: FusionRpg.Launcher references no FusionRpg
        // project at all (its csproj carries no ProjectReference), and this is a LOCAL UI log stamp —
        // not a duration, a deadline, or a persisted row. Recorded as an exclusion with its reason in
        // gk-core/scripts/guard-clock-seam.py; migrating it needs a Launcher -> Core dependency decision
        // (guard-repo-boundary's pinned graph does not name Launcher at all).
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        LogBox.AppendText($"[{stamp}] {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
        _session.Processes.AppendLog(line);
    }

    string? GameFolder => string.IsNullOrWhiteSpace(GameFolderBox.Text) ? null : GameFolderBox.Text.Trim();

    void PersistGameFolderFromUi(bool quiet = false)
    {
        var folder = GameFolder;
        if (string.IsNullOrWhiteSpace(folder))
            return;
        if (LauncherSettings.IsEphemeralTestPath(folder))
            return;
        if (string.Equals(_settings.GameFolder, folder, StringComparison.OrdinalIgnoreCase))
            return;
        _settings.GameFolder = folder;
        _settings.Save();
        if (!quiet)
            AppendLog("Saved game folder: " + folder);
    }

    void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select Fusion game folder" };
        if (dlg.ShowDialog() == true)
        {
            GameFolderBox.Text = dlg.FolderName;
            _settings.GameFolder = dlg.FolderName;
            _settings.Save();
            AppendLog("Saved game folder: " + dlg.FolderName);
            _ = RefreshStatusAsync();
        }
    }

    async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!_settings.TrustAcknowledged && !EnsureTrustAcknowledged())
            return;

        var folder = GameFolder;
        if (folder == null)
        {
            StatusBarText.Text = "Pick a game folder first.";
            return;
        }

        if (AntivirusGuard.ServerExeMissing(_baseDir, out _))
        {
            var help = AntivirusGuard.QuarantineHelpMessage(_baseDir);
            AppendLog(help.Replace("\n", " | ", StringComparison.Ordinal));
            MessageBox.Show(this, help, "Server missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusBarText.Text = "Server exe missing — see Trust & security.";
            return;
        }

        _busy = true;
        PlayButton.IsEnabled = false;
        StatusBarText.Text = "Starting…";
        try
        {
            var (ok, msg) = await _session.PlayAsync(folder, _baseDir, _settings);
            AppendLog(msg);
            StatusBarText.Text = msg.Length > 120 ? msg[..120] + "…" : msg;
            if (!ok && (msg.Contains("antivirus", StringComparison.OrdinalIgnoreCase)
                        || msg.Contains("quarantine", StringComparison.OrdinalIgnoreCase)
                        || msg.Contains("missing", StringComparison.OrdinalIgnoreCase)
                        || AntivirusGuard.ServerExeMissing(_baseDir, out _)))
            {
                MessageBox.Show(this, msg, "Play failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            if (ok && _session.ActiveUrl != null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(_session.ActiveUrl) { UseShellExecute = true });
                }
                catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            AppendLog("Play failed: " + ex.Message);
            StatusBarText.Text = "Play failed.";
            MessageBox.Show(this, ex.Message, "Play failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            PlayButton.IsEnabled = true;
            await RefreshStatusAsync();
        }
    }

    async void RestartServer_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!_settings.TrustAcknowledged && !EnsureTrustAcknowledged())
            return;
        _busy = true;
        try
        {
            var (ok, msg) = await _session.RestartServerAsync(_baseDir, _settings);
            AppendLog(msg);
            StatusBarText.Text = msg.Length > 120 ? msg[..120] + "…" : msg;
            if (!ok)
                MessageBox.Show(this, msg, "Restart server", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            await RefreshStatusAsync();
        }
    }

    void RestartGame_Click(object sender, RoutedEventArgs e)
    {
        var folder = GameFolder;
        if (folder == null) { StatusBarText.Text = "Pick a game folder first."; return; }
        var (ok, msg) = _session.RestartGame(folder, _settings);
        AppendLog(msg);
        StatusBarText.Text = msg;
        _ = RefreshStatusAsync();
        if (!ok) { /* message set */ }
    }

    void StopAll_Click(object sender, RoutedEventArgs e)
    {
        _session.StopAll();
        AppendLog("Stopped server and game.");
        StatusBarText.Text = "Stopped.";
        _ = RefreshStatusAsync();
    }

    void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var folder = GameFolder;
        if (folder == null) { StatusBarText.Text = "Pick a game folder first."; return; }
        var probe = _loader.Probe(folder);
        if (probe.PluginDir == null)
        {
            StatusBarText.Text = "No FusionRpg plugin path.";
            return;
        }

        var confirmBody = probe.Host is { IsSharedPluginDirectory: true }
            ? "Remove only FusionRpg files (FusionRpg.*.dll + fusionrpg.cfg) from shared Mods?\n\n" +
              "Other Melon mods in Mods\\ are kept.\n" +
              "Save databases next to the server are kept unless you delete Server\\data yourself."
            : "Remove FusionRpg plugin DLLs from:\n" + probe.PluginDir + "\n\n" +
              "Save databases next to the server are kept unless you delete the Server\\data folder yourself.";

        var wipe = System.Windows.MessageBox.Show(
            this,
            confirmBody,
            "Uninstall plugin",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (wipe != MessageBoxResult.Yes) return;

        var n = _plugins.UninstallPlugin(probe.PluginDir, probe.Host);
        AppendLog($"Removed {n} FusionRpg plugin file(s).");
        StatusBarText.Text = "Plugin uninstalled.";
        _ = RefreshStatusAsync();
    }

    void OpenRepo_Click(object sender, RoutedEventArgs e) =>
        OpenUrl(GitHubReleaseClient.RepoUrl);

    void OpenReleases_Click(object sender, RoutedEventArgs e) =>
        OpenUrl(GitHubReleaseClient.ReleasesUrl);

    void OpenUi_Click(object sender, RoutedEventArgs e)
    {
        var url = _session.ActiveUrl
                  ?? (_settings.LastPort is int p ? $"http://127.0.0.1:{p}" : null)
                  ?? "http://127.0.0.1:5088";
        OpenUrl(url);
    }

    void OpenBepLog_Click(object sender, RoutedEventArgs e)
    {
        var folder = GameFolder;
        if (folder == null)
        {
            StatusBarText.Text = "Pick a game folder first.";
            return;
        }
        var probe = _loader.Probe(folder);
        var log = ProcessSupervisor.LoaderLogPath(folder, probe.Host);
        if (log == null || !ProcessSupervisor.OpenInExplorer(log))
            StatusBarText.Text = "Loader log not found yet (start the game once).";
        else
            StatusBarText.Text = "Opened loader log location.";
    }

    void OpenServerFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _session.Processes.ResolveServerDir(_baseDir);
        if (!ProcessSupervisor.OpenInExplorer(dir))
            StatusBarText.Text = "Server folder not found. Run publish_player.py first.";
        else
            StatusBarText.Text = "Opened server folder.";
    }

    void About_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(
            this,
            "FusionRpg Launcher " + LocalVersion() + "\n\n" +
            "Copyright (c) 2026 Lê Tú Hào\n" +
            "Licensed under AGPL-3.0-or-later.\n\n" +
            "Unsigned hobby build — some antivirus products may false-positive the server.\n" +
            "Use Trust & security / Prepare Windows Security (Defender only).\n\n" +
            "Players need no .NET SDK, Desktop Runtime, or Node.\n" +
            "Source: " + GitHubReleaseClient.RepoUrl,
            "About",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    async Task CheckUpdatesAsync()
    {
        try
        {
            var rel = await _github.GetLatestAsync();
            if (!rel.Found)
            {
                UpdateBanner.Text = "No GitHub release published yet — you're on a local/player build.";
                UpdateBanner.Visibility = Visibility.Visible;
                UpdateButton.Visibility = Visibility.Collapsed;
                return;
            }

            if (GitHubReleaseClient.IsNewerThan(rel.TagName, LocalVersion()))
            {
                if (!rel.HasPreferredZip)
                {
                    UpdateBanner.Text =
                        $"Newer tag {rel.TagName} exists but FusionRpg-win-x64.zip is missing from the release. Open Releases manually.";
                    UpdateBanner.Visibility = Visibility.Visible;
                    UpdateButton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    UpdateBanner.Text = $"Update available: {rel.TagName}. Download FusionRpg only (not the game).";
                    UpdateBanner.Visibility = Visibility.Visible;
                    UpdateButton.Visibility = Visibility.Visible;
                }
            }
            else
            {
                UpdateBanner.Text = $"Up to date ({rel.TagName}).";
                UpdateBanner.Visibility = Visibility.Visible;
                UpdateButton.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            AppendLog("Update check: " + ex.Message);
            UpdateButton.Visibility = Visibility.Collapsed;
        }
    }

    async void InstallBep_Click(object sender, RoutedEventArgs e) =>
        await InstallLoaderAsync(bepInEx: true);

    async void InstallMelon_Click(object sender, RoutedEventArgs e) =>
        await InstallLoaderAsync(bepInEx: false);

    void InstallPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var folder = GameFolder;
        if (folder == null)
        {
            StatusBarText.Text = "Pick a game folder first.";
            return;
        }

        var probe = _loader.Probe(folder);
        if (!probe.OkForV1 || probe.PluginDir == null || probe.Host == null)
        {
            MessageBox.Show(
                this,
                "Install BepInEx 6 IL2CPP or MelonLoader first (this game is not Mono BepInEx 5.4).\n\n" + probe.Message,
                "Install FusionRpg plugin",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var host = probe.Host;
            var catalog = GameProfileCatalog.LoadFromLauncherBase(_baseDir);
            var profileId = catalog.Detect(folder, _settings.GameProfile);
            var dll = host.InjectorDllNameFor(profileId);
            var drop = host.DropPayloadDir(_baseDir, profileId);
            if (!host.HasDropPayload(_baseDir, profileId) || !Directory.Exists(drop) || !File.Exists(Path.Combine(drop, dll)))
            {
                var tip = host.Kind == LoaderKind.MelonLoader
                    ? $"Melon drop missing for {profileId} ({dll}).\nSet FUSIONRPG_ML_GAMEDIR + FUSIONRPG_GAME_PROFILE and re-run publish_player.py."
                    : $"DropIntoGame is missing {dll} for {profileId}/{host.Kind}.\nRe-download FusionRpg-win-x64.zip (or run publish_player.py).";
                MessageBox.Show(
                    this,
                    tip,
                    "Install FusionRpg plugin",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            var n = _plugins.Install(drop, probe.PluginDir);
            AppendLog($"Installed {n} FusionRpg plugin file(s) [{profileId}] → {probe.PluginDir}");
            StatusBarText.Text = "FusionRpg plugin installed.";
            _ = RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            AppendLog("Install plugin failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Install FusionRpg plugin", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    async Task InstallLoaderAsync(bool bepInEx)
    {
        if (_busy) return;
        var folder = GameFolder;
        if (folder == null)
        {
            StatusBarText.Text = "Pick a game folder first.";
            return;
        }

        var label = bepInEx ? "BepInEx 6 IL2CPP" : "MelonLoader";
        var pin = bepInEx ? _manifest.BepInEx.Tag : _manifest.MelonLoader.Tag;
        var extra = bepInEx
            ? "\n\nPVZ Fusion is Unity IL2CPP. This installs the pinned BepInEx 6 IL2CPP pack (" + pin +
              ") from official GitHub.\n" +
              "Do NOT use BepInEx 5.4.x (GitHub \"Latest\" Mono) — it cannot load this game or FusionRpg."
            : "\n\nAfter MelonLoader install, Play copies DropIntoGame\\MelonLoader into Mods\\ (never dual-load with BepInEx).";
        var confirm = MessageBox.Show(
            this,
            $"Install {label} ({pin}) into:\n\n{folder}" + extra +
            "\n\nConfirm this is your legal PVZ Fusion game folder. Dual-load is refused.",
            $"Install {label}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _busyCts = new CancellationTokenSource();
        var ct = _busyCts.Token;
        SetBusyUi(true);
        InstallBepButton.IsEnabled = false;
        InstallMelonButton.IsEnabled = false;
        StatusBarText.Text = $"Installing {label}…";
        var log = new Progress<string>(AppendLog);
        var progress = new Progress<double>(p => StatusBarText.Text = $"Installing {label}… {(int)(p * 100)}%");
        try
        {
            if (bepInEx)
                await _loaders.InstallBepInExAsync(folder, _manifest.BepInEx, log, progress, ct);
            else
                await _loaders.InstallMelonLoaderAsync(folder, _manifest.MelonLoader, log, progress, ct);
            StatusBarText.Text = $"{label} installed.";
        }
        catch (OperationCanceledException)
        {
            AppendLog($"{label} install cancelled.");
            StatusBarText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            AppendLog($"{label} install failed: " + ex.Message);
            StatusBarText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, $"Install {label}", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusyUi(false);
            await RefreshStatusAsync();
        }
    }

    async void UpdateFusionRpg_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var confirm = MessageBox.Show(
            this,
            "Download and install the latest FusionRpg-win-x64.zip from GitHub Releases?\n\n" +
            "Server\\data (saves) will be preserved. The launcher will restart.\n" +
            "This does not download or patch the PVZ Fusion game.",
            "Update FusionRpg",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _busyCts = new CancellationTokenSource();
        var ct = _busyCts.Token;
        SetBusyUi(true);
        _stoppedForUpdate = false;
        UpdateButton.IsEnabled = false;
        StatusBarText.Text = "Downloading FusionRpg update…";
        try
        {
            var progress = new Progress<double>(p =>
                StatusBarText.Text = $"Downloading FusionRpg… {(int)(p * 100)}%");
            AppendLog("Downloading FusionRpg-win-x64.zip…");
            var (zip, tag) = await _updater.DownloadLatestAsync(_manifest.FusionRpg, progress, ct);
            AppendLog($"Downloaded {tag}: {zip}");

            _session.StopAll();
            _stoppedForUpdate = true;
            AppendLog("Stopped server/game for update.");
            var script = _updater.PrepareApply(zip, _baseDir, stopGame: true, launcherPid: Environment.ProcessId);
            AppendLog("Prepared update bootstrap; restarting…");
            _forceClose = true;
            _pollTimer.Stop();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            FusionRpgUpdater.LaunchBootstrapAndExit(script);
            // Hard-exit: WPF Shutdown() can linger (tray/dispatcher) and the old
            // wait-on-image-name script hung forever. Bootstrap waits on this PID.
            Environment.Exit(0);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Update cancelled.");
            StatusBarText.Text = "Cancelled.";
            if (_stoppedForUpdate)
                await TryRestartServerAfterFailedUpdateAsync();
            SetBusyUi(false);
            UpdateButton.IsEnabled = true;
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            AppendLog("Update failed: " + ex.Message);
            StatusBarText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Update FusionRpg", MessageBoxButton.OK, MessageBoxImage.Error);
            if (_stoppedForUpdate)
                await TryRestartServerAfterFailedUpdateAsync();
            SetBusyUi(false);
            UpdateButton.IsEnabled = true;
            await RefreshStatusAsync();
        }
    }

    async Task TryRestartServerAfterFailedUpdateAsync()
    {
        try
        {
            AppendLog("Restarting server after failed/cancelled update…");
            var (ok, msg) = await _session.RestartServerAsync(_baseDir, _settings);
            AppendLog(msg);
            if (!ok) StatusBarText.Text = msg;
        }
        catch (Exception ex)
        {
            AppendLog("Server restart after update failure: " + ex.Message);
        }
    }

    async Task RefreshStatusAsync()
    {
        try
        {
            var folder = GameFolder;
            LoaderProbeResult? loader = null;
            if (folder != null)
                loader = _loader.Probe(folder);

            LoaderText.Text = loader?.Message ?? "Select a game folder to validate BepInEx.";
            UpdateLoaderButtons(loader);

            var serverUp = _session.Processes.IsServerRunning();
            var gameUp = _session.Processes.IsGameRunning();
            SetLight(ServerLight, ServerStatusText, serverUp, "Server");
            SetLight(GameLight, GameStatusText, gameUp, "Game");

            HealthMonitor.HealthSnapshot? health = null;
            if (_session.ActiveUrl != null)
                health = await _session.Health.CheckAsync(_session.ActiveUrl);

            var inj = health?.InjectorConnected == true;
            if (health is { Reachable: true })
            {
                SetLight(InjectorLight, InjectorStatusText, inj, "Injector");
                if (!serverUp && health.Ok)
                    SetLight(ServerLight, ServerStatusText, true, "Server");
            }
            else
            {
                SetLight(InjectorLight, InjectorStatusText, false, "Injector", unknown: !serverUp);
            }

            // E46 (player-content-boot): the server reports whether it is running on real imported
            // content or the shipped code fallback — show it here so a broken/skipped import is
            // visible to the player, not only in the server log (spec-player-content-boot.md §3.2).
            var contentLabel = health?.ContentSource switch
            {
                "imported" => "content: imported",
                "codeFallback" => "content: fallback (see server log)",
                _ => null,
            };
            UrlText.Text = "URL: " + (_session.ActiveUrl ?? "—")
                + (contentLabel is null ? "" : "  |  " + contentLabel);

            try
            {
                var disk = _session.Disk.Measure(_session.Processes.ResolveServerDir(_baseDir));
                var warn = disk.LowDisk || disk.LargeDb
                    ? " ⚠"
                    : "";
                DiskText.Text =
                    $"Free: {DiskMonitor.FormatBytes(disk.FreeBytes)}\n" +
                    $"DB total: {DiskMonitor.FormatBytes(disk.DbTotalBytes)} " +
                    $"(hot {DiskMonitor.FormatBytes(disk.HotBytes)}, media {DiskMonitor.FormatBytes(disk.MediaBytes)}, legacy {DiskMonitor.FormatBytes(disk.LegacyBytes)})" +
                    warn +
                    (disk.LowDisk ? "\nLow free disk (< 2 GB)." : "") +
                    (disk.LargeDb ? "\nDatabase over 500 MB (no auto-delete)." : "");
            }
            catch (Exception ex)
            {
                DiskText.Text = "Disk: " + ex.Message;
            }

            // Pull server log tail into UI occasionally
            var tail = _session.Processes.ServerLogTail;
            if (!string.IsNullOrEmpty(tail) && LogBox.Text.Length < 20)
                LogBox.Text = tail;
        }
        catch (Exception ex)
        {
            StatusBarText.Text = ex.Message;
        }
    }

    void UpdateLoaderButtons(LoaderProbeResult? loader)
    {
        if (_busy)
        {
            InstallBepButton.IsEnabled = false;
            InstallMelonButton.IsEnabled = false;
            InstallPluginButton.IsEnabled = false;
            return;
        }

        var kind = loader?.Kind ?? LoaderKind.None;
        switch (kind)
        {
            case LoaderKind.None:
                InstallBepButton.Content = "Install BepInEx 6 (IL2CPP)";
                InstallMelonButton.Content = "Install MelonLoader";
                InstallBepButton.IsEnabled = true;
                InstallMelonButton.IsEnabled = true;
                InstallBepButton.Visibility = Visibility.Visible;
                InstallMelonButton.Visibility = Visibility.Visible;
                break;
            case LoaderKind.BepInEx:
                InstallBepButton.Content = "Reinstall BepInEx 6 (IL2CPP)";
                InstallBepButton.IsEnabled = true;
                InstallBepButton.Visibility = Visibility.Visible;
                InstallMelonButton.IsEnabled = false;
                InstallMelonButton.Visibility = Visibility.Collapsed;
                break;
            case LoaderKind.MelonLoader:
                InstallMelonButton.Content = "Reinstall MelonLoader";
                InstallMelonButton.IsEnabled = true;
                InstallMelonButton.Visibility = Visibility.Visible;
                InstallBepButton.IsEnabled = false;
                InstallBepButton.Visibility = Visibility.Collapsed;
                break;
            default:
                InstallBepButton.IsEnabled = false;
                InstallMelonButton.IsEnabled = false;
                InstallBepButton.Visibility = Visibility.Visible;
                InstallMelonButton.Visibility = Visibility.Visible;
                break;
        }

        var canPlugin = false;
        if (loader is { OkForV1: true, PluginDir: not null, Host: not null } && GameFolder != null)
        {
            var catalog = GameProfileCatalog.LoadFromLauncherBase(_baseDir);
            var profileId = catalog.Detect(GameFolder, _settings.GameProfile);
            canPlugin = loader.Host!.HasDropPayload(_baseDir, profileId);
        }
        InstallPluginButton.IsEnabled = canPlugin;
        InstallPluginButton.Visibility = loader is { OkForV1: true, PluginDir: not null }
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (loader is { OkForV1: true, PluginDir: not null })
        {
            if (!canPlugin && loader.Host != null)
            {
                InstallPluginButton.Content = loader.Host.Kind == LoaderKind.MelonLoader
                    ? "Melon drop missing (publish with FUSIONRPG_ML_GAMEDIR)"
                    : "DropIntoGame missing injector";
                InstallPluginButton.IsEnabled = false;
                InstallPluginButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
            }
            else
            {
                InstallPluginButton.Content = loader!.PluginInstalled
                    ? "Reinstall FusionRpg plugin"
                    : "Install FusionRpg plugin";
                InstallPluginButton.Appearance = loader.PluginInstalled
                    ? Wpf.Ui.Controls.ControlAppearance.Secondary
                    : Wpf.Ui.Controls.ControlAppearance.Primary;
            }
        }
    }

    static void SetLight(System.Windows.Shapes.Ellipse light, System.Windows.Controls.TextBlock label, bool on, string name, bool unknown = false)
    {
        if (unknown)
        {
            light.Fill = Gray;
            label.Text = $"{name}: —";
            return;
        }
        light.Fill = on ? Green : Red;
        label.Text = on ? $"{name}: online" : $"{name}: stopped";
    }

    /// <summary>
    /// Asks before taking the game and server down with us. Defaults to <c>No</c>: closing the
    /// launcher mid-match must never cost a run just because someone hit Enter.
    /// </summary>
    MessageBoxResult AskHowToExit() =>
        System.Windows.MessageBox.Show(
            this,
            "Exit launcher only (leave server/game running),\nor stop everything?\n\n" +
            "Yes = stop server + game and exit\n" +
            "No = exit launcher only (safe while playing)\n" +
            "Cancel = stay open",
            "Close FusionRpg Launcher",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.No);

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose) return;

        var result = AskHowToExit();

        if (result == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (result == MessageBoxResult.Yes)
            _session.StopAll();

        _pollTimer.Stop();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        if (_hwndSource != null)
        {
            GameWindowInterop.UnregisterOverlayHotKey(_hwndSource.Handle);
            _hwndSource.RemoveHook(OverlayHotKeyHook);
            _hwndSource = null;
        }
        _overlayPipe?.Dispose();
        _overlayPipe = null;
        _overlay?.ForceClose();
        _overlay = null;
        _forceClose = true;
    }
}
