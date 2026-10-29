using System.Net.Http.Headers;
using System.Text.Json;

namespace WildTrack.Projects;

/// <summary>
/// Asks GitHub for the latest published release and compares it with this build's version (Directory.Build.props,
/// the same for every project). One unauthenticated GET to the public API: no account, no data sent beyond the
/// request itself, and any failure (offline, rate limit, odd tag) is simply "don't know", never an error the user
/// sees. Off when config.json says <c>"checkUpdates": "off"</c> (the toggle in "Acerca de").
/// </summary>
public static class UpdateCheck
{
    public const string Repo = "xtrimdr/WildTrack";
    public const string ReleasesUrl = $"https://github.com/{Repo}/releases/latest";
    const string ApiUrl = $"https://api.github.com/repos/{Repo}/releases/latest";

    /// <summary>The check never holds anything up: past this it gives up silently.</summary>
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    /// <summary>The latest release: its version, tag ("v0.9.8") and page.</summary>
    public sealed record Release(Version Version, string Tag, string Url);

    /// <summary>This build's version (major.minor.build).</summary>
    public static Version Current
    {
        get
        {
            var v = typeof(UpdateCheck).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static bool Enabled
    {
        get => AppConfig.Get("checkUpdates") != "off";
        set => AppConfig.Set("checkUpdates", value ? null : "off");
    }

    /// <summary>The latest release on GitHub, or null when it can't be known (no network, no releases, bad answer).</summary>
    public static async Task<Release?> LatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = Timeout };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WildTrack", Current.ToString(3)));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var res = await http.GetAsync(ApiUrl, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
            string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            string url = root.TryGetProperty("html_url", out var u) && u.GetString() is { Length: > 0 } s ? s : ReleasesUrl;
            return tag != null && Parse(tag) is { } v ? new Release(v, tag, url) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>"v0.9.8", "0.9.8", "v1.0" → a three-part version; anything after '-' or '+' (pre-release, commit) is dropped.</summary>
    public static Version? Parse(string tag)
    {
        string s = tag.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s.Contains('.') ? s : s + ".0", out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public static bool IsNewer(Release r) => r.Version > Current;
}
