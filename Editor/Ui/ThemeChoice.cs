using System.Diagnostics;

namespace Editor.Ui;

// Which look the editor starts with: --lightmode or --darkmode on the command line, and when neither is given, the one the system is set
// to (dark if the system can't be asked). Plain .NET, so it is tested without a window.
public static class ThemeChoice
{
    public const string LightFlag = "--lightmode", DarkFlag = "--darkmode";

    public enum Request { System, Light, Dark }

    // Takes the theme flags out of the arguments and says which was asked for; if both are given the last one wins. `rest` is
    // everything else, in order, so the scene and project paths are found as before wherever the flag was put.
    public static Request FromArguments(string[] args, out string[] rest)
    {
        var request = Request.System;
        var others = new List<string>();
        foreach (var arg in args)
        {
            if (arg.Equals(LightFlag, StringComparison.OrdinalIgnoreCase)) request = Request.Light;
            else if (arg.Equals(DarkFlag, StringComparison.OrdinalIgnoreCase)) request = Request.Dark;
            else others.Add(arg);
        }
        rest = others.ToArray();
        return request;
    }

    // True for the light look. Light and Dark are taken as asked; System asks the computer, and falls back to dark.
    public static bool IsLight(Request request, Func<bool?>? systemIsLight = null) => request switch
    {
        Request.Light => true,
        Request.Dark => false,
        _ => (systemIsLight ?? SystemIsLight)() ?? false,
    };

    // Whether the system is set to a light appearance, or null when that can't be found out.
    public static bool? SystemIsLight()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                // The key only exists while the Mac is in dark mode ("Dark"); in light mode `defaults` fails.
                var (code, output) = Run("/usr/bin/defaults", "read", "-g", "AppleInterfaceStyle");
                return !(code == 0 && output.Contains("Dark", StringComparison.OrdinalIgnoreCase));
            }
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int value ? value != 0 : null;
            }
            var (_, scheme) = Run("gsettings", "get", "org.gnome.desktop.interface", "color-scheme");
            if (FromColorScheme(scheme) is { } fromSettings) return fromSettings;
            return FromGtkTheme(Environment.GetEnvironmentVariable("GTK_THEME"));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // GNOME's color-scheme setting: 'prefer-dark', 'prefer-light' or 'default' (no preference, which is light). Anything else is unknown.
    public static bool? FromColorScheme(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        if (output.Contains("prefer-dark", StringComparison.OrdinalIgnoreCase)) return false;
        if (output.Contains("prefer-light", StringComparison.OrdinalIgnoreCase) || output.Contains("default", StringComparison.OrdinalIgnoreCase)) return true;
        return null;
    }

    // A GTK_THEME value such as "Adwaita:dark" or "Breeze-Dark".
    public static bool? FromGtkTheme(string? theme) =>
        string.IsNullOrWhiteSpace(theme) ? null : theme.Contains("dark", StringComparison.OrdinalIgnoreCase) ? false : null;

    private static (int Code, string Output) Run(string program, params string[] arguments)
    {
        var info = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("could not start " + program);
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(2000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            return (-1, "");
        }
        return (process.ExitCode, output.Result);
    }
}
