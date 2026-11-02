using System.Numerics;

namespace Engine.Core.Scene;

// Shows a picture (PNG, JPEG, BMP or GIF; a GIF shows its first frame) as a flat rectangle facing +Z, the way a 2D project is
// seen. Sprites aren't lit: they show their own colors, tinted by Tint. Transparent pixels are see-through.
//
// The picture is PixelsPerUnit pixels to one world unit, so a 100 x 50 picture is 1 x 0.5 units at the default 100, centered on
// the object. Nearer the camera (a larger Z, for the usual 2D camera) is drawn in front.
public sealed class SpriteRenderer : Component
{
    public string Sprite { get; set; } = "";              // path relative to the project folder
    public Vector4 Tint { get; set; } = Vector4.One;      // multiplies the picture's colors and alpha
    public float PixelsPerUnit { get; set; } = 100f;
    public bool FlipX { get; set; }
    public bool FlipY { get; set; }

    // The files a sprite can show.
    public static bool IsSpriteImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif";

    public static float ClampPixelsPerUnit(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, 0.01f, 100_000f) : 100f;
}
