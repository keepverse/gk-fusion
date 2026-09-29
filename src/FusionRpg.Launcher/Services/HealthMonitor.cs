using System.Net.Http;
using System.Text.Json;

namespace FusionRpg.Launcher.Services;

public sealed class HealthMonitor
{
    readonly HttpClient _http;

    public HealthMonitor(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    }

    /// <summary>Short-timeout client used by PortPicker ownership checks.</summary>
    public static HealthMonitor ForPortProbe() =>
        new(new HttpClient { Timeout = TimeSpan.FromMilliseconds(400) });

    public sealed record HealthSnapshot(
        bool Reachable,
        bool Ok,
        bool InjectorConnected,
        string? Source,
        string? RawError,
        // E46 (player-content-boot): whether the server's own catalog tables are populated ("imported")
        // or it is still on the shipped code fallback ("codeFallback"), plus why, so the launcher can
        // show it on the one status surface the player already looks at — never only in a server log.
        string? ContentSource = null,
        string? ContentImportError = null);

    public async Task<HealthSnapshot> CheckAsync(string baseUrl, CancellationToken ct = default)
    {
        var url = baseUrl.TrimEnd('/') + "/health";
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new HealthSnapshot(false, false, false, null, $"HTTP {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            var inj = root.TryGetProperty("injectorConnected", out var injEl) && injEl.ValueKind == JsonValueKind.True;
            var source = root.TryGetProperty("source", out var srcEl) ? srcEl.GetString() : null;
            var contentSource = root.TryGetProperty("contentSource", out var csEl) ? csEl.GetString() : null;
            var contentError = root.TryGetProperty("contentImportError", out var cieEl) ? cieEl.GetString() : null;
            return new HealthSnapshot(true, ok, inj, source, null, contentSource, contentError);
        }
        catch (Exception ex)
        {
            return new HealthSnapshot(false, false, false, null, ex.Message);
        }
    }

    public async Task<bool> WaitUntilOkAsync(string baseUrl, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var snap = await CheckAsync(baseUrl, ct).ConfigureAwait(false);
            if (snap.Reachable && snap.Ok) return true;
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>True when GET /health returns JSON with ok=true (our FusionRpg.Server).</summary>
    public bool LooksLikeOurServer(int port)
    {
        try
        {
            var snap = CheckAsync($"http://127.0.0.1:{port}").GetAwaiter().GetResult();
            return snap.Reachable && snap.Ok;
        }
        catch
        {
            return false;
        }
    }
}
