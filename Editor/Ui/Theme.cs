using Brutal.Numerics;

namespace Editor.Ui;

// The editor's look. Every panel, the code view and the terminal take their colors from here, as a dark set (the original look) and a
// light one. Light is chosen once when the editor starts (see ThemeChoice); the scene itself is never themed.
public static class Theme
{
    public static bool Light { get; set; }

    // A color as 0xRRGGBB for the dark theme or the light one.
    public static float4 Pick(int dark, int light, float alpha = 1f) => FromRgb(Light ? light : dark, alpha);

    public static int PickRgb(int dark, int light) => Light ? light : dark;

    public static float4 FromRgb(int rgb, float alpha = 1f) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

    // Text drawn on a strong accent color (the highlighted buttons): white in both, since the accent is strong in both.
    public static float4 OnAccent => FromRgb(0xFFFFFF);
}
