using System.Numerics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Engine.Core.Scene;

namespace Editor;

public enum GizmoTool { None, Move, Scale }

// Move and scale handles drawn over the selected object in the 3D view. Left-drag a handle to edit the object.
// Move uses world axes with arrow tips. Scale uses the object's own axes with box tips, and the center box
// scales evenly (drag right to grow, left to shrink). With several objects selected, the handles sit on the
// primary one and every selected object moves or scales by the same amount. Holding Ctrl while dragging snaps: a move puts the
// primary object on a grid of MoveSnap along that axis (the others keep their distance to it), a scale along an axis rounds it to
// a multiple of ScaleSnap, and the center box scales in steps of ScaleSnap (0.1 is 10% at a time).
public sealed class TransformGizmo
{
    private const float HandlePixels = 8f;    // how close the mouse must be to a handle to grab it
    private const float ScreenSize = 0.18f;   // handle length as a share of the distance to the camera
    private const int Uniform = 3;

    private static readonly float4[] AxisColors =
    {
        new(0.95f, 0.25f, 0.25f, 1f),
        new(0.35f, 0.9f, 0.3f, 1f),
        new(0.3f, 0.5f, 1f, 1f),
    };
    private static readonly float4 CenterColor = new(0.9f, 0.9f, 0.9f, 1f);
    private static readonly float4 ActiveColor = new(1f, 0.85f, 0.2f, 1f);

    public GizmoTool Tool { get; set; } = GizmoTool.None;

    // Grid steps for snapping, in world units and in scale (Project Settings).
    public float MoveSnap { get; set; } = 0.5f;
    public float ScaleSnap { get; set; } = 0.1f;

    private bool _prevLeft;
    private int _dragAxis = -1;       // 0..2 for X, Y, Z, Uniform for the center box, -1 when not dragging
    private Vector3 _dragDir;
    private float _startParam;
    private float2 _startMouse;
    private Vector3 _startWorld;
    private List<(SceneObject Obj, Vector3 World, Vector3 Scale)> _targets = new();   // captured when a drag starts

    public bool IsDragging => _dragAxis >= 0;

    // obj is the primary selection, which carries the handles; others are the rest of the selection.
    // area is the scene rectangle in window pixels (x, y, width, height). Handles are drawn on canvas's current layer.
    // Returns true when an object changed.
    public bool Update(GlfwWindow window, Canvas2D canvas, SceneObject? obj, IReadOnlyCollection<SceneObject> others,
        Matrix4x4 viewProj, Vector3 eye, float4 area, bool uiWantsMouse)
    {
        var cursor = window.CursorPos;
        var mouse = new float2((float)cursor.X, (float)cursor.Y);
        bool left = window.GetMouseButton(GlfwMouseButton.Left) == GlfwButtonAction.Press;
        bool pressed = left && !_prevLeft;
        _prevLeft = left;
        bool snap = window.GetKey(GlfwKey.LeftControl) == GlfwKeyAction.Press || window.GetKey(GlfwKey.RightControl) == GlfwKeyAction.Press;

        if (obj is null || Tool == GizmoTool.None)
        {
            _dragAxis = -1;
            return false;
        }

        var world = obj.WorldMatrix;
        var origin = world.Translation;
        float length = Vector3.Distance(eye, origin) * ScreenSize;
        if (!TryProject(origin, viewProj, area, out var originScreen))
            return false;

        var axes = new Vector3[3];
        var tips = new float2[3];
        var visible = new bool[3];
        for (int i = 0; i < 3; i++)
        {
            axes[i] = Tool == GizmoTool.Move ? Unit(i) : LocalAxis(world, i);
            visible[i] = TryProject(origin + axes[i] * length, viewProj, area, out tips[i]);
        }

        bool changed = false;
        if (_dragAxis >= 0 && !left)
            _dragAxis = -1;
        else if (_dragAxis >= 0)
            changed = Drag(mouse, viewProj, area, length, snap);

        int hover = -1;
        if (_dragAxis < 0 && !uiWantsMouse && Inside(mouse, area))
        {
            hover = HoveredHandle(mouse, originScreen, tips, visible);
            if (pressed && hover >= 0)
                BeginDrag(obj, others, hover, hover == Uniform ? Vector3.Zero : axes[hover], mouse, viewProj, area, origin);
        }

        Draw(canvas, originScreen, tips, visible, _dragAxis >= 0 ? _dragAxis : hover);
        return changed;
    }

    private void BeginDrag(SceneObject obj, IReadOnlyCollection<SceneObject> others, int axis, Vector3 dir, float2 mouse,
        Matrix4x4 viewProj, float4 area, Vector3 origin)
    {
        _dragAxis = axis;
        _dragDir = dir;
        _startMouse = mouse;
        _startWorld = origin;

        // An object under another selected object already follows it, so editing it too would apply the change twice.
        var all = new HashSet<SceneObject>(others) { obj };
        _targets = all.Where(o => !Ancestors(o).Any(all.Contains))
                      .Select(o => (o, o.WorldMatrix.Translation, o.Scale))
                      .ToList();
        _startParam = axis == Uniform ? 0f : AxisParam(mouse, viewProj, area) ?? 0f;
    }

    private bool Drag(float2 mouse, Matrix4x4 viewProj, float4 area, float length, bool snap)
    {
        if (_dragAxis == Uniform)
        {
            float factor = MathF.Max(0.01f, 1f + (mouse.X - _startMouse.X) / 100f);
            if (snap) factor = Snapping.Factor(factor, ScaleSnap);
            foreach (var (obj, _, scale) in _targets)
                obj.Scale = ClampScale(scale * factor);
            return true;
        }

        if (AxisParam(mouse, viewProj, area) is not { } param)
            return false;

        if (Tool == GizmoTool.Move)
        {
            float moved = param - _startParam;
            if (snap) moved = Snapping.Move(Vector3.Dot(_startWorld, _dragDir), moved, MoveSnap);   // move handles are world axes
            var offset = _dragDir * moved;
            foreach (var (obj, world, _) in _targets)
            {
                var target = world + offset;
                obj.Position = obj.Parent is { } parent && Matrix4x4.Invert(parent.WorldMatrix, out var toParent)
                    ? Vector3.Transform(target, toParent)
                    : target;
            }
        }
        else
        {
            // Grabbed near the tip, so the ratio of distances along the axis is the scale factor.
            // Each object scales along its own matching axis.
            float grab = MathF.Abs(_startParam) > length * 0.05f ? _startParam : length;
            float factor = MathF.Max(0.01f, param / grab);
            foreach (var (obj, _, start) in _targets)
            {
                var scale = start;
                switch (_dragAxis)
                {
                    case 0: scale.X = snap ? Snapping.Scale(scale.X * factor, ScaleSnap) : scale.X * factor; break;
                    case 1: scale.Y = snap ? Snapping.Scale(scale.Y * factor, ScaleSnap) : scale.Y * factor; break;
                    default: scale.Z = snap ? Snapping.Scale(scale.Z * factor, ScaleSnap) : scale.Z * factor; break;
                }
                obj.Scale = ClampScale(scale);
            }
        }
        return true;
    }

    private static IEnumerable<SceneObject> Ancestors(SceneObject obj)
    {
        for (var p = obj.Parent; p is not null; p = p.Parent)
            yield return p;
    }

    // Where the mouse ray passes closest to the dragged axis line, as a distance along the axis from the start point.
    private float? AxisParam(float2 mouse, Matrix4x4 viewProj, float4 area)
    {
        if (!Matrix4x4.Invert(viewProj, out var inverse)) return null;
        float ndcX = (mouse.X - area.X) / area.Z * 2f - 1f;
        float ndcY = (mouse.Y - area.Y) / area.W * 2f - 1f;
        var nearH = Vector4.Transform(new Vector4(ndcX, ndcY, 0f, 1f), inverse);
        var farH = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), inverse);
        var rayStart = new Vector3(nearH.X, nearH.Y, nearH.Z) / nearH.W;
        var rayDir = Vector3.Normalize(new Vector3(farH.X, farH.Y, farH.Z) / farH.W - rayStart);

        var w0 = _startWorld - rayStart;
        float b = Vector3.Dot(_dragDir, rayDir);
        float d = Vector3.Dot(_dragDir, w0);
        float e = Vector3.Dot(rayDir, w0);
        float denom = 1f - b * b;
        if (denom < 1e-4f) return null;   // looking straight down the axis
        return (b * e - d) / denom;
    }

    private int HoveredHandle(float2 mouse, float2 origin, float2[] tips, bool[] visible)
    {
        if (Tool == GizmoTool.Scale && Distance(mouse, origin) < HandlePixels)
            return Uniform;

        int best = -1;
        float bestDistance = HandlePixels;
        for (int i = 0; i < 3; i++)
        {
            if (!visible[i]) continue;
            float distance = SegmentDistance(mouse, origin, tips[i]);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }

    private void Draw(Canvas2D c, float2 origin, float2[] tips, bool[] visible, int highlight)
    {
        for (int i = 0; i < 3; i++)
        {
            if (!visible[i]) continue;
            var color = i == highlight ? ActiveColor : AxisColors[i];
            var tip = tips[i];
            c.Line(origin, tip, 3f, color);

            if (Tool == GizmoTool.Move)
            {
                // Arrow head pointing away from the origin.
                var dir = Normalize(new float2(tip.X - origin.X, tip.Y - origin.Y));
                var side = new float2(-dir.Y, dir.X);
                var point = new float2(tip.X + dir.X * 14f, tip.Y + dir.Y * 14f);
                var a = new float2(tip.X + side.X * 6f, tip.Y + side.Y * 6f);
                var b = new float2(tip.X - side.X * 6f, tip.Y - side.Y * 6f);
                c.Triangle(point, a, b, color);
            }
            else
            {
                c.Rect(tip.X - 6f, tip.Y - 6f, 12f, 12f, color);
            }
        }

        if (Tool == GizmoTool.Scale)
            c.Rect(origin.X - 7f, origin.Y - 7f, 14f, 14f, highlight == Uniform ? ActiveColor : CenterColor);
        else
            c.Circle(origin, 4f, CenterColor);
    }

    private static Vector3 Unit(int axis) => axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };

    // The object's own axis in world space, so scale handles turn with the object.
    private static Vector3 LocalAxis(Matrix4x4 world, int axis)
    {
        var dir = Vector3.TransformNormal(Unit(axis), world);
        return dir.LengthSquared() < 1e-12f ? Unit(axis) : Vector3.Normalize(dir);
    }

    private static Vector3 ClampScale(Vector3 s) =>
        new(MathF.Max(0.001f, s.X), MathF.Max(0.001f, s.Y), MathF.Max(0.001f, s.Z));

    // Same mapping as the camera gizmos: world point to pixels inside the scene rectangle.
    private static bool TryProject(Vector3 point, Matrix4x4 viewProj, float4 area, out float2 screen)
    {
        var clip = Vector4.Transform(new Vector4(point, 1f), viewProj);
        if (clip.W <= 0.01f)
        {
            screen = default;
            return false;
        }
        screen = new float2(area.X + (clip.X / clip.W * 0.5f + 0.5f) * area.Z,
                            area.Y + (clip.Y / clip.W * 0.5f + 0.5f) * area.W);
        return true;
    }

    private static bool Inside(float2 p, float4 area) =>
        p.X >= area.X && p.X <= area.X + area.Z && p.Y >= area.Y && p.Y <= area.Y + area.W;

    private static float SegmentDistance(float2 p, float2 a, float2 b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float lengthSq = dx * dx + dy * dy;
        float t = lengthSq < 1e-6f ? 0f : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSq, 0f, 1f);
        return Distance(p, new float2(a.X + t * dx, a.Y + t * dy));
    }

    private static float Distance(float2 a, float2 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static float2 Normalize(float2 v)
    {
        float length = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        return length < 1e-6f ? new float2(1f, 0f) : new float2(v.X / length, v.Y / length);
    }
}
