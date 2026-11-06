using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Editor.Ui;
using Engine.Core.Animation;
using Engine.Core.Assets;
using Engine.Core.Scene;

namespace Editor.Modeling;

public enum SelectMode { Vertex, Edge, Face }

// The Modeling section. Select mode picks an object with a model; Edit mode shows that model on its own, with its vertices, edges
// and faces, and the tools to reshape it. Saving writes the model file, and the scene editor reloads a changed file by itself. A
// .glb whose shape only moved is changed in place, keeping everything else in it. One with new or removed geometry is written
// anew. Anything else (a built-in shape, .gltf, .obj, .fbx) becomes a new .glb that the object then uses.
public sealed class ModelingView(string contentRoot, AssetCache assets)
{
    private const int MaxDrawnPoints = 60_000;   // past this many, drawing every vertex would slow the editor more than it helps
    private const int MaxDrawnEdges = 40_000;
    private const float PanelWidth = 300f;
    private const float HalfButton = 130f;
    private const float ThirdButton = 84f;

    public const string PreviewKey = "modeling:preview";

    private enum Tool { None, Grab, Rotate, Scale }

    private static readonly float4 PointColor = new(0.1f, 0.9f, 1f, 1f);
    private static readonly float4 SelectedColor = new(1f, 0.6f, 0.1f, 1f);
    private static readonly float4 EdgeColor = new(0.1f, 0.9f, 1f, 0.35f);
    private static readonly float4 FaceColor = new(1f, 0.6f, 0.1f, 0.25f);
    private static readonly float4 BoxColor = new(1f, 1f, 1f, 0.8f);
    private static readonly float4[] AxisColors = [new(0.95f, 0.25f, 0.25f, 1f), new(0.35f, 0.9f, 0.3f, 1f), new(0.3f, 0.5f, 1f, 1f)];

    private readonly TransformGizmo _gizmo = new() { Tool = GizmoTool.Move };
    private readonly SceneObject _pivot = new();   // what the gizmo moves: it stands for the selected vertices
    private ModelEdit? _edit;
    private MeshRenderer? _renderer;
    private DateTime _fileTime;
    private int _uploadedVersion = -1;
    private bool _showingModifiers;
    private long _nextEvaluation;
    private readonly Dictionary<string, ModelEdit> _booleanMeshes = new();
    private SelectMode _selectMode;
    private bool _addToSelection;
    private bool _xray;
    private bool _proportional;
    private float _proportionalRadius = 1f;
    private float _insetAmount = 0.2f;
    private bool _typing;       // a change is being made in the Position field
    private string _status = "";
    private Action? _afterPrompt;
    private bool _prompting;

    // The gizmo's drag, or a G, R or S move: where the points started, and how much each follows (1 for the selection, less around it
    // with proportional editing).
    private bool _gizmoDragging;
    private (int Point, Vector3 World, float Weight)[] _start = [];
    private Vector3 _startCenter;
    private Tool _tool;
    private int _axis = -1;
    private Vector3? _customAxis;
    private float2 _toolStartMouse;
    private string _typedValue = "";
    private bool _undoIfCancelled;
    private bool _leftSeenUp;
    private bool _leftWasDown;
    private bool _rightWasDown;

    // A box being dragged out over the view.
    private float2? _boxStart;
    private float2 _boxEnd;

    // What the last Update saw, for the panel's buttons and the menu, which run outside it.
    private Matrix4x4 _viewProj = Matrix4x4.Identity;
    private Vector3 _eye;
    private float4 _area;
    private float2 _mouse;
    private bool[] _facing = [];
    private Scene? _scene;

    public SceneObject? Target { get; private set; }
    public MeshRenderer? Renderer => _renderer;
    public ModelPose? Pose => _showingModifiers ? null : _edit?.Pose;   // modified, the model is drawn as it was sent
    public bool Editing => _edit is not null;
    public bool Dirty => _edit?.Dirty ?? false;
    public bool Moving => _gizmoDragging || _tool != Tool.None;
    public bool IsDragging => Moving || _boxStart is not null;
    public bool Transforming => _tool != Tool.None;

    public Action<Vector3, float>? Opened;       // an edit began on a model at this world position and of this size
    public Action? Closed;
    public Action? Saved;
    public Action<string>? AddObject;            // adds a built-in shape to the scene as a new object
    public Action<float?, float?>? SetView;      // turns the camera to this yaw and pitch, or stays turned as it is, and frames the model
    public Action<Vector3>? OrbitAround;

    // Starts editing this object's model. Says why in the panel if it can't be.
    public void Open(SceneObject obj)
    {
        var renderer = obj.Components.OfType<MeshRenderer>().FirstOrDefault(r => r.Model.Length > 0);
        if (renderer is null)
        {
            _status = "This object has no model to edit.";
            return;
        }
        try
        {
            var model = assets.GetModel(renderer.Model);
            string? problem = ModelEdit.Problem(model);
            if (problem is null && !InPlace(renderer.Model) && model.Animations.Count > 0)
                problem = "Only .glb models with animations can be edited, because the animations would be lost in a new file.";
            if (problem is not null)
            {
                _status = problem;
                return;
            }

            var edit = new ModelEdit(model);
            if (InPlace(renderer.Model))
            {
                string file = Path.Combine(contentRoot, renderer.Model);
                GlbWriter.UpdateVertices(File.ReadAllBytes(file), model.Meshes, edit.Positions, edit.Normals);   // finds out now if it can't be saved
                _fileTime = File.GetLastWriteTimeUtc(file);
            }

            _edit = edit;
            _renderer = renderer;
            Target = obj;
            _uploadedVersion = -1;
            _status = edit.CanChangeShape ? "" : "This model has animations, so its vertices can be moved, but not added or removed.";
            var world = obj.WorldMatrix;
            var center = Vector3.Transform((model.BoundsMin + model.BoundsMax) * 0.5f, world);
            float size = Vector3.TransformNormal(model.BoundsMax - model.BoundsMin, world).Length();
            _proportionalRadius = MathF.Max(size * 0.25f, 0.01f);
            Opened?.Invoke(center, size);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _status = $"This model can't be edited: {ex.Message}";
        }
    }

    // What the camera should look at: the selection, or the whole model, in the world, and how big it is.
    public (Vector3 Center, float Size) Framing()
    {
        if (_edit is not { } edit || Target is not { } target) return (Vector3.Zero, 1f);
        var world = target.WorldMatrix;
        var points = edit.Selection.Count > 0 ? edit.Selection.ToList() : Enumerable.Range(0, edit.PointCount).ToList();
        var center = Vector3.Transform(edit.Center(points), world);
        float size = points.Count == 0 ? 1f : points.Max(p => Vector3.Distance(Vector3.Transform(edit.Point(p), world), center)) * 2f;
        return (center, size);
    }

    // The model to send to the renderer, when its shape has changed since the last time this was asked. Goes under PreviewKey.
    public GlbModel? TakeChangedModel()
    {
        if (_edit is not { } edit) return null;
        if (edit.HasModifiers)
        {
            // Slow modifiers are worked out a few times a second during a drag, and once more when it ends.
            long now = Environment.TickCount64;
            if (!edit.ModifiersStale && _showingModifiers) return null;
            if (Moving && now < _nextEvaluation) return null;
            var model = edit.Evaluate();
            long took = Environment.TickCount64 - now;
            _nextEvaluation = Environment.TickCount64 + (took > 30 ? took * 3 : 0);
            _showingModifiers = true;
            _uploadedVersion = edit.Version;
            return model;
        }
        if (edit.Version == _uploadedVersion && !_showingModifiers) return null;
        _showingModifiers = false;
        _uploadedVersion = edit.Version;
        return edit.Model;
    }

    // Ends the edit, first asking what to do with unsaved changes. then runs once it has ended, and not if the user cancels.
    public void Close(Action? then = null)
    {
        CancelTool();
        if (Dirty)
        {
            _prompting = true;
            _afterPrompt = then;
            return;
        }
        EndEdit();
        then?.Invoke();
    }

    // Stops a G, R or S move and puts everything back. Returns false if none was going.
    public bool CancelTool()
    {
        if (_tool == Tool.None) return false;
        _tool = Tool.None;
        _edit?.CancelChange();
        if (_undoIfCancelled) _edit?.Undo();
        return true;
    }

    public void Undo() => _edit?.Undo();

    public void Redo() => _edit?.Redo();

    // Writes the edit to the model's file. Returns false, with the reason in the panel, if it couldn't be written.
    public bool Save()
    {
        if (_edit is not { } edit || _renderer is not { } renderer) return true;
        if (edit.HasModifiers)
            edit.ApplyModifiers();
        if (edit.Model.Meshes.Count == 0)
        {
            _status = "There is nothing left in this model to save.";
            return false;
        }
        try
        {
            bool inPlace = InPlace(renderer.Model);
            if (inPlace)
            {
                string file = Path.Combine(contentRoot, renderer.Model);
                if (File.GetLastWriteTimeUtc(file) != _fileTime)
                {
                    _status = $"{renderer.Model} was changed by another program, so it wasn't saved over. Press Select, discard this edit, and open the model again.";
                    return false;
                }
                if (edit.NeedsNewFile)
                {
                    WriteFile(file, GlbWriter.Write(edit.Model, edit.Positions, edit.Normals));
                    edit.Rebind(GlbLoader.Load(file));
                }
                else
                    WriteFile(file, GlbWriter.UpdateVertices(File.ReadAllBytes(file), edit.Model.Meshes, edit.Positions, edit.Normals));
                _fileTime = File.GetLastWriteTimeUtc(file);
                _status = $"Saved {renderer.Model}.";
            }
            else
            {
                string saved = NewFileFor(renderer.Model);
                string file = Path.Combine(contentRoot, saved);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                WriteFile(file, GlbWriter.Write(edit.Model, edit.Positions, edit.Normals));
                edit.Rebind(GlbLoader.Load(file));
                _fileTime = File.GetLastWriteTimeUtc(file);
                renderer.Model = saved.Replace('\\', '/');
                _status = $"Saved as {renderer.Model}, and this object now uses it. Save the scene to keep that.";
            }
            edit.MarkSaved();
            Saved?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _status = $"Could not save: {ex.Message}";
            return false;
        }
    }

    // The panel on the left, and the menus. Returns true when the user asks to go back to the scene.
    public bool DrawPanel(Gui g, Scene scene, SceneObject? selected, Action<SceneObject> select)
    {
        _scene = scene;
        bool leave = false;
        float top = Gui.MenuBarHeight;
        g.BeginWindow("Modeling", new Box(0f, top, PanelWidth, g.Size.Y - top), "Modeling", WindowFlags.NoScroll);

        if (g.Button("Select", HalfButton, 26f, highlighted: !Editing) && Editing)
            Close();
        g.SameLine();
        bool canEdit = selected?.Components.OfType<MeshRenderer>().Any(r => r.Model.Length > 0) ?? false;
        if (g.Button("Edit", HalfButton, 26f, enabled: Editing || canEdit, highlighted: Editing) && !Editing)
            Open(selected!);
        g.Spacing();

        float below = Gui.RowHeight * ((_status.Length > 0 ? 6f : 2.5f) + (Editing ? 3.5f : 0f));
        g.BeginChild("modeling-panel", 0f, -below);
        if (_edit is { } edit && Target is { } target)
            DrawEditPanel(g, edit, target);
        else
            DrawSelectPanel(g, scene, selected, select);
        g.EndChild();

        // These stay in view however far the tools above are scrolled.
        if (_edit is { } current)
        {
            if (g.Button("Undo", HalfButton, 26f, enabled: current.CanUndo)) Undo();
            g.SameLine();
            if (g.Button("Redo", HalfButton, 26f, enabled: current.CanRedo)) Redo();
            if (g.Button("Save Model", -1f, 30f, enabled: current.Dirty, highlighted: current.Dirty)) Save();
        }
        if (_status.Length > 0)
            g.TextWrapped(_status, Gui.DimColor);
        g.Separator();
        if (g.Button("Scene View", -1f, 26f))
            leave = true;
        g.EndWindow();

        DrawMenus(g);
        DrawPrompt(g);
        return leave;
    }

    // A right click in the view.
    public void OpenMenu(Gui g, float2 at) => g.OpenPopup("model-menu", at, 200f);

    // Keyboard shortcuts, which act when the mouse is over the view and nothing is being typed into.
    public void HandleKeys(Gui g, SceneObject? selected, bool overView)
    {
        var input = UiInput.Current;
        bool Pressed(ImGuiKey key) => input.IsKeyPressed(key, false);
        bool command = input.KeyCtrl || input.KeySuper;

        if (_tool != Tool.None) return;
        if (g.IsTyping || _prompting || !overView) return;
        if (Pressed(ImGuiKey.Tab))
        {
            if (Editing) Close();
            else if (selected is not null) Open(selected);
            return;
        }
        if (_edit is not { } edit) return;

        if (command)
        {
            if (Pressed(ImGuiKey.I)) edit.SelectInverse();
            else if (Pressed(ImGuiKey.L)) edit.SelectLinked();
            else if (Pressed(ImGuiKey.R)) Report(LoopCutUnderMouse(edit));
            return;
        }
        if (Pressed(ImGuiKey._1)) _selectMode = SelectMode.Vertex;
        else if (Pressed(ImGuiKey._2)) _selectMode = SelectMode.Edge;
        else if (Pressed(ImGuiKey._3)) _selectMode = SelectMode.Face;
        else if (Pressed(ImGuiKey.G)) StartTool(Tool.Grab);
        else if (Pressed(ImGuiKey.R)) StartTool(Tool.Rotate);
        else if (Pressed(ImGuiKey.S)) StartTool(Tool.Scale);
        else if (Pressed(ImGuiKey.L)) SelectPieceUnderMouse(edit, remove: input.KeyShift);
        else if (Pressed(ImGuiKey.A)) { if (input.KeyAlt) edit.SelectNone(); else edit.SelectAll(); }
        else if (Pressed(ImGuiKey.Z) && input.KeyAlt) _xray = !_xray;
        else if (Pressed(ImGuiKey.O)) _proportional = !_proportional;
        else if (Pressed(ImGuiKey.E)) Extrude();
        else if (Pressed(ImGuiKey.I)) Shape(e => e.Inset(_insetAmount));
        else if (Pressed(ImGuiKey.X) || Pressed(ImGuiKey.Delete)) g.OpenPopup("model-delete", _mouse, 160f);
        else if (Pressed(ImGuiKey.M)) Shape(e => e.Merge());
        else if (Pressed(ImGuiKey.F)) Shape(e => e.Fill());
        else if (Pressed(ImGuiKey.D) && input.KeyShift) Duplicate();
        else if (Pressed(ImGuiKey.N) && input.KeyShift) Shape(e => { e.RecalculateNormals(); return null; });
        else if (Pressed(ImGuiKey.Keypad1)) SetView?.Invoke(0f, 0f);
        else if (Pressed(ImGuiKey.Keypad3)) SetView?.Invoke(-MathF.PI / 2f, 0f);
        else if (Pressed(ImGuiKey.Keypad7)) SetView?.Invoke(0f, -1.5f);
        else if (Pressed(ImGuiKey.KeypadDecimal)) SetView?.Invoke(null, null);
    }

    // Draws the model's mesh over the view and runs whichever move is going. area is the scene in window pixels.
    public void Update(IAppWindow window, Canvas2D canvas, Matrix4x4 viewProj, Vector3 eye, float4 area, bool uiWantsMouse, float moveSnap, float scaleSnap)
    {
        var cursor = window.CursorPos;
        _mouse = new float2((float)cursor.X, (float)cursor.Y);
        _viewProj = viewProj;
        _eye = eye;
        _area = area;
        if (_edit is not { } edit || Target is not { } target) return;

        var world = target.WorldMatrix;
        if (!Matrix4x4.Invert(world, out var toModel)) return;
        FindFacing(edit, Vector3.Transform(eye, toModel));
        OrbitAround?.Invoke(Vector3.Transform(edit.Selection.Count > 0 ? edit.Center(edit.Selection) : edit.Center(Enumerable.Range(0, edit.PointCount)), world));
        DrawOverlay(canvas, edit, world, viewProj, area);

        if (_tool != Tool.None)
        {
            UpdateTool(window, canvas, edit, toModel);
            _gizmo.Update(window, canvas, null, [], viewProj, eye, area, uiWantsMouse: true);
            return;
        }

        _gizmo.MoveSnap = moveSnap;
        _gizmo.ScaleSnap = scaleSnap;
        if (edit.Selection.Count == 0)
        {
            _gizmo.Update(window, canvas, null, [], viewProj, eye, area, uiWantsMouse);
            return;
        }

        if (!_gizmoDragging)
        {
            _pivot.Position = Vector3.Transform(edit.Center(edit.Selection), world);
            _pivot.Scale = Vector3.One;
        }
        var before = _pivot.Position;
        _gizmo.Update(window, canvas, _pivot, [], viewProj, eye, area, uiWantsMouse);

        if (_gizmo.IsDragging && !_gizmoDragging)
        {
            _gizmoDragging = true;
            Capture(edit, world, before);
        }
        if (_gizmoDragging)
        {
            foreach (var (point, start, weight) in _start)
                edit.SetPoint(point, Vector3.Transform(Vector3.Lerp(start, _pivot.Position + (start - _startCenter) * _pivot.Scale, weight), toModel));
            edit.UpdateNormals(_start.Select(s => s.Point).ToList());
            if (!_gizmo.IsDragging)
            {
                _gizmoDragging = false;
                edit.EndChange();
            }
        }
    }

    // A click in the view: selects what's under the mouse, the thing nearest the camera if several are. add keeps the rest selected
    // and toggles this. loop takes the whole line of edges through it. A click on nothing clears the selection.
    public void Click(float2 mouse, bool add, bool loop, Matrix4x4 viewProj, Vector3 eye, float4 area)
    {
        if (_edit is not { } edit || Target is not { } target) return;
        add |= _addToSelection;
        var world = target.WorldMatrix;

        if (loop && NearestEdge(edit, world, mouse) is { } edge)
        {
            if (!add) edit.SelectNone();
            edit.Selection.UnionWith(edit.EdgeLoop(edge.A, edge.B));
            return;
        }

        int[]? hit = _selectMode switch
        {
            SelectMode.Edge => NearestEdge(edit, world, mouse) is { } nearest ? [nearest.A, nearest.B] : null,
            SelectMode.Face => FaceAt(edit, world, mouse) is { } face ? face.Corners.Select(c => c.Point).ToArray() : null,
            _ => NearestPoint(edit, world, mouse) is { } point ? [point] : null,
        };
        if (hit is null)
        {
            if (!add) edit.SelectNone();
            return;
        }
        if (!add) edit.SelectNone();
        if (add && hit.All(edit.Selection.Contains)) edit.Selection.ExceptWith(hit);
        else edit.Selection.UnionWith(hit);
    }

    // A left drag from empty space draws a box, which selects what's inside it when it ends.
    public void BoxDrag(float2 start, float2 end)
    {
        _boxStart = start;
        _boxEnd = end;
    }

    public void BoxEnd(bool add)
    {
        if (_boxStart is not { } start || _edit is not { } edit || Target is not { } target)
        {
            _boxStart = null;
            return;
        }
        _boxStart = null;
        add |= _addToSelection;
        float left = MathF.Min(start.X, _boxEnd.X), right = MathF.Max(start.X, _boxEnd.X);
        float top = MathF.Min(start.Y, _boxEnd.Y), bottom = MathF.Max(start.Y, _boxEnd.Y);
        if (!add) edit.SelectNone();
        var world = target.WorldMatrix;
        for (int i = 0; i < edit.PointCount; i++)
            if (Shown(edit, i) && Project(Vector3.Transform(edit.Point(i), world), _viewProj, _area, out var at) &&
                at.X >= left && at.X <= right && at.Y >= top && at.Y <= bottom)
                edit.Selection.Add(i);
    }

    // ---- the panel -----------------------------------------------------------------------------

    private void DrawSelectPanel(Gui g, Scene scene, SceneObject? selected, Action<SceneObject> select)
    {
        g.TextWrapped("Pick an object with a model, here or in the view, then press Edit. Right-click the view to add a shape.", Gui.DimColor);
        g.Spacing();
        int id = 0;
        foreach (var obj in scene.InHierarchyOrder.Where(o => o.Components.OfType<MeshRenderer>().Any(r => r.Model.Length > 0)))
            if (g.Selectable($"{SafeLabel.Of(obj.Name, 40)}##{id++}", ReferenceEquals(obj, selected)))
                select(obj);
    }

    private void DrawEditPanel(Gui g, ModelEdit edit, SceneObject target)
    {
        g.Text(SafeLabel.Of(target.Name, 36));
        g.TextDisabled(SafeLabel.Of(Primitives.Label(_renderer!.Model), 40));
        g.TextDisabled($"{edit.PointCount:N0} vertices, {edit.Edges.Count:N0} edges, {edit.Polys.Count:N0} faces");
        g.TextDisabled($"{edit.Selection.Count:N0} vertices selected");
        if (edit.PointCount > MaxDrawnPoints)
            g.TextWrapped("This model has too many vertices to show them all. Selected ones are still shown.", Gui.DimColor);
        g.Spacing();

        if (g.Button("Vertex", ThirdButton, 26f, highlighted: _selectMode == SelectMode.Vertex)) _selectMode = SelectMode.Vertex;
        g.SameLine();
        if (g.Button("Edge", ThirdButton, 26f, highlighted: _selectMode == SelectMode.Edge)) _selectMode = SelectMode.Edge;
        g.SameLine();
        if (g.Button("Face", ThirdButton, 26f, highlighted: _selectMode == SelectMode.Face)) _selectMode = SelectMode.Face;

        if (g.CollapsingHeader("Selection", defaultOpen: true))
        {
            g.Checkbox("Add Selection", ref _addToSelection);
            g.Checkbox("X-Ray", ref _xray);
            if (g.Button("All", ThirdButton, 26f)) edit.SelectAll();
            g.SameLine();
            if (g.Button("None", ThirdButton, 26f)) edit.SelectNone();
            g.SameLine();
            if (g.Button("Invert", ThirdButton, 26f)) edit.SelectInverse();
            if (g.Button("Linked", ThirdButton, 26f)) edit.SelectLinked();
            g.SameLine();
            if (g.Button("More", ThirdButton, 26f)) edit.SelectMore();
            g.SameLine();
            if (g.Button("Less", ThirdButton, 26f)) edit.SelectLess();
        }

        if (g.CollapsingHeader("Transform", defaultOpen: true))
        {
            if (g.Button("Move", ThirdButton, 26f, highlighted: _gizmo.Tool == GizmoTool.Move)) _gizmo.Tool = GizmoTool.Move;
            g.SameLine();
            if (g.Button("Scale", ThirdButton, 26f, highlighted: _gizmo.Tool == GizmoTool.Scale)) _gizmo.Tool = GizmoTool.Scale;
            g.SameLine();
            if (g.Button("Rotate", ThirdButton, 26f)) StartTool(Tool.Rotate);
            DrawPositionField(g, edit, target);
            g.Checkbox("Proportional", ref _proportional);
            if (_proportional)
                g.DragFloat("Radius", ref _proportionalRadius, 0.01f, 0.001f, 1000f);
        }

        if (edit.CanChangeShape && g.CollapsingHeader("Mesh Tools", defaultOpen: true))
        {
            if (g.Button("Extrude", HalfButton, 26f)) Extrude();
            g.SameLine();
            if (g.Button("Inset", HalfButton, 26f)) Shape(e => e.Inset(_insetAmount));
            g.DragFloat("Inset Amount", ref _insetAmount, 0.005f, 0.01f, 0.95f);
            if (g.Button("Subdivide", HalfButton, 26f)) Shape(e => e.Subdivide());
            g.SameLine();
            if (g.Button("Loop Cut", HalfButton, 26f)) Report(LoopCutSelected(edit));
            if (g.Button("Fill", HalfButton, 26f)) Shape(e => e.Fill());
            g.SameLine();
            if (g.Button("Merge", HalfButton, 26f)) Shape(e => e.Merge());
            if (g.Button("Duplicate", HalfButton, 26f)) Duplicate();
            g.SameLine();
            if (g.Button("Join Doubles", HalfButton, 26f)) _status = $"Removed {edit.RemoveDoubles(0.0001f):N0} vertices.";
            if (g.Button("Mirror X", ThirdButton, 26f)) Shape(e => e.Mirror(0));
            g.SameLine();
            if (g.Button("Mirror Y", ThirdButton, 26f)) Shape(e => e.Mirror(1));
            g.SameLine();
            if (g.Button("Mirror Z", ThirdButton, 26f)) Shape(e => e.Mirror(2));
            if (g.Button("Delete...", -1f, 26f)) g.OpenPopup("model-delete", g.Mouse, 160f);
        }

        if (edit.CanChangeShape && g.CollapsingHeader("Modifiers", defaultOpen: true))
            DrawModifiers(g, edit);

        if (edit.CanChangeShape && g.CollapsingHeader("Normals"))
        {
            if (g.Button("Shade Smooth", HalfButton, 26f)) Shape(e => e.ShadeSmooth());
            g.SameLine();
            if (g.Button("Shade Flat", HalfButton, 26f)) Shape(e => e.ShadeFlat());
            if (g.Button("Flip Normals", HalfButton, 26f)) Shape(e => e.FlipNormals());
            g.SameLine();
            if (g.Button("Fix Normals", HalfButton, 26f)) Shape(e => { e.RecalculateNormals(); return null; });
        }

        if (g.CollapsingHeader("Reshape"))
        {
            if (g.Button("Smooth", HalfButton, 26f)) edit.SmoothVertices(0.5f);
            g.SameLine();
            if (g.Button("Randomize", HalfButton, 26f)) edit.Randomize(_proportionalRadius * 0.1f, Environment.TickCount);
            if (g.Button("To Sphere", HalfButton, 26f)) edit.ToSphere();
            g.SameLine();
            if (g.Button("Flatten", HalfButton, 26f)) edit.Flatten();
        }
    }

    private void DrawModifiers(Gui g, ModelEdit edit)
    {
        if (g.Button("Add Modifier...", -1f, 26f)) g.OpenPopup("model-modifiers", g.Mouse, 200f);
        int id = 0;
        foreach (var modifier in edit.Modifiers.ToList())
        {
            string tag = "##modifier" + id++;
            g.Text(modifier.Name);
            bool on = modifier.Enabled;
            if (g.Checkbox("On" + tag, ref on))
            {
                modifier.Enabled = on;
                edit.ModifiersChanged();
            }
            if (DrawModifierSettings(g, modifier, tag))
                edit.ModifiersChanged();
            if (modifier.Problem is { } problem)
                g.TextWrapped(problem, Gui.ErrorColor);
            if (g.Button("Apply" + tag, 84f, 24f)) { edit.ApplyModifier(modifier); Report(null); }
            g.SameLine();
            if (g.Button("Up" + tag, 56f, 24f)) edit.MoveModifier(modifier, -1);
            g.SameLine();
            if (g.Button("Down" + tag, 64f, 24f)) edit.MoveModifier(modifier, 1);
            g.SameLine();
            if (g.Button("X" + tag, 34f, 24f)) edit.RemoveModifier(modifier);
            g.Separator();
        }
    }

    // Returns true when a setting changed.
    private bool DrawModifierSettings(Gui g, ModelModifier modifier, string tag)
    {
        bool changed = false;
        void Int(string label, int value, int min, int max, Action<int> set)
        {
            if (g.DragInt(label + tag, ref value, 0.1f, min, max)) { set(value); changed = true; }
        }
        void Float(string label, float value, float speed, float min, float max, Action<float> set)
        {
            if (g.DragFloat(label + tag, ref value, speed, min, max)) { set(value); changed = true; }
        }
        void Toggle(string label, bool value, Action<bool> set)
        {
            if (g.Checkbox(label + tag, ref value)) { set(value); changed = true; }
        }

        switch (modifier)
        {
            case SubdivisionModifier m:
                Int("Levels", m.Levels, 1, 5, v => m.Levels = v);
                break;
            case ArrayModifier m:
                Int("Count", m.Count, 1, 1000, v => m.Count = v);
                var offset = new float3(m.Offset.X, m.Offset.Y, m.Offset.Z);
                if (g.DragFloat3("Offset" + tag, ref offset, 0.01f))
                {
                    m.Offset = new Vector3(offset.X, offset.Y, offset.Z);
                    changed = true;
                }
                break;
            case MirrorModifier m:
                Toggle("X", m.X, v => m.X = v);
                Toggle("Y", m.Y, v => m.Y = v);
                Toggle("Z", m.Z, v => m.Z = v);
                break;
            case SolidifyModifier m:
                Float("Thickness", m.Thickness, 0.002f, -100f, 100f, v => m.Thickness = v);
                Float("Offset", m.Offset, 0.01f, -1f, 1f, v => m.Offset = v);
                break;
            case DecimateModifier m:
                Float("Ratio", m.Ratio, 0.005f, 0.01f, 1f, v => m.Ratio = v);
                break;
            case SkinModifier m:
                Float("Radius", m.Radius, 0.002f, 0.001f, 100f, v => m.Radius = v);
                Int("Sides", m.Sides, 3, 32, v => m.Sides = v);
                break;
            case ScrewModifier m:
                if (g.Combo("Axis" + tag, m.Axis, ["X", "Y", "Z"], out int axis)) { m.Axis = axis; changed = true; }
                Float("Angle", m.Angle, 1f, -3600f, 3600f, v => m.Angle = v);
                Int("Steps", m.Steps, 1, 512, v => m.Steps = v);
                Float("Height", m.Height, 0.01f, -1000f, 1000f, v => m.Height = v);
                break;
            case BooleanModifier m:
                changed |= DrawBooleanSettings(g, m, tag);
                break;
        }
        return changed;
    }

    private bool DrawBooleanSettings(Gui g, BooleanModifier modifier, string tag)
    {
        bool changed = false;
        var operation = modifier.Operation;
        if (g.Combo("Operation" + tag, ref operation))
        {
            modifier.Operation = operation;
            changed = true;
        }

        var objects = _scene?.InHierarchyOrder.Where(o => !ReferenceEquals(o, Target) && o.Components.OfType<MeshRenderer>().Any(r => r.Model.Length > 0)).ToList() ?? [];
        int current = objects.FindIndex(o => o.Id == modifier.Target) + 1;
        var names = objects.Select(o => SafeLabel.Of(o.Name, 24)).Prepend("Choose...").ToList();
        if (g.Combo("Object" + tag, current, names, out int picked) && picked != current)
        {
            modifier.Target = picked > 0 ? objects[picked - 1].Id : null;
            modifier.Other = picked > 0 ? MeshOf(objects[picked - 1]) : null;
            changed = true;
        }
        return changed;
    }

    // Another object's mesh, placed where it is relative to the object being edited.
    private MeshWork? MeshOf(SceneObject other)
    {
        if (Target is not { } target || !Matrix4x4.Invert(target.WorldMatrix, out var toModel)) return null;
        var renderer = other.Components.OfType<MeshRenderer>().First(r => r.Model.Length > 0);
        try
        {
            if (!_booleanMeshes.TryGetValue(renderer.Model, out var source))
                _booleanMeshes[renderer.Model] = source = new ModelEdit(assets.GetModel(renderer.Model));
            var place = other.WorldMatrix * toModel;
            var points = Enumerable.Range(0, source.PointCount).Select(i => Vector3.Transform(source.Point(i), place)).ToList();
            var polys = source.Polys.Select(p => new Poly((Corner[])p.Corners.Clone(), 0)).ToList();
            return new MeshWork(points, polys);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or System.Text.Json.JsonException)
        {
            Report($"Could not read {renderer.Model}: {ex.Message}");
            return null;
        }
    }

    // Where the selected vertices are, in the world. Changing it moves them together.
    private void DrawPositionField(Gui g, ModelEdit edit, SceneObject target)
    {
        if (edit.Selection.Count == 0 || !Matrix4x4.Invert(target.WorldMatrix, out var toModel))
        {
            FinishTyping(g, edit);
            return;
        }
        var world = target.WorldMatrix;
        var center = Vector3.Transform(edit.Center(edit.Selection), world);
        var value = new float3(center.X, center.Y, center.Z);
        if (g.DragFloat3("Position", ref value, 0.01f))
        {
            if (!_typing)
            {
                _typing = true;
                edit.BeginChange(edit.Selection);
            }
            var offset = new Vector3(value.X, value.Y, value.Z) - center;
            foreach (int point in edit.Selection)
                edit.SetPoint(point, Vector3.Transform(Vector3.Transform(edit.Point(point), world) + offset, toModel));
            edit.UpdateNormals(edit.Selection);
        }
        FinishTyping(g, edit);
    }

    private void FinishTyping(Gui g, ModelEdit edit)
    {
        if (!_typing || g.AnyActive) return;
        _typing = false;
        edit.EndChange();
    }

    private void DrawMenus(Gui g)
    {
        if (g.BeginPopup("model-menu"))
        {
            if (_edit is { } edit)
            {
                if (edit.CanChangeShape && g.MenuItem("Add...")) g.OpenPopup("model-add", g.Mouse, 160f);
                if (g.MenuItem("Grab", "G")) StartTool(Tool.Grab);
                if (g.MenuItem("Rotate", "R")) StartTool(Tool.Rotate);
                if (g.MenuItem("Scale", "S")) StartTool(Tool.Scale);
                if (edit.CanChangeShape)
                {
                    if (g.MenuItem("Extrude", "E")) Extrude();
                    if (g.MenuItem("Inset", "I")) Shape(e => e.Inset(_insetAmount));
                    if (g.MenuItem("Subdivide")) Shape(e => e.Subdivide());
                    if (g.MenuItem("Merge", "M")) Shape(e => e.Merge());
                    if (g.MenuItem("Fill", "F")) Shape(e => e.Fill());
                    if (g.MenuItem("Duplicate", "Shift+D")) Duplicate();
                    if (g.MenuItem("Delete...", "X")) g.OpenPopup("model-delete", g.Mouse, 160f);
                }
                if (g.MenuItem("Frame Selected")) SetView?.Invoke(null, null);
                if (g.MenuItem("Front View")) SetView?.Invoke(0f, 0f);
                if (g.MenuItem("Right View")) SetView?.Invoke(-MathF.PI / 2f, 0f);
                if (g.MenuItem("Top View")) SetView?.Invoke(0f, -1.5f);
            }
            else
                foreach (string shape in Primitives.All)
                    if (g.MenuItem("Add " + Primitives.Label(shape))) AddObject?.Invoke(shape);
            g.EndPopup();
        }

        if (g.BeginPopup("model-add"))
        {
            foreach (var kind in Enum.GetValues<ModelPrimitive>())
                if (g.MenuItem(kind.ToString())) AddPrimitive(kind);
            g.EndPopup();
        }

        if (g.BeginPopup("model-modifiers"))
        {
            if (_edit is { } edit)
            {
                if (g.MenuItem("Subdivision Surface")) edit.AddModifier(new SubdivisionModifier());
                if (g.MenuItem("Array")) edit.AddModifier(new ArrayModifier());
                if (g.MenuItem("Boolean")) edit.AddModifier(new BooleanModifier());
                if (g.MenuItem("Decimate")) edit.AddModifier(new DecimateModifier());
                if (g.MenuItem("Mirror")) edit.AddModifier(new MirrorModifier());
                if (g.MenuItem("Solidify")) edit.AddModifier(new SolidifyModifier());
                if (g.MenuItem("Skin")) edit.AddModifier(new SkinModifier());
                if (g.MenuItem("Screw")) edit.AddModifier(new ScrewModifier());
            }
            g.EndPopup();
        }

        if (g.BeginPopup("model-delete"))
        {
            if (g.MenuItem("Vertices")) Shape(e => e.Delete(DeleteKind.Vertices));
            if (g.MenuItem("Edges")) Shape(e => e.Delete(DeleteKind.Edges));
            if (g.MenuItem("Faces")) Shape(e => e.Delete(DeleteKind.Faces));
            g.EndPopup();
        }
    }

    private void DrawPrompt(Gui g)
    {
        if (!_prompting) return;
        var rect = new Box(g.Size.X * 0.5f - 200f, g.Size.Y * 0.5f - 70f, 400f, 120f);
        g.BeginWindow("Unsaved Model", rect, "Unsaved Changes", WindowFlags.Modal | WindowFlags.NoScroll);
        g.TextWrapped($"Save your changes to {SafeLabel.Of(Primitives.Label(_renderer?.Model ?? ""), 40)}?");
        g.Spacing();
        if (g.Button("Save", highlighted: true))
        {
            _prompting = false;
            if (Save()) FinishPrompt();
        }
        g.SameLine();
        if (g.Button("Don't Save"))
        {
            _prompting = false;
            FinishPrompt();
        }
        g.SameLine();
        if (g.Button("Cancel"))
        {
            _prompting = false;
            _afterPrompt = null;
        }
        g.EndWindow();
    }

    private void FinishPrompt()
    {
        var then = _afterPrompt;
        _afterPrompt = null;
        EndEdit();
        then?.Invoke();
    }

    private void EndEdit()
    {
        _edit = null;
        _renderer = null;
        Target = null;
        _showingModifiers = false;
        _booleanMeshes.Clear();
        _gizmoDragging = false;
        _typing = false;
        _boxStart = null;
        _status = "";
        Closed?.Invoke();
    }

    // ---- operations ----------------------------------------------------------------------------

    private void Report(string? problem) => _status = problem ?? "";

    private void Shape(Func<ModelEdit, string?> operation)
    {
        if (_edit is { } edit)
            Report(edit.CanChangeShape ? operation(edit) : "This model has animations, so vertices can't be added or removed.");
    }

    private void Extrude()
    {
        if (_edit is not { } edit) return;
        if (!edit.CanChangeShape)
        {
            Report("This model has animations, so vertices can't be added or removed.");
            return;
        }
        if (edit.Extrude() is not { } direction)
        {
            Report("Select a face or an edge to extrude.");
            return;
        }
        Report(null);
        var along = Target is { } target ? Vector3.TransformNormal(direction, target.WorldMatrix) : direction;
        StartTool(Tool.Grab, along, undoIfCancelled: true);
    }

    private void Duplicate()
    {
        if (_edit is not { } edit) return;
        string? problem = edit.CanChangeShape ? edit.Duplicate() : "This model has animations, so vertices can't be added or removed.";
        Report(problem);
        if (problem is null)
            StartTool(Tool.Grab, undoIfCancelled: true);
    }

    private void AddPrimitive(ModelPrimitive kind)
    {
        if (_edit is not { } edit) return;
        var at = edit.Selection.Count > 0 ? edit.Center(edit.Selection) : (edit.Model.BoundsMin + edit.Model.BoundsMax) * 0.5f;
        edit.AddPrimitive(kind, at);
        Report(null);
    }

    // L: the loose piece under the mouse, added to the selection. With Shift, taken out of it.
    private void SelectPieceUnderMouse(ModelEdit edit, bool remove)
    {
        if (Target is not { } target) return;
        var world = target.WorldMatrix;
        int? point = NearestPoint(edit, world, _mouse) ?? FaceAt(edit, world, _mouse)?.Corners[0].Point ?? NearestEdge(edit, world, _mouse)?.A;
        if (point is { } start) edit.SelectPiece(start, remove);
        else Report("Point at part of the model to select it.");
    }

    private string? LoopCutUnderMouse(ModelEdit edit) =>
        !edit.CanChangeShape ? "This model has animations, so vertices can't be added or removed."
        : Target is { } target && NearestEdge(edit, target.WorldMatrix, _mouse) is { } edge ? edit.LoopCut(edge.A, edge.B)
        : "Point at an edge to cut across it.";

    // The panel's button can't point at an edge, so it cuts across the one selected.
    private string? LoopCutSelected(ModelEdit edit)
    {
        foreach (var (a, b) in edit.Edges)
            if (edit.Selection.Contains(a) && edit.Selection.Contains(b))
                return edit.LoopCut(a, b);
        return "Select an edge to cut across it.";
    }

    // ---- moving --------------------------------------------------------------------------------

    private void Capture(ModelEdit edit, Matrix4x4 world, Vector3 center)
    {
        var points = new Dictionary<int, float>();
        foreach (int point in edit.Selection) points[point] = 1f;
        if (_proportional)
        {
            var selected = edit.Selection.Select(p => Vector3.Transform(edit.Point(p), world)).ToList();
            for (int i = 0; i < edit.PointCount; i++)
            {
                if (points.ContainsKey(i)) continue;
                var at = Vector3.Transform(edit.Point(i), world);
                float nearest = selected.Min(s => Vector3.Distance(s, at));
                if (nearest >= _proportionalRadius) continue;
                float t = nearest / _proportionalRadius;
                points[i] = 1f - t * t * (3f - 2f * t);
            }
        }
        _start = points.Select(p => (p.Key, Vector3.Transform(edit.Point(p.Key), world), p.Value)).ToArray();
        _startCenter = center;
        edit.BeginChange(points.Keys);
    }

    // Starts a G, R or S move. along, when given, is the only direction it can go.
    private void StartTool(Tool tool, Vector3? along = null, bool undoIfCancelled = false)
    {
        if (_edit is not { } edit || Target is not { } target) return;
        if (edit.Selection.Count == 0)
        {
            Report("Select something first.");
            return;
        }
        var world = target.WorldMatrix;
        Capture(edit, world, Vector3.Transform(edit.Center(edit.Selection), world));
        _tool = tool;
        _axis = -1;
        _customAxis = along is { } direction && direction.LengthSquared() > 1e-12f ? Vector3.Normalize(direction) : null;
        _toolStartMouse = _mouse;
        _typedValue = "";
        _undoIfCancelled = undoIfCancelled;
        _leftSeenUp = false;
        _leftWasDown = false;
        _rightWasDown = false;
        Report(null);
    }

    private void UpdateTool(IAppWindow window, Canvas2D canvas, ModelEdit edit, Matrix4x4 toModel)
    {
        var input = UiInput.Current;
        bool left = window.GetMouseButton(GlfwMouseButton.Left) == GlfwButtonAction.Press;
        bool right = window.GetMouseButton(GlfwMouseButton.Right) == GlfwButtonAction.Press;
        bool startedUp = _leftSeenUp;   // a button still down from starting the move doesn't finish it
        if (!left) _leftSeenUp = true;

        // A finger only has a position while it's down, so on a touch screen the move starts where it lands.
        if (OperatingSystem.IsIOS() && left && !_leftWasDown)
            _toolStartMouse = _mouse;

        if (input.IsKeyPressed(ImGuiKey.X, false)) ToggleAxis(0);
        else if (input.IsKeyPressed(ImGuiKey.Y, false)) ToggleAxis(1);
        else if (input.IsKeyPressed(ImGuiKey.Z, false)) ToggleAxis(2);
        foreach (uint typed in input.InputQueueCharacters)
        {
            char c = (char)typed;
            if (char.IsAsciiDigit(c) || (c == '.' && !_typedValue.Contains('.')) || (c == '-' && _typedValue.Length == 0))
                _typedValue += c;
        }
        if (input.IsKeyPressed(ImGuiKey.Backspace, false) && _typedValue.Length > 0)
            _typedValue = _typedValue[..^1];
        if (_proportional && input.MouseWheel != 0f)
            _proportionalRadius = MathF.Max(0.001f, _proportionalRadius * (1f + input.MouseWheel * 0.1f));

        foreach (var (point, start, weight) in _start)
            edit.SetPoint(point, Vector3.Transform(Vector3.Lerp(start, Transformed(start), weight), toModel));
        edit.UpdateNormals(_start.Select(s => s.Point).ToList());

        string axis = _customAxis is not null ? " along the normal" : _axis >= 0 ? " on " + "XYZ"[_axis] : "";
        _status = $"{_tool}{axis} {_typedValue}\nClick or Enter to finish, right-click or Esc to cancel. X, Y, Z lock an axis; type a number for an exact value.";
        DrawAxis(canvas);

        bool confirm = (startedUp && _leftWasDown && !left) || input.IsKeyPressed(ImGuiKey.Enter, false);
        bool cancel = !right && _rightWasDown;   // on release, so the same click doesn't open the menu too
        _leftWasDown = left;
        _rightWasDown = right;
        if (cancel)
        {
            CancelTool();
            Report(null);
        }
        else if (confirm)
        {
            _tool = Tool.None;
            edit.EndChange();
            Report(null);
        }
    }

    private void ToggleAxis(int axis)
    {
        _customAxis = null;
        _axis = _axis == axis ? -1 : axis;
    }

    private Vector3? Axis => _customAxis ?? (_axis >= 0 ? Unit(_axis) : null);

    private static Vector3 Unit(int axis) => axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;

    // Where a point goes under the move as the mouse has it now, in the world.
    private Vector3 Transformed(Vector3 start)
    {
        bool hasNumber = float.TryParse(_typedValue, System.Globalization.CultureInfo.InvariantCulture, out float number);
        var center = _startCenter;
        switch (_tool)
        {
            case Tool.Rotate:
            {
                var axis = Axis ?? Vector3.Normalize(_eye - center);
                float angle = hasNumber ? number * MathF.PI / 180f : -ScreenAngle();
                return center + Vector3.Transform(start - center, Quaternion.CreateFromAxisAngle(axis, angle));
            }
            case Tool.Scale:
            {
                float factor = hasNumber ? number : ScreenScale();
                return Axis is { } axis
                    ? start + axis * Vector3.Dot(start - center, axis) * (factor - 1f)
                    : center + (start - center) * factor;
            }
            default:
                return start + GrabOffset(hasNumber, number);
        }
    }

    private Vector3 GrabOffset(bool hasNumber, float number)
    {
        if (Axis is { } axis)
        {
            if (hasNumber) return axis * number;
            if (AlongAxis(_mouse, axis) is { } now && AlongAxis(_toolStartMouse, axis) is { } was)
                return axis * (now - was);
            return Vector3.Zero;
        }
        var facing = Vector3.Normalize(_startCenter - _eye);
        if (OnViewPlane(_mouse, facing) is { } current && OnViewPlane(_toolStartMouse, facing) is { } first)
            return current - first;
        return Vector3.Zero;
    }

    private float ScreenAngle()
    {
        if (!Project(_startCenter, _viewProj, _area, out var center)) return 0f;
        float now = MathF.Atan2(_mouse.Y - center.Y, _mouse.X - center.X);
        float was = MathF.Atan2(_toolStartMouse.Y - center.Y, _toolStartMouse.X - center.X);
        return now - was;
    }

    private float ScreenScale()
    {
        if (!Project(_startCenter, _viewProj, _area, out var center)) return 1f;
        float was = MathF.Max(Distance(_toolStartMouse, center), 1f);
        return Distance(_mouse, center) / was;
    }

    private static float Distance(float2 a, float2 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private (Vector3 Origin, Vector3 Direction)? Ray(float2 mouse)
    {
        if (!Matrix4x4.Invert(_viewProj, out var inverse)) return null;
        float x = (mouse.X - _area.X) / _area.Z * 2f - 1f;
        float y = (mouse.Y - _area.Y) / _area.W * 2f - 1f;
        var near = Vector4.Transform(new Vector4(x, y, 0f, 1f), inverse);
        var far = Vector4.Transform(new Vector4(x, y, 1f, 1f), inverse);
        var origin = new Vector3(near.X, near.Y, near.Z) / near.W;
        var direction = new Vector3(far.X, far.Y, far.Z) / far.W - origin;
        return direction.LengthSquared() < 1e-12f ? null : (origin, Vector3.Normalize(direction));
    }

    private Vector3? OnViewPlane(float2 mouse, Vector3 normal)
    {
        if (Ray(mouse) is not { } ray) return null;
        float across = Vector3.Dot(ray.Direction, normal);
        if (MathF.Abs(across) < 1e-6f) return null;
        return ray.Origin + ray.Direction * (Vector3.Dot(_startCenter - ray.Origin, normal) / across);
    }

    // How far along the line through the move's center the mouse ray passes closest.
    private float? AlongAxis(float2 mouse, Vector3 axis)
    {
        if (Ray(mouse) is not { } ray) return null;
        var between = _startCenter - ray.Origin;
        float b = Vector3.Dot(axis, ray.Direction);
        float d = Vector3.Dot(axis, between);
        float e = Vector3.Dot(ray.Direction, between);
        float denominator = 1f - b * b;
        if (denominator < 1e-6f) return null;
        return (b * e - d) / denominator;
    }

    private void DrawAxis(Canvas2D canvas)
    {
        if (Axis is not { } axis) return;
        float reach = Vector3.Distance(_eye, _startCenter) * 2f;
        if (Project(_startCenter - axis * reach, _viewProj, _area, out var a) && Project(_startCenter + axis * reach, _viewProj, _area, out var b))
            canvas.Line(a, b, 1.5f, _axis >= 0 ? AxisColors[_axis] : BoxColor);
    }

    // ---- picking and drawing -------------------------------------------------------------------

    // Which faces look toward the camera. Without X-Ray, only what's on those faces is shown and can be picked.
    private void FindFacing(ModelEdit edit, Vector3 eye)
    {
        if (_facing.Length != edit.Polys.Count) _facing = new bool[edit.Polys.Count];
        for (int i = 0; i < _facing.Length; i++)
        {
            var poly = edit.Polys[i];
            _facing[i] = _xray || Vector3.Dot(edit.FaceNormal(poly), eye - edit.FaceCenter(poly)) > 0f;
        }
    }

    private bool Shown(ModelEdit edit, int point)
    {
        if (_xray) return true;
        foreach (int poly in edit.PolysOf(point))
            if (poly < _facing.Length && _facing[poly]) return true;
        return false;
    }

    private static float Reach => OperatingSystem.IsIOS() ? 22f : 12f;

    private int? NearestPoint(ModelEdit edit, Matrix4x4 world, float2 mouse)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < edit.PointCount; i++)
        {
            if (!Shown(edit, i)) continue;
            var at = Vector3.Transform(edit.Point(i), world);
            if (!Project(at, _viewProj, _area, out var screen)) continue;
            if (MathF.Abs(screen.X - mouse.X) > Reach || MathF.Abs(screen.Y - mouse.Y) > Reach) continue;
            float distance = Vector3.DistanceSquared(at, _eye);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best >= 0 ? best : null;
    }

    private (int A, int B)? NearestEdge(ModelEdit edit, Matrix4x4 world, float2 mouse)
    {
        (int, int)? best = null;
        float bestDistance = Reach;
        foreach (var (a, b) in edit.Edges)
        {
            if (!Shown(edit, a) || !Shown(edit, b)) continue;
            if (!Project(Vector3.Transform(edit.Point(a), world), _viewProj, _area, out var pa) ||
                !Project(Vector3.Transform(edit.Point(b), world), _viewProj, _area, out var pb))
                continue;
            float distance = DistanceToSegment(mouse, pa, pb);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = (a, b);
            }
        }
        return best;
    }

    private Poly? FaceAt(ModelEdit edit, Matrix4x4 world, float2 mouse)
    {
        Poly? best = null;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < edit.Polys.Count; i++)
        {
            if (i >= _facing.Length || !_facing[i]) continue;
            var poly = edit.Polys[i];
            var outline = new float2[poly.Corners.Length];
            bool onScreen = true;
            for (int c = 0; c < outline.Length && onScreen; c++)
                onScreen = Project(Vector3.Transform(edit.Point(poly.Corners[c].Point), world), _viewProj, _area, out outline[c]);
            if (!onScreen || !Inside(outline, mouse)) continue;
            float distance = Vector3.DistanceSquared(Vector3.Transform(edit.FaceCenter(poly), world), _eye);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = poly;
            }
        }
        return best;
    }

    private static bool Inside(float2[] outline, float2 at)
    {
        bool inside = false;
        for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
            if ((outline[i].Y > at.Y) != (outline[j].Y > at.Y) &&
                at.X < (outline[j].X - outline[i].X) * (at.Y - outline[i].Y) / (outline[j].Y - outline[i].Y) + outline[i].X)
                inside = !inside;
        return inside;
    }

    private static float DistanceToSegment(float2 at, float2 a, float2 b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float lengthSquared = dx * dx + dy * dy;
        float t = lengthSquared < 1e-6f ? 0f : Math.Clamp(((at.X - a.X) * dx + (at.Y - a.Y) * dy) / lengthSquared, 0f, 1f);
        return Distance(at, new float2(a.X + dx * t, a.Y + dy * t));
    }

    private void DrawOverlay(Canvas2D canvas, ModelEdit edit, Matrix4x4 world, Matrix4x4 viewProj, float4 area)
    {
        var screen = new float2[edit.PointCount];
        var visible = new bool[edit.PointCount];
        for (int i = 0; i < screen.Length; i++)
            visible[i] = Shown(edit, i) && Project(Vector3.Transform(edit.Point(i), world), viewProj, area, out screen[i]);

        for (int i = 0; i < edit.Polys.Count && i < _facing.Length; i++)
        {
            var poly = edit.Polys[i];
            if (!_facing[i] || !edit.IsSelected(poly)) continue;
            var corners = poly.Corners;
            for (int c = 1; c + 1 < corners.Length; c++)
                if (visible[corners[0].Point] && visible[corners[c].Point] && visible[corners[c + 1].Point])
                    canvas.Triangle(screen[corners[0].Point], screen[corners[c].Point], screen[corners[c + 1].Point], FaceColor);
        }

        if (edit.Edges.Count <= MaxDrawnEdges)
            foreach (var (a, b) in edit.Edges)
                if (visible[a] && visible[b])
                {
                    bool selected = edit.Selection.Contains(a) && edit.Selection.Contains(b);
                    canvas.Line(screen[a], screen[b], selected ? 2f : 1f, selected ? SelectedColor : EdgeColor);
                }

        if (_selectMode == SelectMode.Vertex && edit.PointCount <= MaxDrawnPoints)
            for (int i = 0; i < edit.PointCount; i++)
                if (visible[i] && !edit.Selection.Contains(i) && InArea(screen[i], area))
                    canvas.Rect(screen[i].X - 2.5f, screen[i].Y - 2.5f, 5f, 5f, PointColor);

        if (_selectMode == SelectMode.Face && edit.Polys.Count <= MaxDrawnPoints)
            for (int i = 0; i < edit.Polys.Count && i < _facing.Length; i++)
                if (_facing[i] && Project(Vector3.Transform(edit.FaceCenter(edit.Polys[i]), world), viewProj, area, out var center) && InArea(center, area))
                    canvas.Rect(center.X - 2f, center.Y - 2f, 4f, 4f, edit.IsSelected(edit.Polys[i]) ? SelectedColor : PointColor);

        foreach (int i in edit.Selection)
            if (i < visible.Length && visible[i] && InArea(screen[i], area))
                canvas.Rect(screen[i].X - 3.5f, screen[i].Y - 3.5f, 7f, 7f, SelectedColor);

        if (_boxStart is { } start)
            canvas.Outline(MathF.Min(start.X, _boxEnd.X), MathF.Min(start.Y, _boxEnd.Y), MathF.Abs(_boxEnd.X - start.X), MathF.Abs(_boxEnd.Y - start.Y), 1f, BoxColor);
    }

    private static bool InPlace(string model) => string.Equals(Path.GetExtension(model), ".glb", StringComparison.OrdinalIgnoreCase);

    // A name for the .glb that replaces a model that can't be changed in place, one that isn't taken.
    private string NewFileFor(string model)
    {
        string folder = Primitives.IsBuiltIn(model) ? "Models" : Path.GetDirectoryName(model) ?? "";
        string name = Primitives.IsBuiltIn(model) ? Primitives.Label(model) : Path.GetFileNameWithoutExtension(model);
        string candidate = Path.Combine(folder, name + ".glb");
        for (int i = 2; File.Exists(Path.Combine(contentRoot, candidate)); i++)
            candidate = Path.Combine(folder, $"{name} {i}.glb");
        return candidate;
    }

    // Writes beside the file and then replaces it, so a failed write can't leave a model half saved.
    private static void WriteFile(string file, byte[] bytes)
    {
        string temporary = file + ".saving";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, file, overwrite: true);
    }

    private static bool InArea(float2 at, float4 area) =>
        at.X >= area.X && at.Y >= area.Y && at.X <= area.X + area.Z && at.Y <= area.Y + area.W;

    private static bool Project(Vector3 point, Matrix4x4 viewProj, float4 area, out float2 screen)
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
}
