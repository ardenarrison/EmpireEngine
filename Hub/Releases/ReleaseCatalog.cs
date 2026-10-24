using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EmpireHub;

// A download the hub can offer for one computer: a zip made by publish.py, which holds Empire/<version>/ (the editor).
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

// One published version of Empire, as listed on GitHub's Releases page.
public sealed class EmpireRelease
{
    public required string Tag { get; init; }             // "v0.1.1"
    public required string Version { get; init; }         // "0.1.1"
    public required string Title { get; init; }
    public DateTimeOffset? Published { get; init; }
    public bool Prerelease { get; init; }
    public string Notes { get; init; } = "";
    public string? PageUrl { get; init; }
    public ReleaseAsset? Asset { get; init; }             // the zip for this computer, or null when the release has none
    public bool IsLatest { get; set; }                    // the newest version that isn't a pre-release
}

// Finds the versions of Empire that can be installed: the releases of the project's GitHub repository. A release counts
// when its tag is a version ("v0.1.1" or "0.1.1") and it has a zip named Empire-<version>-<platform>.zip for this computer.
public static class ReleaseCatalog
{
    public const string Repository = "ardenarrison/EmpireEngine";

    // For tests and forks: point the hub at another GitHub-style releases list, for example a repository of your own.
    // A value starting with http:// is only accepted for this computer (localhost), never for a real download.
    public const string OverrideVariable = "EMPIRE_RELEASES_URL";

    public static string ApiUrl =>
        Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } custom
            ? custom
            : $"https://api.github.com/repos/{Repository}/releases?per_page=100";

    public static bool UsesOverride => Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 };

    // The platform names publish.py uses: osx-arm64, osx-x64, win-x64, win-arm64, linux-x64, linux-arm64.
    public static string CurrentPlatform
    {
        get
        {
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                var other => other.ToString().ToLowerInvariant(),
            };
            return $"{os}-{arch}";
        }
    }

    public static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };   // a big zip on a slow line
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EmpireHub", "1.0"));   // GitHub requires one
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    // The releases for a platform, newest first. Throws InstallException with a plain explanation when GitHub can't be
    // reached or has nothing to show.
    public static async Task<List<EmpireRelease>> FetchAsync(HttpClient http, CancellationToken ct, string? platform = null)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(ApiUrl, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InstallException($"Couldn't reach GitHub ({ex.Message}). Check your internet connection and try again.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InstallException("GitHub didn't answer in time. Try again in a moment.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InstallException("GitHub has no releases to show for Empire yet. (If the project's repository is private, " +
                                           "its releases can't be listed from here.)");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new InstallException("GitHub says too many requests were made from this connection. Try again in about an hour.");
            if (!response.IsSuccessStatusCode)
                throw new InstallException($"GitHub answered with an error ({(int)response.StatusCode}). Try again later.");

            string json = await response.Content.ReadAsStringAsync(ct);
            return Parse(json, platform ?? CurrentPlatform);
        }
    }

    public static List<EmpireRelease> Parse(string json, string platform)
    {
        List<GitHubRelease> raw;
        try
        {
            raw = JsonSerializer.Deserialize(json, ReleaseJson.Default.ListGitHubRelease) ?? new();
        }
        catch (JsonException)
        {
            throw new InstallException("GitHub's answer wasn't in the expected form, so the releases couldn't be read.");
        }

        var releases = new List<EmpireRelease>();
        foreach (var r in raw)
        {
            if (r.Draft || VersionOf(r.TagName) is not { } version) continue;   // drafts aren't public; other tags aren't versions
            releases.Add(new EmpireRelease
            {
                Tag = r.TagName,
                Version = version,
                Title = string.IsNullOrWhiteSpace(r.Name) ? $"Empire {version}" : r.Name!,
                Published = r.PublishedAt,
                Prerelease = r.Prerelease,
                Notes = (r.Body ?? "").Trim(),
                PageUrl = r.HtmlUrl,
                Asset = PickAsset(r.Assets, version, platform),
            });
        }

        var ordered = releases.OrderByDescending(r => Numeric(r.Version))
            .ThenBy(r => r.Prerelease ? 1 : 0).ToList();
        if (ordered.FirstOrDefault(r => !r.Prerelease) is { } latest) latest.IsLatest = true;
        return ordered;
    }

    // The numbers of a version, "0.2.0-beta" being 0.2.0, for putting versions in order.
    public static Version Numeric(string text) =>
        Version.TryParse(text.Split('-', '+')[0], out var v) ? v : new Version(0, 0);

    // "v0.1.1" and "0.1.1" are versions; "nightly" is not. Returns the version without a leading v, or null.
    public static string? VersionOf(string tag)
    {
        string text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        return Regex.IsMatch(text, @"^\d+(\.\d+){1,3}([-+][0-9A-Za-z][0-9A-Za-z.\-+]*)?$") ? text : null;
    }

    // The release's zip for this platform: Empire-<version>-<platform>.zip. A zip for another version number still counts
    // (a release can be re-tagged), but the one named for this version wins.
    private static ReleaseAsset? PickAsset(List<GitHubAsset>? assets, string version, string platform)
    {
        var matches = (assets ?? new()).Where(a =>
                (a.State is null or "uploaded") &&
                a.BrowserDownloadUrl.Length > 0 &&
                a.Name.StartsWith("Empire-", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith($"-{platform}.zip", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var best = matches.FirstOrDefault(a => a.Name.Equals($"Empire-{version}-{platform}.zip", StringComparison.OrdinalIgnoreCase))
                   ?? matches.FirstOrDefault();
        if (best is null) return null;

        string? sha = best.Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..].ToLowerInvariant() : null;
        return new ReleaseAsset(best.Name, best.BrowserDownloadUrl, best.Size, sha);
    }
}

// A problem with a plain-English message that can be shown to the user as it is.
public sealed class InstallException(string message) : Exception(message);
