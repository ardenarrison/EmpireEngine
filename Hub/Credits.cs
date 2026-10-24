namespace EmpireHub;

// Licenses/credits.json, shared with the editor's Help > Credits window.
public sealed class CreditsFile
{
    public string BrutalNotice { get; set; } = "";
    public List<CreditEntry> Entries { get; set; } = new();
}

public sealed class CreditEntry
{
    public string Name { get; set; } = "";
    public string Copyright { get; set; } = "";
    public string License { get; set; } = "";
    public string File { get; set; } = "";
    public string Url { get; set; } = "";
    public List<string> UsedIn { get; set; } = new();
}

// One row of the About page: a piece of third-party software and the file holding its license.
public sealed class CreditItem
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string FilePath { get; init; }

    // The hub's entries, then the bundled editor's (BRUTAL and the components it includes, read from the editor's
    // own Licenses folder so they match the version shipped).
    public static (string Notice, List<CreditItem> Items) Load(IReadOnlyList<EditorInstall> installs)
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "Licenses");
        var credits = System.Text.Json.JsonSerializer.Deserialize(
            System.IO.File.ReadAllText(Path.Combine(folder, "credits.json")), HubJson.Default.CreditsFile) ?? new CreditsFile();

        var items = new List<CreditItem>();
        foreach (var e in credits.Entries.Where(e => e.UsedIn.Contains("hub")))
            items.Add(new CreditItem { Title = e.Name, Subtitle = $"{e.License} · {e.Copyright}", FilePath = Path.Combine(folder, e.File) });

        foreach (var e in credits.Entries.Where(e => !e.UsedIn.Contains("hub") && e.UsedIn.Contains("editor")))
            items.Add(new CreditItem { Title = e.Name, Subtitle = $"{e.License} · {e.Copyright} · in the editor", FilePath = Path.Combine(folder, e.File) });

        if (installs.FirstOrDefault(i => i.Bundled) is { } bundled)
        {
            string thirdParty = Path.Combine(bundled.Folder, "Licenses", "ThirdParty");
            if (Directory.Exists(thirdParty))
                foreach (var file in Directory.GetFiles(thirdParty, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileNameWithoutExtension(file).Replace("BRUTAL-", "").Replace("-THIRD-PARTY-NOTICES", "");
                    items.Add(new CreditItem { Title = $"BRUTAL components: {name}", Subtitle = "Notices for software included in BRUTAL · in the editor", FilePath = file });
                }
        }
        return (credits.BrutalNotice, items);
    }
}
