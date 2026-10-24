namespace EmpireHub;

// Which installed editor to suggest for a project whose own version isn't installed.
public static class EditorVersions
{
    // The closest version to open a project with: the lowest installed version that is at least as new as the one the
    // project wants (older projects usually open fine in a newer editor, not the other way round), otherwise the newest
    // installed one. Null when nothing is installed.
    public static string? Suggest(string wanted, IEnumerable<string> installed)
    {
        var ordered = installed.OrderBy(v => ReleaseCatalog.Numeric(v)).ThenBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        if (ordered.Count == 0) return null;
        var target = ReleaseCatalog.Numeric(wanted);
        return ordered.FirstOrDefault(v => ReleaseCatalog.Numeric(v) >= target) ?? ordered[^1];
    }
}
