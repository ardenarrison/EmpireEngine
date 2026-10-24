namespace EmpireHub;

// One row of the Install version window: a published release and what can be done with it on this computer.
public sealed class ReleaseItem
{
    public required EmpireRelease Release { get; init; }
    public bool Installed { get; init; }

    public string Title => $"Empire {Release.Version}";
    public bool IsLatest => Release.IsLatest;
    public bool IsPrerelease => Release.Prerelease;
    public bool CanInstall => !Installed && Release.Asset is not null;
    public string ButtonText => Installed ? "Installed" : "Install";
    public string Notes => Release.Notes;

    public string StateText =>
        Installed ? "" :
        Release.Asset is null ? $"No download for {ReleaseCatalog.CurrentPlatform}" : "";

    // "Published 24 Oct 2026 · 85 MB"
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (Release.Published is { } date) parts.Add("Published " + date.LocalDateTime.ToString("d MMM yyyy"));
            if (Release.Asset is { Size: > 0 } asset) parts.Add(FormatSize(asset.Size));
            return string.Join(" · ", parts);
        }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";
}
