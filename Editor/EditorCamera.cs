using System.Numerics;
using Brutal.GlfwApi;
using Brutal.Numerics;

namespace Editor;

// Fly camera: right-drag to look; hold right-drag and use WASD (Q/E down/up, Shift to go faster).
// Scrolling (a mouse wheel, or two fingers on a trackpad) over the 3D view zooms by moving the camera along its view
// direction: scroll up to move in, down to move out. Hold Shift to zoom faster.
public sealed class EditorCamera
{
    public Vector3 Position;
    public float Yaw;     // radians around Y; 0 looks down -Z
    public float Pitch;   // radians, clamped
    public float Far = ProjectSettings.DefaultFarClipPlane;     // how far the scene view draws (File > Project Settings)
    public float Near = ProjectSettings.DefaultNearClipPlane;   // how close things can be and still be drawn (File > Project Settings)

    private double2? _lastCursor;

    // The point zooming is measured from. Each scroll step moves the camera a tenth of its distance to this point
    // (at least 0.5 units), so zooming is fine up close and quick when far away.
    public Vector3 Focus;

    public Vector3 Forward => new(
        MathF.Cos(Pitch) * MathF.Sin(Yaw),
        MathF.Sin(Pitch),
        -MathF.Cos(Pitch) * MathF.Cos(Yaw));

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitY);

    public void LookAt(Vector3 target)
    {
        var dir = Vector3.Normalize(target - Position);
        Pitch = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));
        Yaw = MathF.Atan2(dir.X, -dir.Z);
    }

    // A pinch on a trackpad: amount is the fraction the fingers moved together (positive) or apart (negative) since last time.
    public void Pinch(float amount)
    {
        if (amount == 0f) return;
        float distance = MathF.Max(0.5f, Vector3.Distance(Position, Focus));
        Position += Forward * (distance * Math.Clamp(amount, -0.9f, 0.9f));
    }

    // uiWantsMouse is true when the cursor is over an editor panel, so the UI keeps the mouse (and its scrolling).
    // scroll is this frame's vertical scroll amount: about 1 per wheel notch, in small fractions on a trackpad.
    public void Update(GlfwWindow window, float dt, bool uiWantsMouse, float scroll = 0f)
    {
        if (!uiWantsMouse && scroll != 0f)
        {
            float step = MathF.Max(0.5f, Vector3.Distance(Position, Focus) * 0.1f);
            bool fast = IsDown(window, GlfwKey.LeftShift) || IsDown(window, GlfwKey.RightShift);
            Position += Forward * (scroll * step * (fast ? 3f : 1f));
        }

        bool look = !uiWantsMouse && window.GetMouseButton(GlfwMouseButton.Right) == GlfwButtonAction.Press;
        var cursor = window.CursorPos;

        if (!look)
        {
            _lastCursor = null;
            return;
        }

        if (_lastCursor is { } last)
        {
            Yaw += (float)(cursor.X - last.X) * 0.003f;
            Pitch = Math.Clamp(Pitch - (float)(cursor.Y - last.Y) * 0.003f, -1.5f, 1.5f);
        }
        _lastCursor = cursor;

        float speed = 6f * dt * (IsDown(window, GlfwKey.LeftShift) ? 3f : 1f);
        var move = Vector3.Zero;
        if (IsDown(window, GlfwKey.W)) move += Forward;
        if (IsDown(window, GlfwKey.S)) move -= Forward;
        if (IsDown(window, GlfwKey.D)) move += Right;
        if (IsDown(window, GlfwKey.A)) move -= Right;
        if (IsDown(window, GlfwKey.E)) move += Vector3.UnitY;
        if (IsDown(window, GlfwKey.Q)) move -= Vector3.UnitY;
        if (move != Vector3.Zero)
            Position += Vector3.Normalize(move) * speed;
    }

    public static bool IsDown(GlfwWindow window, GlfwKey key) =>
        window.GetKey(key) == GlfwKeyAction.Press;
}
