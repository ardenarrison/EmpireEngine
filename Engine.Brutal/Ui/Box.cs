using Brutal.Numerics;

namespace Engine.Brutal.Ui;

// A rectangle in canvas pixels.
public readonly record struct Box(float X, float Y, float W, float H)
{
    public bool Contains(float2 p) => p.X >= X && p.X < X + W && p.Y >= Y && p.Y < Y + H;
}
