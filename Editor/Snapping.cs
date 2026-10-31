namespace Editor;

// The arithmetic behind snapping with the move and scale handles (hold Ctrl while dragging). Kept apart from the handles so it
// can be tested on its own.
public static class Snapping
{
    // How far to move along a world axis so the object's coordinate on it lands on a multiple of step. start is the coordinate
    // when the drag began and moved how far the mouse has dragged it.
    public static float Move(float start, float moved, float step) =>
        step > 0f ? MathF.Round((start + moved) / step) * step - start : moved;

    // A scale on one axis, rounded to a multiple of step (never below one step, so it can't collapse to zero).
    public static float Scale(float scale, float step) =>
        step > 0f ? MathF.Max(step, MathF.Round(scale / step) * step) : scale;

    // How much to multiply every axis by when scaling evenly: the factor goes up and down in whole steps (0.1 is 10% at a time).
    public static float Factor(float factor, float step) =>
        step > 0f ? MathF.Max(step, MathF.Round(factor / step) * step) : factor;
}
