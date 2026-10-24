using System.Text.Json.Serialization;

namespace EmpireHub;

// The parts of GitHub's "list releases" answer (docs.github.com/rest/releases/releases) that the hub reads.
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    [JsonPropertyName("digest")] public string? Digest { get; set; }   // "sha256:<hex>", which GitHub fills in for new uploads
}

// Written into every editor folder the hub downloads, so the Installs page can tell it from the one that came with the hub.
public sealed class InstallMarker
{
    public string Version { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Source { get; set; } = "";
    public string? Sha256 { get; set; }
    public DateTimeOffset InstalledAt { get; set; }
}

// Source-generated JSON, so it also works in the hub's trimmed builds.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<GitHubRelease>))]
[JsonSerializable(typeof(InstallMarker))]
internal sealed partial class ReleaseJson : JsonSerializerContext;
