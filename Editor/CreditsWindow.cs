using System.Reflection;
using System.Text;
using System.Text.Json;
using Editor.Ui;

namespace Editor;

// Help > Credits: the third-party software in the editor and the license each is used under, read from
// Licenses/credits.json next to the editor. BRUTAL's license requires its copyright notice on a credits screen.
// The text font only has ASCII glyphs, so other characters are spelled out or replaced.
public sealed class CreditsWindow
{
    private sealed record Entry(string Name, string Copyright, string License, string File, List<string> UsedIn);
    private sealed record CreditsFile(string BrutalNotice, List<Entry> Entries);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Licenses");

    private bool _open;
    private CreditsFile? _credits;
    private List<string> _componentNotices = new();
    private string? _shownFile;
    private string[] _shownLines = [];
    private string _error = "";

    public void Open()
    {
        _open = true;
        try
        {
            _credits = JsonSerializer.Deserialize<CreditsFile>(File.ReadAllText(Path.Combine(Folder, "credits.json")), JsonOptions);
            string thirdParty = Path.Combine(Folder, "ThirdParty");
            _componentNotices = Directory.Exists(thirdParty)
                ? Directory.GetFiles(thirdParty, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
            _error = "";
        }
        catch (Exception ex)
        {
            _credits = null;
            _error = $"Could not read {Path.Combine(Folder, "credits.json")}: {ex.Message}";
        }
    }

    public bool IsOpen => _open;
    public void Close() => _open = false;

    public void Draw(Gui g)
    {
        if (!_open) return;

        float w = Math.Min(860f, g.Size.X - 40f), h = Math.Min(560f, g.Size.Y - 80f);
        var rect = new Box((g.Size.X - w) * 0.5f, 50f, w, h);
        if (!g.BeginWindow("Credits", rect, "Credits", WindowFlags.Closable | WindowFlags.NoScroll))
            _open = false;

        string version = typeof(CreditsWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";
        g.Text($"Empire Engine Editor {version}");
        g.Separator();

        if (_credits is null)
            g.TextWrapped(Ascii(_error), Gui.ErrorColor);
        else
            DrawCredits(g);
        g.EndWindow();
    }

    private void DrawCredits(Gui g)
    {
        g.TextWrapped(Ascii(_credits!.BrutalNotice), Gui.DimColor);
        g.Separator();

        g.BeginChild("credits-list", 300f, 0f);
        g.Text("Third-party software");
        int id = 0;
        foreach (var entry in _credits.Entries.Where(e => e.UsedIn.Contains("editor")))
        {
            string path = Path.Combine(Folder, entry.File);
            if (g.Selectable($"{Ascii(entry.Name)} ({Ascii(entry.License)})##c{id++}", _shownFile == path))
                Show(path);
            g.TextWrapped("  " + Ascii(entry.Copyright), Gui.DimColor);
        }

        if (_componentNotices.Count > 0)
        {
            g.Separator();
            g.Text("Components included in BRUTAL");
            foreach (var file in _componentNotices)
            {
                string label = Path.GetFileNameWithoutExtension(file).Replace("BRUTAL-", "").Replace("-THIRD-PARTY-NOTICES", "");
                if (g.Selectable($"{Ascii(label)} notices##n{id++}", _shownFile == file))
                    Show(file);
            }
        }
        g.EndChild();

        g.SameLine();
        g.BeginChild("credits-text", 0f, 0f);
        if (_shownFile is null)
            g.TextWrapped("Pick an entry on the left to read its license.", Gui.DimColor);
        else
            foreach (var line in _shownLines)
                g.Text(line);
        g.EndChild();
    }

    private void Show(string file)
    {
        _shownFile = file;
        try
        {
            _shownLines = File.ReadAllLines(file).Select(Ascii).ToArray();
        }
        catch (Exception ex)
        {
            _shownLines = [$"Could not read {file}: {ex.Message}"];
        }
    }

    // Characters outside ASCII become close ASCII equivalents, or '?'.
    private static string Ascii(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            switch (c)
            {
                case '©': sb.Append("(c)"); break;
                case '‘' or '’': sb.Append('\''); break;
                case '“' or '”': sb.Append('"'); break;
                case '–' or '—': sb.Append('-'); break;
                case '\t': sb.Append("    "); break;
                default: sb.Append(c >= 32 && c < 127 ? c : '?'); break;
            }
        }
        return sb.ToString();
    }
}
