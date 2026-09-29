using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FusionRpg.Launcher.Services;

public sealed class OfficialReleaseDownloader
{
    readonly HttpClient _http;

    public OfficialReleaseDownloader(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("FusionRpg-Launcher/1.0");
        if (!_http.DefaultRequestHeaders.Accept.Any())
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public sealed record RemoteAsset(string Name, string DownloadUrl, string TagName, string HtmlUrl);

    public async Task<RemoteAsset> ResolveAssetAsync(
        string owner,
        string repo,
        string tagOrLatest,
        string assetRegex,
        CancellationToken ct = default)
    {
        var url = string.Equals(tagOrLatest, "latest", StringComparison.OrdinalIgnoreCase)
            ? $"https://api.github.com/repos/{owner}/{repo}/releases/latest"
            : $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tagOrLatest}";

        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? tagOrLatest;
        var html = root.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : "";
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Release {owner}/{repo}@{tag} has no assets.");

        foreach (var a in assets.EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (!LoaderManifest.AssetMatches(name, assetRegex)) continue;
            var dl = a.GetProperty("browser_download_url").GetString()
                     ?? throw new InvalidOperationException("Asset missing browser_download_url.");
            return new RemoteAsset(name, dl, tag, html);
        }

        throw new InvalidOperationException(
            $"No asset matching /{assetRegex}/ in {owner}/{repo}@{tag}.");
    }

    public async Task DownloadAsync(
        string downloadUrl,
        string destFile,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(destFile);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var partial = destFile + ".partial";
        try
        {
            using var resp = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;
            await using (var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = File.Create(partial))
            {
                var buffer = new byte[81920];
                long readTotal = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    readTotal += n;
                    if (total is > 0)
                        progress?.Report(readTotal / (double)total.Value);
                }
            }
            progress?.Report(1);
            if (File.Exists(destFile))
                File.Delete(destFile);
            File.Move(partial, destFile);
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { /* ignore */ }
            throw;
        }
    }
}