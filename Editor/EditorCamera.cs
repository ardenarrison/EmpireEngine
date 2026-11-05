using System.Numerics;
using Brutal.GlfwApi;
using Brutal.Numerics;

namespace Editor;

// Fly camera: right-drag to look; hold right-drag and use WASD (Q/E down/up, Shift to go faster).
// Scrolling (a mouse wheel, or two fingers on a trackpad) over the 3D view zooms by moving the camera along its view
// direction: scroll up to move in, down to move out. Hold Shift to zoom faster.
//
// In a 2D project (Is2D) it looks straight down -Z at the X-Y plane through an orthographic view instead, and never turns:
// right-drag (or a left-drag on empty space, see EditorApp) pans, WASD pans while the right button is held, and scrolling or
// pinching zooms by changing OrthoSize, half the height of what it shows.
public sealed class EditorCamera
{
    public Vector3 Position;
    public float Yaw;     // radians around Y; 0 looks down -Z
    public float Pitch;   // radians, clamped
    public float Far = ProjectSettings.DefaultFarClipPlane;     // how far the scene view draws (File > Project Settings)
    public float Near = ProjectSettings.DefaultNearClipPlane;   // how close things can be and still be drawn (File > Project Settings)

    private double2? _lastCursor;

    public bool Is2D;
    public float OrthoSize = 5f;
    public float ViewHeightPixels = 600f;   // the scene view's height in mouse units, for panning in 2D (set each frame)

    public const float MinOrthoSize = 0.05f;
    public const float MaxOrthoSize = 10_000f;

    // World units one mouse unit covers in 2D.
    public float UnitsPerPixel => OrthoSize * 2f / MathF.Max(1f, ViewHeightPixels);

    // Moves a 2D view by a mouse movement, so the world follows the cursor.
    public void Pan(float dxPixels, float dyPixels)
    {
        float k = UnitsPerPixel;
        Position += new Vector3(-dxPixels * k, dyPixels * k, 0f);
    }

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
        if (Is2D)
        {
            OrthoSize = Math.Clamp(OrthoSize * (1f - Math.Clamp(amount, -0.9f, 0.9f)), MinOrthoSize, MaxOrthoSize);
            return;
        }
        float distance = MathF.Max(0.5f, Vector3.Distance(Position, Focus));
        Position += Forward * (distance * Math.Clamp(amount, -0.9f, 0.9f));
    }

    // Two fingers dragged over the scene on a touch screen: circles the camera around the point it looks at, or pans a 2D view.
    public void Swipe(float dxPixels, float dyPixels)
    {
        if (dxPixels == 0f && dyPixels == 0f) return;
        if (Is2D)
        {
            Pan(dxPixels, dyPixels);
            return;
        }
        float distance = MathF.Max(0.5f, Vector3.Distance(Position, Focus));
        Yaw += dxPixels * 0.005f;
        Pitch = Math.Clamp(Pitch - dyPixels * 0.005f, -1.5f, 1.5f);
        Position = Focus - Forward * distance;
    }

    // uiWantsMouse is true when the cursor is over an editor panel, so the UI keeps the mouse (and its scrolling).
    // scroll is this frame's vertical scroll amount: about 1 per wheel notch, in small fractions on a trackpad.
    public void Update(IAppWindow window, float dt, bool uiWantsMouse, float scroll = 0f)
    {
        if (Is2D)
        {
            Update2D(window, dt, uiWantsMouse, scroll);
            return;
        }
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

    private void Update2D(IAppWindow window, float dt, bool uiWantsMouse, float scroll)
    {
        Yaw = 0f;
        Pitch = 0f;
        if (!uiWantsMouse && scroll != 0f)
        {
            bool fast = IsDown(window, GlfwKey.LeftShift) || IsDown(window, GlfwKey.RightShift);
            OrthoSize = Math.Clamp(OrthoSize * MathF.Pow(0.9f, scroll * (fast ? 3f : 1f)), MinOrthoSize, MaxOrthoSize);
        }

        bool drag = !uiWantsMouse && window.GetMouseButton(GlfwMouseButton.Right) == GlfwButtonAction.Press;
        var cursor = window.CursorPos;
        if (!drag)
        {
            _lastCursor = null;
            return;
        }
        if (_lastCursor is { } last)
            Pan((float)(cursor.X - last.X), (float)(cursor.Y - last.Y));
        _lastCursor = cursor;

        // WASD pans by about the view's height per second (three times that with Shift).
        float speed = OrthoSize * 2f * dt * (IsDown(window, GlfwKey.LeftShift) ? 3f : 1f);
        var move = Vector3.Zero;
        if (IsDown(window, GlfwKey.W)) move += Vector3.UnitY;
        if (IsDown(window, GlfwKey.S)) move -= Vector3.UnitY;
        if (IsDown(window, GlfwKey.D)) move += Vector3.UnitX;
        if (IsDown(window, GlfwKey.A)) move -= Vector3.UnitX;
        if (move != Vector3.Zero)
            Position += Vector3.Normalize(move) * speed;
    }

    public static bool IsDown(IAppWindow window, GlfwKey key) =>
        window.GetKey(key) == GlfwKeyAction.Press;
}
