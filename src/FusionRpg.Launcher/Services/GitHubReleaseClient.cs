using System.Net.Http;
using System.Text.Json;

namespace FusionRpg.Launcher.Services;

public sealed class GitHubReleaseClient
{
    public const string Owner = "letuhao";
    public const string Repo = "plant-vs-zombie-rise-of-summoner";
    public static string RepoUrl => $"https://github.com/{Owner}/{Repo}";
    public static string ReleasesUrl => $"{RepoUrl}/releases";
    public static string DocsUrl => $"{RepoUrl}#readme";

    public const string DefaultAssetRegex = @"^FusionRpg-win-x64\.zip$";

    readonly HttpClient _http;
    readonly string _owner;
    readonly string _repo;
    readonly string _assetRegex;

    public GitHubReleaseClient(HttpClient? http = null)
        : this(Owner, Repo, DefaultAssetRegex, http)
    {
    }

    public GitHubReleaseClient(string owner, string repo, string? assetRegex = null, HttpClient? http = null)
    {
        _owner = string.IsNullOrWhiteSpace(owner) ? Owner : owner;
        _repo = string.IsNullOrWhiteSpace(repo) ? Repo : repo;
        _assetRegex = string.IsNullOrWhiteSpace(assetRegex) ? DefaultAssetRegex : assetRegex;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("FusionRpg-Launcher/1.0");
    }

    public static GitHubReleaseClient ForManifest(LoaderManifest.FusionRpgChannel channel, HttpClient? http = null) =>
        new(channel.Owner, channel.Repo, channel.AssetRegex, http);

    public sealed record ReleaseInfo(
        string TagName,
        string HtmlUrl,
        string? Name,
        string? Body,
        bool Found,
        string? AssetName = null,
        string? DownloadUrl = null)
    {
        public bool HasPreferredZip =>
            !string.IsNullOrEmpty(AssetName) && !string.IsNullOrEmpty(DownloadUrl);
    }

    public async Task<ReleaseInfo> GetLatestAsync(CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
        var releasesUrl = $"https://github.com/{_owner}/{_repo}/releases";
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new ReleaseInfo("", releasesUrl, null, null, false);

            if (!resp.IsSuccessStatusCode)
                return new ReleaseInfo("", releasesUrl, null, $"HTTP {(int)resp.StatusCode}", false);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var html = root.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? releasesUrl : releasesUrl;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            var body = root.TryGetProperty("body", out var b) ? b.GetString() : null;

            string? assetName = null;
            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var an = a.GetProperty("name").GetString() ?? "";
                    if (!LoaderManifest.AssetMatches(an, _assetRegex)) continue;
                    assetName = an;
                    downloadUrl = a.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() : null;
                    break;
                }
            }

            return new ReleaseInfo(tag, html, name, body, true, assetName, downloadUrl);
        }
        catch (Exception ex)
        {
            return new ReleaseInfo("", releasesUrl, null, ex.Message, false);
        }
    }

    /// <summary>Strip leading v and NuGet/SDK metadata (+build / -prerelease kept only if Version can parse; +sha stripped).</summary>
    public static string NormalizeVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "";
        var v = version.Trim().TrimStart('v', 'V');
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        // Keep pre-release suffix for Version.TryParse when possible; strip only if it breaks parse later
        return v.Trim();
    }

    public static bool IsNewerThan(string releaseTag, string localVersion)
    {
        var a = NormalizeVersion(releaseTag);
        var b = NormalizeVersion(localVersion);
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        // Version.TryParse does not like some prerelease forms — strip -metadata for numeric compare when needed
        static string ForParse(string s)
        {
            var dash = s.IndexOf('-');
            return dash >= 0 ? s[..dash] : s;
        }
        if (Version.TryParse(ForParse(a), out var va) && Version.TryParse(ForParse(b), out var vb))
            return va > vb;
        return !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
