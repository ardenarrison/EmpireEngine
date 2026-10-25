using Avalonia;
using Avalonia.Styling;

namespace EmpireHub;

// The Hub's look: dark, light, or whichever the system is set to. Chosen on the About page and remembered in the Hub's settings.
public static class HubTheme
{
    public const string System = "system", Light = "light", Dark = "dark";

    // The theme the setting stands for. An unknown value is the dark theme the Hub always had.
    public static ThemeVariant VariantOf(string? setting) => setting?.ToLowerInvariant() switch
    {
        System => ThemeVariant.Default,
        Light => ThemeVariant.Light,
        _ => ThemeVariant.Dark,
    };

    public static void Apply(string? setting)
    {
        if (Application.Current is { } app) app.RequestedThemeVariant = VariantOf(setting);
    }

    // What to tell an editor the Hub opens so it looks like the Hub: --lightmode, --darkmode, or nothing when the Hub follows the system (the
    // editor then follows it too).
    public static string? EditorFlag(string? setting) => setting?.ToLowerInvariant() switch
    {
        System => null,
        Light => "--lightmode",
        _ => "--darkmode",
    };

    // The three choices in the order the list on the About page shows them.
    public static readonly string[] Choices = [System, Light, Dark];
}
