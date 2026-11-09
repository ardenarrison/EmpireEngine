using System.Numerics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Engine.Core.Scene;

namespace Editor;

// Editing a Polygon Collider 2D's outline in the scene view: drag a point to move it, drag the dot in the middle of an edge to add a
// point there, right-click a point to remove it. It holds the object's id, not the collider, because undo rebuilds the scene.
public sealed class PolygonEditor
{
    private static readonly float4 PointColor = new(1f, 0.85f, 0.25f, 1f);
    private static readonly float4 ChosenColor = new(1f, 0.45f, 0.15f, 1f);
    private static readonly float4 EdgeDotColor = new(1f, 1f, 1f, 0.75f);

    private static float Reach => OperatingSystem.IsIOS() ? 22f : 10f;

    private Guid? _object;
    private int _index;           // which of the object's polygon colliders
    private int _grabbed = -1;    // the point being dragged
    private bool _prevLeft, _prevRight;

    public bool Active => _object is not null;
    public bool Dragging => _grabbed >= 0;
    public bool OverHandle { get; private set; }   // the mouse is on something here, so the scene shouldn't take a click
    public int ChosenPoint { get; private set; } = -1;   // the point last touched, which the Inspector's Remove Point removes

    public bool IsEditing(SceneObject obj, PolygonCollider2D polygon) =>
        _object == obj.Id && obj.Components.OfType<PolygonCollider2D>().ElementAtOrDefault(_index) == polygon;

    public void Begin(SceneObject obj, PolygonCollider2D polygon)
    {
        _object = obj.Id;
        _index = obj.Components.OfType<PolygonCollider2D>().ToList().IndexOf(polygon);
        ChosenPoint = -1;
    }

    public void End()
    {
        _object = null;
        _grabbed = -1;
        OverHandle = false;
        ChosenPoint = -1;
    }

    // Handles the mouse and draws the handles. Only the selected object's collider can be edited, so choosing another ends this.
    public void Update(IAppWindow window, Canvas2D canvas, SceneObject? selected, Matrix4x4 viewProj, float4 area, bool uiWantsMouse)
    {
        bool left = window.GetMouseButton(GlfwMouseButton.Left) == GlfwButtonAction.Press;
        bool right = window.GetMouseButton(GlfwMouseButton.Right) == GlfwButtonAction.Press;
        bool pressed = left && !_prevLeft, rightPressed = right && !_prevRight;
        _prevLeft = left;
        _prevRight = right;

        if (_object is null) return;
        if (selected?.Id != _object || selected.Components.OfType<PolygonCollider2D>().ElementAtOrDefault(_index) is not { } polygon)
        {
            End();
            return;
        }

        var toWorld = Matrix4x4.CreateTranslation(polygon.Center) * selected.WorldMatrix;
        var cursor = window.CursorPos;
        var mouse = new float2((float)cursor.X, (float)cursor.Y);
        var points = polygon.Points;

        if (_grabbed >= 0)
        {
            if (!left) _grabbed = -1;
            else if (_grabbed < points.Count && PointUnder(mouse, toWorld, viewProj, area) is { } moved) points[_grabbed] = moved;
        }

        int overPoint = -1, overEdge = -1;
        bool inside = !uiWantsMouse && mouse.X >= area.X && mouse.Y >= area.Y && mouse.X <= area.X + area.Z && mouse.Y <= area.Y + area.W;
        if (inside && _grabbed < 0)
            for (int i = 0; i < points.Count && overPoint < 0; i++)
            {
                if (Near(points[i], mouse, toWorld, viewProj, area)) overPoint = i;
                else if (overEdge < 0 && Near((points[i] + points[(i + 1) % points.Count]) / 2f, mouse, toWorld, viewProj, area)) overEdge = i;
            }
        OverHandle = _grabbed >= 0 || overPoint >= 0 || overEdge >= 0;

        if (pressed && overPoint >= 0)
            ChosenPoint = _grabbed = overPoint;
        else if (pressed && overEdge >= 0)
        {
            ChosenPoint = _grabbed = overEdge + 1;
            points.Insert(_grabbed, (points[overEdge] + points[(overEdge + 1) % points.Count]) / 2f);
        }
        else if (rightPressed && overPoint >= 0 && points.Count > 3)
        {
            points.RemoveAt(overPoint);
            ChosenPoint = -1;
            overPoint = -1;
        }

        for (int i = 0; i < points.Count; i++)
        {
            if (EditorApp.TryProject(Vector3.Transform(new Vector3((points[i] + points[(i + 1) % points.Count]) / 2f, 0f), toWorld), viewProj, area, out var dot))
                canvas.Rect(dot.X - 2.5f, dot.Y - 2.5f, 5f, 5f, EdgeDotColor);
            if (EditorApp.TryProject(Vector3.Transform(new Vector3(points[i], 0f), toWorld), viewProj, area, out var at))
            {
                float size = i == overPoint || i == _grabbed ? 11f : 9f;
                canvas.Rect(at.X - size / 2f, at.Y - size / 2f, size, size, i == ChosenPoint ? ChosenColor : PointColor);
            }
        }
    }

    private static bool Near(Vector2 point, float2 mouse, Matrix4x4 toWorld, Matrix4x4 viewProj, float4 area) =>
        EditorApp.TryProject(Vector3.Transform(new Vector3(point, 0f), toWorld), viewProj, area, out var at) &&
        MathF.Abs(at.X - mouse.X) <= Reach && MathF.Abs(at.Y - mouse.Y) <= Reach;

    // The point on the collider's plane under the mouse, in its own space.
    private static Vector2? PointUnder(float2 mouse, Matrix4x4 toWorld, Matrix4x4 viewProj, float4 area)
    {
        if (!Matrix4x4.Invert(viewProj, out var fromClip) || !Matrix4x4.Invert(toWorld, out var toLocal)) return null;
        float x = (mouse.X - area.X) / area.Z * 2f - 1f;
        float y = (mouse.Y - area.Y) / area.W * 2f - 1f;
        var nearH = Vector4.Transform(new Vector4(x, y, 0f, 1f), fromClip);
        var farH = Vector4.Transform(new Vector4(x, y, 1f, 1f), fromClip);
        var near = Vector3.Transform(new Vector3(nearH.X, nearH.Y, nearH.Z) / nearH.W, toLocal);
        var far = Vector3.Transform(new Vector3(farH.X, farH.Y, farH.Z) / farH.W, toLocal);
        var along = far - near;
        if (MathF.Abs(along.Z) < 1e-9f) return null;   // looking along the plane
        var hit = near + along * (-near.Z / along.Z);
        return new Vector2(hit.X, hit.Y);
    }
}
