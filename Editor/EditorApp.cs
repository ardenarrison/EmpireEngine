using System.Diagnostics;
using System.Reflection;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Engine.Brutal;
using Engine.Brutal.Audio;
using Engine.Brutal.Gfx;
using Engine.Brutal.Rendering;
using Engine.Core.Assets;
using Engine.Core.Audio;
using Engine.Core.Rendering;
using Engine.Core.Scene;
using Engine.Core.Scripting;
using Engine.Core.Serialization;
using Engine.Scripting.Build;
using Editor.Code;
using Editor.Ui;

namespace Editor;

public enum EditorMode { Edit, Play }

// Steps of File > Build Project: pick a folder, enter a name, wait for the build, then show the result.
public enum BuildStage { Off, PickFolder, EnterName, Running, Done }

// Edit mode: the scene is static and can be changed in the panels.
// Play mode: components update each frame. Stopping restores the scene from the snapshot taken on Play.
public sealed class EditorApp
{
    private readonly string _scenePath;
    private readonly string _contentRoot;
    private readonly AssetCache _assets;
    private readonly EditorCamera _camera = new();
    private readonly TransformGizmo _gizmo = new();
    private readonly ScriptFieldCache _scriptFields;
    private readonly UndoHistory _history = new();
    private readonly VisualScriptView _vc;
    private bool _vcMode;   // the visual scripting view replaces the 3D view while on
    private readonly CodeEditorView _code;
    private bool _codeMode;   // the C# code view replaces the 3D view while on
    private bool FullView => _vcMode || _codeMode;
    private readonly ModelBrowser _browser = new();
    private readonly AssetPanel _assetPanel;
    private readonly UiOverlay _uiOverlay = new();
    private bool _pickingParent;
    private bool _addingComponent;
    private readonly HashSet<string> _badModels = new(StringComparer.OrdinalIgnoreCase);
    private Guid? _hierPressId;        // row the mouse went down on
    private float2 _hierPressCursor;
    private Guid? _hierDragId;         // row being dragged
    private Guid? _hierReleasedId;     // row let go of this frame away from the hierarchy rows, for object fields to take
    private readonly Dictionary<Guid, Box> _hierRows = new();   // screen rect of each visible row
    private readonly Dictionary<Guid, string> _optionText = new();   // dropdown options being edited, per object
    private bool _uiMode;   // Edit mode with the UI overlay: elements are outlined and selectable
    private readonly HashSet<string> _uploaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _snapshotPath = Path.Combine(Path.GetTempPath(), "empire-play-snapshot.empirescene");
    private Scene _scene;
    private EditorMode _mode = EditorMode.Edit;
    private Guid? _selectedId;          // the primary selection: the Inspector and gizmo act on it
    private SceneObject? _selected;
    private readonly List<Guid> _extraSelected = new();   // more objects added with shift/cmd-click, oldest first
    private Guid? _selectionAnchor;     // where a shift-click range starts: the row last clicked without shift
    private Guid? _extraFor;            // the primary the extras belong to; picking another object any other way clears them
    private Guid? _browserTarget;
    private Guid? _panoramaTarget;   // the object whose sky environment the browser is choosing a panorama image for
    private (Guid Id, int Index)? _meshColliderTarget;   // the mesh collider the model browser is choosing for
    private (Guid Id, int Index)? _audioTarget;   // the audio source the browser is choosing a sound for
    private string _savedJson = "";     // the scene as last loaded or saved; a difference means unsaved edits
    private bool _unsaved;
    private bool _exitRequested;        // the main loop stops after this frame
    private bool _confirmingExit;       // the unsaved-changes prompt is open
    private readonly FolderBrowser _folderBrowser = new();
    private readonly CreditsWindow _credits = new();
    private string _buildName = "";
    private BuildStage _buildStage = BuildStage.Off;
    private string _buildFolder = "";
    private Task<BuildResult>? _buildTask;
    private BuildResult? _buildResult;
    private readonly FullscreenToggle _fullscreen = new();
    private bool _pickingScript;
    private string _newScriptName = "";
    private readonly TerrainDrawer _terrains = new();
    private readonly ScriptMeshes _scriptMeshes = new();   // meshes scripts make at runtime, like water
    private PackageManagerWindow _packages = null!;
    private GlfwWindow? _window;

    // The scene file whose settings the Inspector shows (relative to the project folder), when one was clicked in the
    // Assets panel. For a scene other than the open one, its settings are read from the file and written back to it.
    private string? _inspectedScene;
    private (SceneSettings Settings, Vector3 Gravity)? _inspectedFileSettings;
    private bool _inspectedFileDirty;
    private string _inspectedSceneError = "";   // set when the render loop starts, for things outside it that touch the window

    // Terrain sculpting: while the selected terrain's Sculpt button is on, left-dragging in the scene paints with the brush.
    private bool _sculpting;
    private Guid? _sculptTarget;   // the object being sculpted; picking another object ends sculpting
    private TerrainBrush _brush = TerrainBrush.Raise;
    private float _brushRadius = 6f;
    private float _brushStrength = 4f;
    private float _flattenLevel = 2f;
    private int _genSeed = 1;
    private float _genSize = 30f;
    private int _genOctaves = 5;
    private float _genRoughness = 0.5f;
    private float _genAmount = 0.8f;
    private int _paintLayer;   // the layer the Paint brush paints with
    private readonly Gui _gui = new();   // the editor's panels, menus and gizmos, drawn with Canvas2D

    private static readonly string[] AntiAliasingNames = { "None", "MSAA" };
    private static readonly int[] MsaaCounts = { 2, 4, 8 };
    private static readonly string[] MsaaNames = { "2x", "4x", "8x" };
    private int _maxSamples = 1;   // the most MSAA samples this GPU supports, read once the renderer exists

    private const float PanelTop = Gui.MenuBarHeight + 6f;
    private const float SideWidth = 300f;   // the Hierarchy and Inspector are docked to the window's left and right edges

    public EditorApp(string scenePath, string contentRoot)
    {
        _scenePath = scenePath;
        _contentRoot = contentRoot;
        _assets = new AssetCache(contentRoot);
        Engine.Core.Physics.PhysicsWorld.ModelLoader = _assets.GetModel;   // mesh colliders read models through the cache
        Animator.ModelLoader = _assets.GetModel;                           // and so do animations
        AudioEngine.ClipLoader = _assets.GetAudio;                         // so do sounds
        SceneManager.AssetWarmer = _assets.Warm;                           // scenes that load in the background preload their assets
        // Games played in the editor save under the user's application data folder, by project name.
        SaveGame.Directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EmpireEditor", Path.GetFileName(Path.GetFullPath(contentRoot).TrimEnd('\\', '/')), "Saves");
        _assetPanel = new AssetPanel(contentRoot);
        _vc = new VisualScriptView(Path.Combine(contentRoot, "Scripts"));
        _code = new CodeEditorView(Path.Combine(contentRoot, "Scripts"));
        _scriptFields = new ScriptFieldCache(contentRoot);
        _packages = new PackageManagerWindow(contentRoot);
        _packages.Changed += _scriptFields.Invalidate;
        // The code view checks scripts against the enabled packages too, so using their classes isn't an error.
        Editor.Code.CodeEditorView.ExtraReferences = () => Packages.Enabled(_contentRoot)
            .Select(id => Packages.DllPath(_contentRoot, id)).Where(File.Exists);
        _scene = SceneSerializer.Load(scenePath);
        EnsureSun();
        EnsureCamera();
        _savedJson = SceneSerializer.ToJson(_scene);
        _history.Reset(_savedJson);
    }

    public void Run()
    {
        BundledVulkan.Prepare();
        if (!Glfw.Init())
            throw new InvalidOperationException("GLFW failed to initialize");
        try
        {
            Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);
            var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo
            {
                Title = $"Empire Engine Editor - {_scene.Name}",
                Size = new int2(1280, 720),
            });
            AppIcon.Apply(window);
            try
            {
                RenderLoop(window);
            }
            finally
            {
                window.Dispose();
            }
        }
        finally
        {
            Glfw.Terminate();
        }
    }

    private void RenderLoop(GlfwWindow window)
    {
        _window = window;
        using var ctx = new VulkanContext(window, 1280, 720);
        using var shaders = new ShaderLibrary(ctx.Device);
        using var renderer = new BrutalRenderer(ctx, shaders);
        renderer.AfterSceneDraws = cmd =>
        {
            if (_mode == EditorMode.Play) VulkanScripts.Render(_scene, cmd);
        };
        using var ui = new EditorUi(window, ctx);
        using var audio = new AudioOutput();   // the sound device; the editor runs silent if there is none
        float pixelScale = window.ContentScale.X;   // 2 on Retina, so the text atlases start at the right density
        _maxSamples = ctx.MaxSampleCount;
        // The VC and code views share the editor's monospaced font.
        byte[] codeFont = Canvas2D.DefaultFont();
        using var canvas = new Canvas2D(ctx, shaders, codeFont, 15f, pixelScale);
        using var codeCanvas = new Canvas2D(ctx, shaders, codeFont, 15f, pixelScale);
        using var uiCanvas = new Canvas2D(ctx, shaders, codeFont, 15f, pixelScale);
        // The game's UI, drawn the same way the built game draws it, under the editor's own UI.
        using var gameCanvas = new Canvas2D(ctx, shaders, codeFont, BrutalPlayer.UiFontSize, pixelScale);

        // The editor opens with the Empire Engine and BRUTAL logos, the same screen games open with (the BRUTAL License
        // requires the BRUTAL logo before a product's main function begins). It can't be skipped.
        SplashScreen.Run(ctx, shaders, window, uiCanvas);
        if (window.ShouldClose) return;

        foreach (var key in ModelKeys())
            EnsureUploaded(renderer, key);

        FitCamera();

        var clock = Stopwatch.StartNew();
        double lastTime = 0;
        bool prevPlay = false;
        KeyboardInput.Bind(window);
        // Scripts see the mouse in the scene view, measured from its corner, and the wheel through ImGui (which owns
        // the window's scroll callback).
        MouseInput.Bind(window, () => SceneArea(ctx), () => ImGui.GetIO().MouseWheel);
        // Typed text for the game's text fields (and Input.inputString) comes through the editor's own input too.
        Input.TextProvider = () => _gui.Typed;
        bool prevSave = false;
        bool prevUndo = false;
        double nextModelCheck = 0;
        bool prevRedo = false;
        bool prevDuplicate = false;

        while (!window.ShouldClose && !_exitRequested)
        {
            Glfw.PollEvents();

            // The window's close button goes through the unsaved-changes check like File > Exit.
            if (window.ShouldClose && !_exitRequested)
            {
                window.ShouldClose = false;
                RequestExit();
            }

            _fullscreen.Update(window);
            if (ctx.IsMinimized)
            {
                Thread.Sleep(16);
                continue;
            }


            double now = clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(now - lastTime, 0.1);
            lastTime = now;

            _scriptFields.Update(now);   // re-reads script fields in the background after a .cs file is saved

            // Once a second, reload models whose files changed on disk, so a Blender re-export shows up right away.
            if (now >= nextModelCheck)
            {
                nextModelCheck = now + 1.0;
                foreach (var path in _assets.ReloadChanged())
                {
                    if (_uploaded.Contains(path))
                        renderer.ReloadModel(path, _assets.GetModel(path));
                    Console.WriteLine($"Reloaded {path}.");
                }
            }

            ui.BeginFrame();
            _gui.Begin(window, uiCanvas, ImGui.GetIO().DisplaySize, now);
            // Editor panels, or the game's UI while playing.
            bool uiWantsMouse = _gui.WantsMouse || (_mode == EditorMode.Play && GameUi.PointerOverUi);

            // Escape stops play mode when playing, cancels the exit prompt, closes the open dialog, and otherwise
            // closes the editor. Read from ImGui's input queue, so even a very quick tap between frames counts.
            if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            {
                if (_mode == EditorMode.Play) TogglePlay();
                else if (_confirmingExit) _confirmingExit = false;
                else if (_gui.IsTyping) { }   // the field being typed in uses Escape to stop typing
                else if (CloseTopDialog()) { }
                else if (_vcMode) { if (!_vc.IsTyping) _vcMode = false; }
                else if (_codeMode) { }   // the code view uses Escape itself; leave with its 3D view button
                else RequestExit();
            }

            bool playDown = window.GetKey(GlfwKey.P) == GlfwKeyAction.Press;
            prevPlay = playDown;

            // Cmd+S on macOS, Ctrl+S elsewhere. Acts on the press, not while the key is held.
            bool saveDown = IsSaveShortcutHeld(window) && window.GetKey(GlfwKey.S) == GlfwKeyAction.Press;
            if (saveDown && !prevSave)
            {
                if (_codeMode && _mode == EditorMode.Edit) _code.SaveCurrent();
                else SaveScene();
            }
            prevSave = saveDown;

            // Cmd/Ctrl+Z undoes, Cmd/Ctrl+Shift+Z (or Ctrl+Y) redoes. Text fields keep their own undo while typing.
            bool shortcut = IsSaveShortcutHeld(window);
            bool shift = window.GetKey(GlfwKey.LeftShift) == GlfwKeyAction.Press ||
                         window.GetKey(GlfwKey.RightShift) == GlfwKeyAction.Press;
            bool zDown = window.GetKey(GlfwKey.Z) == GlfwKeyAction.Press;
            bool undoDown = shortcut && zDown && !shift;
            bool redoDown = shortcut && ((zDown && shift) || window.GetKey(GlfwKey.Y) == GlfwKeyAction.Press);
            bool canUndo = _mode == EditorMode.Edit && !FullView && !_gui.IsTyping && !_gizmo.IsDragging;
            if (canUndo && undoDown && !prevUndo)
                ApplyHistory(_history.Undo(SceneSerializer.ToJson(_scene)), "Undo");
            if (canUndo && redoDown && !prevRedo)
                ApplyHistory(_history.Redo(SceneSerializer.ToJson(_scene)), "Redo");
            prevUndo = undoDown;
            prevRedo = redoDown;

            // Cmd/Ctrl+D duplicates the selected object (and its children) in place, then selects the copy.
            bool duplicateDown = shortcut && window.GetKey(GlfwKey.D) == GlfwKeyAction.Press;
            if (canUndo && duplicateDown && !prevDuplicate)
                DuplicateSelected();
            prevDuplicate = duplicateDown;

            // After a play-mode restore the scene objects are new instances, so re-resolve by ID.
            _selected = _selectedId is { } id ? _scene.Find(id) : null;
            if (_selectedId != _extraFor || _selected is null)
                _extraSelected.Clear();
            _extraSelected.RemoveAll(extra => _scene.Find(extra) is null);

            if (!FullView)
                DrawToolbar();

            // The asset panel runs along the bottom while editing, so the scene is fitted above it.
            ctx.BottomReservedFraction = _mode == EditorMode.Edit && !FullView
                ? AssetPanel.Height / Math.Max(1f, ImGui.GetIO().DisplaySize.Y)
                : 0f;

            if (_mode == EditorMode.Edit && _vcMode)
                _vc.Update(window, uiWantsMouse);
            else if (_mode == EditorMode.Edit && _codeMode)
            {
                if (!_gui.IsTyping) _code.Update(window, uiWantsMouse, now);
            }
            else if (_mode == EditorMode.Edit)
            {
                _camera.Update(window, dt, uiWantsMouse, ImGui.GetIO().MouseWheel);
                Particles.Update(_scene, dt);   // live preview while editing
                Animators.UpdatePreview(_scene, dt);
            }
            else
            {
                Simulate(dt);   // the game's UI takes its clicks and typing inside, before scripts run
            }

            // Play mode is the game view: the editor panels are not drawn.
            if (_mode == EditorMode.Edit && !FullView)
                DrawPanels(renderer, window);
            else if (_mode == EditorMode.Edit)
            {
                DrawMenuBar();
                DrawBuildWindow();
                DrawExitPrompt();
            }

            // The game's UI: live while playing, and a still preview with outlines in UI mode.
            gameCanvas.Begin();
            if (_mode == EditorMode.Play)
                UiRenderer.Draw(_scene, gameCanvas, SceneArea(ctx));
            else if (_uiMode && !FullView)
            {
                UiRenderer.Draw(_scene, gameCanvas, SceneArea(ctx), live: false);
                if (_uiOverlay.Draw(_scene, gameCanvas, SceneArea(ctx), _selectedId, _gui, uiWantsMouse) is { } picked)
                    _selectedId = picked;
            }

            string title = $"Empire Engine Editor - {_scene.Name}" +
                           (_mode == EditorMode.Play ? " (Playing)" : "") +
                           (_unsaved ? " *" : "");
            if (window.Title != title)
                window.Title = title;

            float aspect = VulkanContext.SceneAspect;
            Matrix4x4 viewProj;
            Vector3 eye;
            (SceneObject Obj, CameraComponent Cam)? gameCam = _mode == EditorMode.Play ? GameCamera() : null;
            if (gameCam is { } g)
            {
                // Play mode renders through the scene camera, the same way the compiled game does.
                eye = g.Obj.WorldMatrix.Translation;
                viewProj = g.Cam.View(g.Obj.WorldMatrix) *
                           VulkanMath.Projection(g.Cam.VerticalFov(aspect), aspect, g.Cam.Near, g.Cam.Far);
            }
            else
            {
                eye = _camera.Position;
                viewProj = _camera.View * VulkanMath.Projection(MathF.PI / 3f, aspect, 0.1f, 500f);
            }
            var sceneArea = SceneArea(ctx);
            Input.SetView(new Vector2(sceneArea.Z, sceneArea.W), viewProj);   // for Input.GetMouseRay in scripts

            // A game that locked the cursor for mouse-look lets it go on Escape, so the editor stays usable.
            if (_mode == EditorMode.Play && Input.cursorLocked && window.GetKey(GlfwKey.Escape) == GlfwKeyAction.Press)
                MouseInput.Release(window);

            // Scene gizmos go on the canvas's bottom layer, under the panels.
            uiCanvas.Layer = Canvas2D.GizmoLayer;
            uiCanvas.Clip = null;
            if (_mode == EditorMode.Edit && !FullView)
            {
                DrawColliderGizmos(uiCanvas, viewProj, ctx.SceneViewport);
                DrawParticleShapeGizmos(uiCanvas, viewProj, ctx.SceneViewport);
                DrawCameraGizmos(uiCanvas, viewProj, ctx.SceneViewport);
                DrawLightGizmos(uiCanvas, viewProj, ctx.SceneViewport);
            }

            bool sculptingNow = _mode == EditorMode.Edit && !FullView && !_uiMode &&
                                UpdateTerrainSculpt(window, uiCanvas, viewProj, SceneArea(ctx), uiWantsMouse, dt);
            if (_mode == EditorMode.Edit && !FullView && !_uiMode && !sculptingNow)
                _gizmo.Update(window, uiCanvas, _selected, SelectedExtras(), viewProj, eye, SceneArea(ctx), uiWantsMouse);
            uiCanvas.Layer = Canvas2D.PanelLayer;

            // An edit becomes an undo step once it has settled: no button held, no field or drag in progress.
            bool settled = window.GetMouseButton(GlfwMouseButton.Left) != GlfwButtonAction.Press &&
                           !_gui.AnyActive && !_gizmo.IsDragging && _hierDragId is null;
            if (_mode == EditorMode.Edit && settled)
                _history.Record(SceneSerializer.ToJson(_scene));

            if (_mode == EditorMode.Edit && _assetPanel.TakeDrop() is { } drop && !_gui.WantsMouse)
            {
                if (AudioClip.IsAudio(drop.ModelPath))
                    DropSoundIntoScene(drop.ModelPath, drop.Cursor, ctx.SceneViewport, viewProj);
                else
                    DropModelIntoScene(renderer, drop.ModelPath, drop.Cursor, ctx.SceneViewport, viewProj);
            }

            // The main camera's anti-aliasing applies in the editor view and in play mode. Changing it rebuilds the
            // scene's images, so it only does work when the setting actually changes.
            ctx.SetSampleCount(GameCamera()?.Cam.RequestedSamples ?? 1);

            // The sky, ambient light and every light besides the sun.
            var (environment, extraLights) = SceneLighting.Collect(_scene, eye);
            renderer.SetLighting(environment, extraLights, _assets.GetImage);

            var sun = FindSun();
            _terrains.Sync(renderer, _scene);
            _scriptMeshes.Sweep(renderer);
            renderer.SetShadows(_scene.Settings.Shadows && (sun?.Shadows ?? false), sun?.ShadowStrength ?? 0f, sun?.ShadowDistance ?? 40f, _scene.Settings.ShadowResolution);
            renderer.BeginFrame(viewProj, eye,
                sun?.Direction ?? Vector3.UnitY, sun?.Radiance ?? Vector3.Zero);
            if (!FullView)
            foreach (var obj in _scene.All)
                foreach (var mr in obj.Components.OfType<MeshRenderer>())
                {
                    if (mr.Mesh is { } scriptMesh)
                        renderer.DrawModel(_scriptMeshes.KeyFor(renderer, scriptMesh), obj.WorldMatrix, mr.Tint, mr.Pose);
                    else if (EnsureUploaded(renderer, mr.Model))
                        renderer.DrawModel(mr.Model, obj.WorldMatrix, mr.Tint, mr.Pose);
                }
            if (!FullView)
            {
                _terrains.Draw(renderer, _scene);
                Particles.Draw(_scene, renderer, eye);
            }
            var canvasSize = ImGui.GetIO().DisplaySize;
            var canvasScale = ImGui.GetIO().DisplayFramebufferScale;
            bool showVc = _mode == EditorMode.Edit && _vcMode;
            bool showCode = _mode == EditorMode.Edit && _codeMode;
            if (showVc)
                _vc.Build(canvas, canvasSize);
            if (showVc && _vc.TakeExit())
                _vcMode = false;
            if (showCode)
                _code.Build(codeCanvas, canvasSize);
            if (showCode && _code.TakeExit())
                _codeMode = false;
            _gui.End();
            ctx.EndFrame(cmd =>
            {
                if (showVc)
                    canvas.Render(cmd, canvasSize.X, canvasSize.Y, canvasScale);
                if (showCode)
                    codeCanvas.Render(cmd, canvasSize.X, canvasSize.Y, canvasScale);
                ui.Render(cmd);   // ImGui only supplies keyboard input now, so this ends its frame and draws nothing
                gameCanvas.Render(cmd, canvasSize.X, canvasSize.Y, canvasScale);   // the game's UI
                uiCanvas.Render(cmd, canvasSize.X, canvasSize.Y, canvasScale);     // the editor's own UI, on top
            });
        }

        ctx.WaitIdle();
        if (_mode == EditorMode.Play)
            VulkanScripts.Release(_scene);
    }

    // ---- panels ----------------------------------------------------------------------

    private void DrawPanels(BrutalRenderer renderer, GlfwWindow window)
    {
        DrawHierarchy(window);
        DrawToolButtons();
        DrawInspector();
        _assetPanel.Draw(_gui);

        if (_browser.Draw(_gui) is { } picked)
        {
            if (_audioTarget is { } audioTarget)
            {
                if (_scene.Find(audioTarget.Id)?.Components.ElementAtOrDefault(audioTarget.Index) is AudioSource source)
                    source.Clip = picked;
            }
            else if (_meshColliderTarget is { } target)
            {
                if (_scene.Find(target.Id)?.Components.ElementAtOrDefault(target.Index) is MeshCollider mc)
                    mc.Model = picked;
            }
            else if (_panoramaTarget is { } envId)
            {
                if (_scene.Find(envId)?.Components.OfType<SceneEnvironment>().FirstOrDefault() is { } env)
                    env.Panorama = picked;
            }
            else if (_browserTarget is { } targetId &&
                     _scene.Find(targetId)?.Components.OfType<MeshRenderer>().FirstOrDefault() is { } mr)
            {
                mr.Model = picked;
                EnsureUploaded(renderer, picked);
            }
            _browserTarget = null;
            _meshColliderTarget = null;
            _panoramaTarget = null;
            _audioTarget = null;
        }

        DrawBuildWindow();
        DrawScriptPicker();
        DrawMenuBar();
        DrawExitPrompt();
    }

    private void DrawHierarchy(GlfwWindow window)
    {
        var g = _gui;
        _hierRows.Clear();
        bool editing = _mode == EditorMode.Edit;

        g.BeginWindow("Hierarchy", new Box(0f, Gui.MenuBarHeight, SideWidth, SideHeight()), "Hierarchy");

        if (editing)
        {
            if (_selected is not null)
            {
                if (g.Button("Add Child")) AddChild();
                g.SameLine();
                if (g.Button("Delete")) DeleteSelected();
            }
        }

        g.Separator();
        foreach (var obj in _scene.InHierarchyOrder.ToList())
        {
            float indent = Depth(obj) * 14f;
            g.Indent(indent);
            bool clicked = g.Selectable($"{SafeLabel.Of(obj.Name)}##{obj.Id:N}", IsSelected(obj));
            g.Unindent(indent);
            if (clicked)
                ClickHierarchyRow(obj, shift: g.Shift, toggle: IsSaveShortcutHeld(window));
            TrackHierarchyRow(obj, editing);
        }

        FinishHierarchyDrag(editing);

        if (editing && g.BeginContextMenu("hierarchy"))
        {
            g.TextDisabled("Objects");
            if (g.MenuItem("Add Object")) AddObject();
            if (g.MenuItem("Add Camera")) AddCamera();
            g.Separator();
            g.TextDisabled("Lighting");
            if (g.MenuItem("Point Light")) AddLightObject("Point Light", new PointLight(), pitch: 0f);
            if (g.MenuItem("Spot Light")) AddLightObject("Spot Light", new SpotLight(), pitch: -50f);
            if (g.MenuItem("Directional Light")) AddLightObject("Directional Light", new DirectionalLight(), pitch: 0f);
            if (g.MenuItem("Environment (sky and ambient)")) AddEnvironmentObject();
            g.Separator();
            g.TextDisabled("World");
            if (g.MenuItem("Terrain")) AddTerrainObject();
            g.Separator();
            g.TextDisabled("Effects");
            if (g.MenuItem("Particle System")) AddParticleSystemObject();
            foreach (var preset in ParticlePresets.All)
                if (g.MenuItem(preset.Name)) AddParticleSystemObject(preset);
            g.Separator();
            g.TextDisabled("Audio");
            if (g.MenuItem("Audio Source")) AddAudioSourceObject();
            g.Separator();
            g.TextDisabled("UI");
            if (g.MenuItem("Button")) AddUi(UiKind.Button);
            if (g.MenuItem("Text Field")) AddUi(UiKind.TextField);
            if (g.MenuItem("Checkbox")) AddUi(UiKind.Checkbox);
            if (g.MenuItem("Dropdown")) AddUi(UiKind.Dropdown);
            if (g.MenuItem("Bar")) AddUi(UiKind.Bar);
            g.EndPopup();
        }

        g.EndWindow();
    }

    private void DrawInspector()
    {
        var g = _gui;
        bool editing = _mode == EditorMode.Edit;

        g.BeginWindow("Inspector", new Box(g.Size.X - SideWidth, Gui.MenuBarHeight, SideWidth, SideHeight()), "Inspector");

        // A scene clicked in the Assets panel takes the Inspector over; picking an object hands it back.
        if (_assetPanel.TakeClickedScene() is { } clickedScene)
        {
            _inspectedScene = clickedScene;
            _inspectedFileSettings = null;
            _inspectedSceneError = "";
            _selectedId = null;
            _selected = null;
        }
        if (_selected is not null) _inspectedScene = null;
        _assetPanel.InspectedScene = _inspectedScene;
        if (_inspectedScene is { } sceneFile)
        {
            DrawSceneSettings(sceneFile, editing);
            g.EndWindow();
            return;
        }

        if (_selected is null)
        {
            g.TextDisabled("Select an object in the hierarchy.");
            g.EndWindow();
            return;
        }

        var obj = _selected;
        if (!editing)
            g.TextDisabled("Editing is disabled in play mode.");

        DrawNameField(obj, editing);
        DrawParentPicker(obj, editing);
        g.Separator();

        if (obj.Parent is not null)
            g.TextWrapped("Position, rotation and scale are relative to the parent.", Gui.DimColor);
        var pos = new float3(obj.Position.X, obj.Position.Y, obj.Position.Z);
        if (g.DragFloat3("Position", ref pos) && editing)
            obj.Position = new Vector3(pos.X, pos.Y, pos.Z);

        var scale = new float3(obj.Scale.X, obj.Scale.Y, obj.Scale.Z);
        if (g.DragFloat3("Scale", ref scale) && editing)
            obj.Scale = new Vector3(scale.X, scale.Y, scale.Z);

        var euler = EulerRotation.ToDegrees(obj.Rotation);
        var rot = new float3(euler.X, euler.Y, euler.Z);
        if (g.DragFloat3("Rotation", ref rot, 0.5f) && editing)
            obj.Rotation = EulerRotation.FromDegrees(rot.X, rot.Y, rot.Z);

        if (editing && g.Button("Reset Rotation"))
            obj.Rotation = Quaternion.Identity;

        // One section per component, in the order they were added.
        int index = 0;
        foreach (var comp in obj.Components.ToList())
        {
            g.Separator();
            g.PushId(index);
            DrawComponentSection(obj, comp, editing, index++);
            g.PopId();
        }

        g.Separator();
        if (editing && g.Button("Add Component", width: -1f))
            _addingComponent = !_addingComponent;
        if (editing && _addingComponent)
            DrawAddComponentList(obj);

        g.EndWindow();
    }

    // A component's section: a title bar with an X on its right that removes the component, then its fields.
    private void DrawComponentSection(SceneObject obj, Component comp, bool editing, int index)
    {
        var g = _gui;
        string title = comp switch
        {
            MeshRenderer => "Mesh",
            ScriptComponent sc when _scriptFields.Find(sc.ScriptName) is { IsComponent: true } custom => custom.Name,
            ScriptComponent => "Script",
            Spin => "Spin",
            CameraComponent => "Camera",
            DirectionalLight => "Directional Light",
            PointLight => "Point Light",
            SpotLight => "Spot Light",
            SceneEnvironment => "Environment",
            Rigidbody => "Rigidbody",
            BoxCollider => "Box Collider",
            SphereCollider => "Sphere Collider",
            MeshCollider => "Mesh Collider",
            ParticleSystem => "Particle System",
            AudioSource => "Audio Source",
            AudioListener => "Audio Listener",
            UiElement ui => $"UI {ui.Kind}",
            _ => comp.GetType().Name,
        };

        g.Button($"{title}##title", width: -30f);
        g.SameLine();
        if (editing && g.Button("X##remove", width: 24f))
        {
            obj.Components.Remove(comp);
            return;
        }

        switch (comp)
        {
            case MeshRenderer mr:
                DrawMeshBody(obj, mr, editing);
                break;
            case ScriptComponent script:
                DrawScriptBody(obj, script, editing, index);
                break;
            case CameraComponent cam:
                DrawCameraBody(obj, cam, editing);
                break;
            case DirectionalLight light:
                DrawLightBody(light, editing);
                break;
            case PointLight point:
                DrawPointLightBody(point, editing);
                break;
            case SpotLight spot:
                DrawSpotLightBody(spot, editing);
                break;
            case SceneEnvironment env:
                DrawEnvironmentBody(obj, env, editing);
                break;
            case UiElement ui:
                DrawUiInspector(obj, ui, editing);
                break;
            case Rigidbody rb:
                DrawRigidbodyBody(rb, editing);
                break;
            case BoxCollider box:
                DrawBoxColliderBody(box, editing);
                break;
            case SphereCollider sphere:
                DrawSphereColliderBody(sphere, editing);
                break;
            case MeshCollider mesh:
                DrawMeshColliderBody(obj, mesh, editing, index);
                break;
            case Terrain terrain:
                DrawTerrainBody(obj, terrain, editing);
                break;
            case Animator animator:
                DrawAnimatorBody(obj, animator, editing);
                break;
            case AudioSource audio:
                DrawAudioSourceBody(obj, audio, editing, index);
                break;
            case AudioListener:
                DrawAudioListenerBody();
                break;
            case ParticleSystem particles:
                DrawParticleSystemBody(particles, editing);
                break;
        }
    }

    // ---- scene settings --------------------------------------------------------------------

    // The settings of a scene file: for the open scene they change it directly (and save with it); for any other scene
    // they are read from its file and written back as soon as an edit settles.
    private void DrawSceneSettings(string relativePath, bool editing)
    {
        var g = _gui;
        string full = Path.GetFullPath(Path.Combine(_contentRoot, relativePath));
        bool isOpen = string.Equals(full, Path.GetFullPath(_scenePath), StringComparison.OrdinalIgnoreCase);

        g.Text("Scene Settings");
        g.TextDisabled(SafeLabel.Of(relativePath, 60));
        g.TextWrapped(isOpen
            ? "This is the scene open in the editor. Changes are part of it: save with Ctrl+S, undo with Ctrl+Z."
            : "This scene is not open. Changes are written to its file right away.", Gui.DimColor);
        g.Separator();

        SceneSettings settings;
        Vector3 gravity;
        if (isOpen)
        {
            settings = _scene.Settings;
            gravity = _scene.Physics.Gravity;
        }
        else
        {
            if (_inspectedFileSettings is null)
            {
                try
                {
                    _inspectedFileSettings = SceneSerializer.ReadSettings(full);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    g.TextWrapped($"Could not read this scene: {ex.Message}", Gui.ErrorColor);
                    return;
                }
            }
            (settings, gravity) = _inspectedFileSettings.Value;
        }

        bool changed = false;
        g.TextDisabled("Shadows");
        bool shadows = settings.Shadows;
        if (g.Checkbox("Shadows", ref shadows) && editing)
        {
            settings.Shadows = shadows;
            changed = true;
        }
        if (!settings.Shadows)
            g.TextWrapped("Nothing in this scene casts a shadow. Turning them back on uses the settings below.", Gui.DimColor);
        var names = SceneSettings.ShadowResolutions.Select(r => $"{r} x {r}").ToArray();
        int current = Math.Max(0, Array.IndexOf(SceneSettings.ShadowResolutions, settings.ShadowResolution));
        if (g.Combo("Resolution", current, names, out int picked) && editing && picked != current)
        {
            settings.ShadowResolution = SceneSettings.ShadowResolutions[picked];
            changed = true;
        }
        int megabytes = settings.ShadowResolution * settings.ShadowResolution * 4 / (1024 * 1024);
        g.TextWrapped($"The size of the sun's shadow map. Bigger is sharper and uses more GPU memory ({megabytes} MB per frame in flight, two frames).", Gui.DimColor);
        g.TextWrapped("How far shadows reach is the sun's Shadow Distance, on the Directional Light.", Gui.DimColor);

        g.Separator();
        g.TextDisabled("Physics");
        var gravityField = new float3(gravity.X, gravity.Y, gravity.Z);
        if (g.DragFloat3("Gravity", ref gravityField, 0.05f) && editing)
        {
            gravity = new Vector3(gravityField.X, gravityField.Y, gravityField.Z);
            changed = true;
        }
        if (editing && g.Button("Reset Gravity##gravity"))
        {
            gravity = new Vector3(0f, -9.81f, 0f);
            changed = true;
        }
        g.TextDisabled("Units per second squared. Earth is 0, -9.81, 0.");

        if (changed)
        {
            if (isOpen)
                _scene.Physics.Gravity = gravity;
            else
            {
                _inspectedFileSettings = (settings, gravity);
                _inspectedFileDirty = true;
            }
        }

        // A file is written once the edit has settled, not on every step of a drag.
        if (!isOpen && _inspectedFileDirty && !g.AnyActive)
        {
            _inspectedFileDirty = false;
            try
            {
                SceneSerializer.WriteSettings(full, settings, gravity);
                _inspectedSceneError = "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                _inspectedSceneError = $"Could not save the settings: {ex.Message}";
            }
        }
        if (_inspectedSceneError.Length > 0)
            g.TextWrapped(_inspectedSceneError, Gui.ErrorColor);
    }

    // ---- terrain ---------------------------------------------------------------------------

    // A terrain with gentle hills, so a new one is visibly ground and not a flat sheet.
    private static Terrain NewTerrain()
    {
        var terrain = new Terrain();
        terrain.Generate(seed: 1);
        // A few layers to paint with from the start; they can be renamed, recolored, removed and added to.
        terrain.AddLayer("Dirt", new Vector3(0.42f, 0.30f, 0.20f));
        terrain.AddLayer("Sand", new Vector3(0.80f, 0.72f, 0.50f));
        terrain.AddLayer("Stone", new Vector3(0.50f, 0.50f, 0.52f));
        return terrain;
    }

    // A terrain on its own object, at the origin of the scene.
    private void AddTerrainObject()
    {
        var obj = new SceneObject { Name = "Terrain", Position = Vector3.Zero };
        obj.Components.Add(NewTerrain());
        _scene.Add(obj);
        _selectedId = obj.Id;
        _sculpting = false;
    }

    // Size, colors and collision, then the sculpting brush and the noise generator.
    private void DrawTerrainBody(SceneObject obj, Terrain terrain, bool editing)
    {
        var g = _gui;
        g.TextDisabled($"{terrain.Resolution} x {terrain.Resolution} points, centered on the object");
        Float("Width", terrain.Width, v => terrain.Width = v, editing, 0.5f);
        Float("Length", terrain.Length, v => terrain.Length = v, editing, 0.5f);
        Float("Height", terrain.Height, v => terrain.Height = v, editing, 0.1f);

        var resolutions = Terrain.Resolutions.Select(r => $"{r} x {r}").ToArray();
        int resolutionIndex = Math.Max(0, Array.IndexOf(Terrain.Resolutions, terrain.Resolution));
        if (g.Combo("Resolution", resolutionIndex, resolutions, out int pickedResolution) && editing)
            terrain.Resolution = Terrain.Resolutions[pickedResolution];
        g.TextDisabled("More points give finer detail and cost more to draw.");

        g.Separator();
        var baseColor = terrain.BaseColor;
        if (g.ColorEdit3("Base Color", ref baseColor) && editing) terrain.BaseColor = baseColor;
        g.TextDisabled("The color of ground with no paint on it.");

        DrawTerrainLayers(terrain, editing);
        g.Separator();

        Bool("Collide", terrain.Collide, v => terrain.Collide = v, editing);
        if (terrain.Collide)
        {
            float friction = terrain.Friction;
            if (g.DragFloat("Friction", ref friction, 0.01f, 0f, 2f) && editing) terrain.Friction = friction;
            float bounce = terrain.Bounciness;
            if (g.DragFloat("Bounciness", ref bounce, 0.01f, 0f, 1f) && editing) terrain.Bounciness = bounce;
        }

        if (!editing) return;

        g.Separator();
        g.TextDisabled("Sculpt");
        bool active = _sculpting && ReferenceEquals(_selected, obj);
        if (g.Button(active ? "Sculpting: ON (click to stop)##sculpt" : "Sculpt Terrain##sculpt", highlighted: active))
        {
            _sculpting = !active;
            _sculptTarget = obj.Id;
        }
        if (active)
            g.TextWrapped("Hold the left mouse button over the terrain to paint with the brush. Hold the right button and WASD to fly the camera.", Gui.DimColor);
        Choice("Brush", _brush, v => _brush = v, true);
        if (_brush == TerrainBrush.Paint)
            g.TextWrapped(terrain.Layers.Count == 0
                ? "Add a layer under Paint layers first."
                : $"Painting with {SafeLabel.Of(terrain.Layers[Math.Clamp(_paintLayer, 0, terrain.Layers.Count - 1)].Name, 24)}. Pick another layer in the list above.", Gui.DimColor);
        else if (_brush == TerrainBrush.Erase)
            g.TextWrapped("Erase removes paint, so the ground goes back to its base color.", Gui.DimColor);
        Float("Brush Size", _brushRadius, v => _brushRadius = MathF.Max(0.25f, v), true, 0.1f);
        Float("Strength", _brushStrength, v => _brushStrength = MathF.Max(0.05f, v), true, 0.05f);
        if (_brush == TerrainBrush.Flatten)
            Float("Flatten To", _flattenLevel, v => _flattenLevel = Math.Clamp(v, 0f, terrain.Height), true, 0.1f);

        g.Separator();
        g.TextDisabled("Generate");
        int seed = _genSeed;
        if (g.DragInt("Seed", ref seed, 0.2f) ) _genSeed = seed;
        Float("Hill Size", _genSize, v => _genSize = MathF.Max(1f, v), true, 0.5f);
        int octaves = _genOctaves;
        if (g.DragInt("Detail", ref octaves, 0.1f, 1, 8)) _genOctaves = octaves;
        float roughness = _genRoughness;
        if (g.DragFloat("Roughness", ref roughness, 0.01f, 0f, 1f)) _genRoughness = roughness;
        float amount = _genAmount;
        if (g.DragFloat("Amount", ref amount, 0.01f, 0f, 1f)) _genAmount = amount;
        if (g.Button("Generate Hills##gen"))
            terrain.Generate(_genSeed, _genSize, _genOctaves, _genRoughness, _genAmount);
        g.SameLine();
        if (g.Button("Random Seed##seed"))
        {
            _genSeed = Random.Shared.Next(1, 100000);
            terrain.Generate(_genSeed, _genSize, _genOctaves, _genRoughness, _genAmount);
        }
        if (g.Button("Flatten All##flat"))
            terrain.Flatten(_flattenLevel);
        g.SameLine();
        if (g.Button("Smooth All##smooth"))
            terrain.SmoothAll();
        g.TextDisabled("Generate replaces the whole ground; Undo (Ctrl+Z) brings it back.");
    }

    // The paint layers: each a named color. Pick one to paint with, rename it, change its color, or remove it.
    private void DrawTerrainLayers(Terrain terrain, bool editing)
    {
        var g = _gui;
        g.Separator();
        g.TextDisabled("Paint layers (pick one, then use the Paint brush)");
        if (terrain.Layers.Count == 0)
            g.TextWrapped("No layers yet. Add one, give it a color, then paint it onto the ground.", Gui.DimColor);
        _paintLayer = Math.Clamp(_paintLayer, 0, Math.Max(0, terrain.Layers.Count - 1));

        for (int i = 0; i < terrain.Layers.Count; i++)
        {
            var layer = terrain.Layers[i];
            g.PushId($"terrainLayer{i}");
            bool selected = i == _paintLayer;
            if (g.Button($"{(selected ? "> " : "")}{SafeLabel.Of(layer.Name, 24)}##pick", width: -34f, highlighted: selected) && editing)
            {
                _paintLayer = i;
                _brush = TerrainBrush.Paint;
            }
            g.SameLine();
            if (editing && g.Button("X##remove", width: 28f))
            {
                terrain.RemoveLayer(i);
                if (_paintLayer >= terrain.Layers.Count) _paintLayer = Math.Max(0, terrain.Layers.Count - 1);
                g.PopId();
                break;
            }
            string name = layer.Name;
            if (g.InputText("Name", ref name, 32) && editing) terrain.SetLayer(i, name, layer.Color);
            var color = layer.Color;
            if (g.ColorEdit3("Color", ref color) && editing) terrain.SetLayer(i, layer.Name, color);
            g.PopId();
        }

        if (editing)
        {
            if (terrain.Layers.Count < Terrain.MaxLayers && g.Button("Add Layer##addlayer"))
            {
                int index = terrain.AddLayer($"Layer {terrain.Layers.Count + 1}", new Vector3(0.6f, 0.45f, 0.3f));
                if (index >= 0) { _paintLayer = index; _brush = TerrainBrush.Paint; }
            }
            if (terrain.Layers.Count > 0)
            {
                if (terrain.Layers.Count < Terrain.MaxLayers) g.SameLine();
                if (g.Button("Fill With Selected##fill")) terrain.FillLayer(_paintLayer);
                g.SameLine();
                if (g.Button("Clear Paint##clearpaint")) terrain.ClearPaint();
            }
            if (terrain.Layers.Count >= Terrain.MaxLayers)
                g.TextDisabled($"At most {Terrain.MaxLayers} layers.");
        }
    }

    // The brush: aims at the terrain under the mouse, shows its size as a ring, and paints while the left button is held.
    // Returns true while sculpting is in charge of the mouse, so the transform gizmo stays out of the way.
    private bool UpdateTerrainSculpt(GlfwWindow window, Canvas2D draw, Matrix4x4 viewProj, float4 area, bool uiWantsMouse, float dt)
    {
        if (_sculpting && _selected?.Id != _sculptTarget) _sculpting = false;
        if (!_sculpting || _selected is null || _selected.Components.OfType<Terrain>().FirstOrDefault() is not { } terrain)
            return false;
        if (uiWantsMouse) return true;

        var cursor = window.CursorPos;
        var mouse = new float2((float)cursor.X, (float)cursor.Y);
        if (mouse.X < area.X || mouse.Y < area.Y || mouse.X > area.X + area.Z || mouse.Y > area.Y + area.W) return true;
        if (!Matrix4x4.Invert(viewProj, out var inverse) || !Matrix4x4.Invert(_selected.WorldMatrix, out var toLocal)) return true;

        float ndcX = (mouse.X - area.X) / area.Z * 2f - 1f;
        float ndcY = (mouse.Y - area.Y) / area.W * 2f - 1f;
        var nearH = Vector4.Transform(new Vector4(ndcX, ndcY, 0f, 1f), inverse);
        var farH = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), inverse);
        var rayStart = new Vector3(nearH.X, nearH.Y, nearH.Z) / nearH.W;
        var rayEnd = new Vector3(farH.X, farH.Y, farH.Z) / farH.W;
        var localStart = Vector3.Transform(rayStart, toLocal);
        var localDir = Vector3.TransformNormal(rayEnd - rayStart, toLocal);
        float reach = (terrain.Width + terrain.Length + terrain.Height) * 4f;
        if (localDir.LengthSquared() < 1e-12f || !terrain.Raycast(localStart, localDir, reach, out float t)) return true;

        var hit = localStart + Vector3.Normalize(localDir) * t;
        var world = _selected.WorldMatrix;
        var ringColor = new float4(1f, 0.85f, 0.2f, 1f);
        if (_brush == TerrainBrush.Paint && _paintLayer >= 0 && _paintLayer < terrain.Layers.Count)
        {
            var c = terrain.Layers[_paintLayer].Color;
            ringColor = new float4(MathF.Min(1f, c.X + 0.25f), MathF.Min(1f, c.Y + 0.25f), MathF.Min(1f, c.Z + 0.25f), 1f);
        }
        else if (_brush == TerrainBrush.Erase)
            ringColor = new float4(1f, 0.4f, 0.4f, 1f);
        const int segments = 48;
        Vector3 Ring(int i)
        {
            float a = i * MathF.Tau / segments;
            float x = hit.X + MathF.Cos(a) * _brushRadius, z = hit.Z + MathF.Sin(a) * _brushRadius;
            return Vector3.Transform(new Vector3(x, terrain.HeightAt(x, z) + 0.05f, z), world);
        }
        var identity = Matrix4x4.Identity;
        for (int i = 0; i < segments; i++)
            DrawWorldLine(draw, identity, viewProj, area, Ring(i), Ring(i + 1), ringColor, 2f);

        if (window.GetMouseButton(GlfwMouseButton.Left) == GlfwButtonAction.Press)
        {
            float amount = _brushStrength * dt;
            switch (_brush)
            {
                case TerrainBrush.Raise: terrain.Sculpt(_brush, hit.X, hit.Z, _brushRadius, amount); break;
                case TerrainBrush.Lower: terrain.Sculpt(_brush, hit.X, hit.Z, _brushRadius, amount); break;
                case TerrainBrush.Smooth: terrain.Sculpt(_brush, hit.X, hit.Z, _brushRadius, amount); break;
                case TerrainBrush.Paint: terrain.Sculpt(_brush, hit.X, hit.Z, _brushRadius, amount, layer: _paintLayer); break;
                case TerrainBrush.Erase: terrain.Sculpt(_brush, hit.X, hit.Z, _brushRadius, amount); break;
                default: terrain.Sculpt(_brush, hit.X, hit.Z, _brushRadius, amount * 2f, _flattenLevel); break;
            }
        }
        return true;
    }

    // ---- animation -------------------------------------------------------------------------

    // An Animator holds animations, each a clip from a model file with an id. Open the arrow on a model in the Assets
    // panel and drag one of its animations onto an entry's Clip field. Scripts play them by id: animator.Play("Walk").
    private void DrawAnimatorBody(SceneObject obj, Animator animator, bool editing)
    {
        var g = _gui;
        bool hasMesh = obj.Components.OfType<MeshRenderer>().Any() ||
                       obj.Children.Any(c => c.Components.OfType<MeshRenderer>().Any());
        if (!hasMesh)
            g.TextWrapped("Add a Mesh to this object: the Animator moves that model.", Gui.ErrorColor);

        for (int i = 0; i < animator.Animations.Count; i++)
        {
            var entry = animator.Animations[i];
            g.PushId($"animation{i}");
            string title = entry.Id.Length == 0 ? $"Animation {i + 1}" : SafeLabel.Of(entry.Id, 30);
            if (g.CollapsingHeader($"{title}##entry", defaultOpen: true))
            {
                string id = entry.Id;
                if (g.InputText("Id", ref id, 64) && editing) entry.Id = id;
                if (entry.Id.Length > 0 && animator.Animations.Count(a => string.Equals(a.Id, entry.Id, StringComparison.OrdinalIgnoreCase)) > 1)
                    g.TextWrapped("Another animation has this id. Scripts will play the first one.", Gui.ErrorColor);
                else if (entry.Id.Length == 0)
                    g.TextDisabled("Give it an id: scripts play it with Play(\"id\").");

                string label = entry.Clip.Length == 0
                    ? "None (drag an animation here)"
                    : SafeLabel.Of($"{entry.Clip}  ({Path.GetFileName(entry.Model)})", 70);
                g.Button($"{label}##clip", width: -60f);
                var field = g.LastItem;
                g.SameLine();
                if (g.Button("Clear##clip", width: 56f) && editing)
                {
                    entry.Model = "";
                    entry.Clip = "";
                }

                // Highlight the field while an animation is dragged over it; letting go assigns it.
                if (editing && _assetPanel.DraggedClip is { } dragged)
                {
                    bool over = g.Hovered(field);
                    g.C.Outline(field.X, field.Y, field.W, field.H, 2f, over ? Gui.AccentHover : Gui.DimColor);
                    if (over && g.MouseReleased)
                    {
                        entry.Model = dragged.Model;
                        entry.Clip = dragged.Clip;
                        if (entry.Id.Length == 0) entry.Id = UniqueAnimationId(animator, dragged.Clip);
                        _assetPanel.CancelDrag();
                    }
                }

                Bool("Play At Start", entry.PlayAtStart, v => entry.PlayAtStart = v, editing);
                Bool("Loop", entry.Loop, v => entry.Loop = v, editing);
                Float("Speed", entry.Speed, v => entry.Speed = v, editing);

                if (editing && entry.Clip.Length > 0 && entry.Id.Length > 0)
                {
                    bool previewing = animator.Previewing && animator.IsPlaying(entry.Id);
                    if (g.Button(previewing ? "Stop Preview##preview" : "Preview##preview"))
                    {
                        if (previewing) animator.StopPreview();
                        else animator.Preview(obj, entry.Id);
                    }
                    g.SameLine();
                }
                if (editing && g.Button("Remove##entry"))
                {
                    if (animator.Previewing) animator.StopPreview();
                    animator.Animations.RemoveAt(i);
                    g.PopId();
                    break;
                }
            }
            g.PopId();
        }

        if (editing && g.Button("Add Animation##add"))
            animator.Animations.Add(new AnimationEntry());
        float speed = animator.Speed;
        if (g.DragFloat("Speed (all)", ref speed, 0.05f) && editing) animator.Speed = speed;
        g.TextDisabled("Clips come from .glb / .gltf files with animations.");
    }

    // The clip's name as an id, with a number added when another animation already uses it.
    private static string UniqueAnimationId(Animator animator, string wanted)
    {
        string id = wanted;
        for (int n = 2; animator.Animations.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)); n++)
            id = $"{wanted} {n}";
        return id;
    }

    // ---- audio -----------------------------------------------------------------------------

    // The same settings as Unity's Audio Source. A sound dragged from the Assets panel can be dropped on the Audio Clip field.
    private void DrawAudioSourceBody(SceneObject obj, AudioSource src, bool editing, int index)
    {
        var g = _gui;

        string label = src.Clip.Length == 0 ? "None (drop a sound here)" : SafeLabel.Of(Path.GetFileName(src.Clip), 60);
        if (g.Button($"{label}##clip", width: -60f) && editing)
        {
            _audioTarget = (obj.Id, index);
            _browserTarget = null;
            _meshColliderTarget = null;
            _panoramaTarget = null;
            _browser.Open(_contentRoot, src.Clip, AudioClip.IsAudio, "Select Sound", "Audio");
        }
        var field = g.LastItem;
        g.SameLine();
        if (g.Button("Clear##clip", width: 56f) && editing)
            src.Clip = "";

        // Highlight the field while a sound is dragged over it; letting go assigns it.
        if (editing && _assetPanel.DraggedPath is { } dragged && AudioClip.IsAudio(dragged))
        {
            bool over = g.Hovered(field);
            g.C.Outline(field.X, field.Y, field.W, field.H, 2f, over ? Gui.AccentHover : Gui.DimColor);
            if (over && g.MouseReleased)
            {
                src.Clip = dragged;
                _assetPanel.CancelDrag();
            }
        }

        if (src.Clip.Length > 0)
        {
            var clip = AudioEngine.LoadClip(src.Clip);
            g.TextDisabled(clip is null
                ? "Could not load this sound (see the console)."
                : $"{clip.Length:F2} s, {clip.SampleRate} Hz, {(clip.Channels == 1 ? "mono" : "stereo")}");
        }

        Bool("Mute", src.Mute, v => src.Mute = v, editing);
        Bool("Play On Awake", src.PlayOnAwake, v => src.PlayOnAwake = v, editing);
        Bool("Loop", src.Loop, v => src.Loop = v, editing);

        int priority = src.Priority;
        if (g.DragInt("Priority", ref priority, 1f, 0, 256) && editing) src.Priority = priority;
        float volume = src.Volume;
        if (g.DragFloat("Volume", ref volume, 0.01f, 0f, 1f) && editing) src.Volume = volume;
        float pitch = src.Pitch;
        if (g.DragFloat("Pitch", ref pitch, 0.01f, -3f, 3f) && editing) src.Pitch = pitch;
        float pan = src.StereoPan;
        if (g.DragFloat("Stereo Pan", ref pan, 0.01f, -1f, 1f) && editing) src.StereoPan = pan;
        float blend = src.SpatialBlend;
        if (g.DragFloat("Spatial Blend", ref blend, 0.01f, 0f, 1f) && editing) src.SpatialBlend = blend;
        g.TextDisabled("Spatial Blend: 0 is 2D, 1 is 3D");

        if (g.CollapsingHeader("3D Sound Settings"))
        {
            float doppler = src.DopplerLevel;
            if (g.DragFloat("Doppler Level", ref doppler, 0.01f, 0f, 5f) && editing) src.DopplerLevel = doppler;
            float spread = src.Spread;
            if (g.DragFloat("Spread", ref spread, 1f, 0f, 360f) && editing) src.Spread = spread;
            Choice("Volume Rolloff", src.RolloffMode, v => src.RolloffMode = v, editing);
            float min = src.MinDistance;
            if (g.DragFloat("Min Distance", ref min, 0.05f, 0f, 1_000_000f) && editing)
            {
                src.MinDistance = min;
                if (src.MaxDistance < min) src.MaxDistance = min;
            }
            float max = src.MaxDistance;
            if (g.DragFloat("Max Distance", ref max, 1f, 0.01f, 1_000_000f) && editing)
                src.MaxDistance = MathF.Max(max, src.MinDistance);
        }

        if (src.Clip.Length == 0) return;
        if (editing)
        {
            // Edit mode has no scene playing, so this plays the clip as a plain sound to hear it.
            if (g.Button(AudioEngine.IsPreviewing ? "Stop Preview" : "Preview"))
            {
                if (AudioEngine.IsPreviewing) AudioEngine.StopPreview();
                else AudioEngine.PlayPreview(src.Clip, src.Volume, src.Pitch);
            }
        }
        else
        {
            if (g.Button("Play")) src.Play();
            g.SameLine();
            if (g.Button(src.IsPlaying ? "Pause" : "Resume"))
            {
                if (src.IsPlaying) src.Pause();
                else src.UnPause();
            }
            g.SameLine();
            if (g.Button("Stop")) src.Stop();
            g.TextDisabled($"{(src.IsPlaying ? "Playing" : "Not playing")}   {src.Time:F1} / {src.ClipLength:F1} s");
        }
    }

    private void DrawAudioListenerBody()
    {
        _gui.TextWrapped("3D sounds are heard from this object's position and direction. Put it on the camera. " +
                         "Without one, the scene is heard from its first camera.", Gui.DimColor);
    }

    // ---- script fields -----------------------------------------------------------------

    // The script's file, then one editor per public field. Values typed here are saved with the scene and copied into
    // the script when play starts; untouched fields keep the default from the script's code.
    private void DrawScriptBody(SceneObject obj, ScriptComponent script, bool editing, int index)
    {
        var g = _gui;
        var found = _scriptFields.Find(script.ScriptName);
        if (found?.Package is { } package)
            g.TextDisabled($"From the {package} package");
        else if (found is { IsComponent: true } && !File.Exists(Path.Combine(_contentRoot, "Scripts", script.ScriptName + ".cs")))
            g.TextDisabled("A component from the project's scripts");
        else
        {
            g.TextDisabled("Scripts/" + SafeLabel.Of(script.ScriptName, 80) + ".cs");
            if (g.Button("Edit Code"))
            {
                _code.OpenScript(script.ScriptName);
                _codeMode = true;
                _vcMode = false;
            }
        }
        if (found is null && _scriptFields.For(script.ScriptName) is not null)
            g.TextWrapped($"No script or enabled package has a class named {SafeLabel.Of(script.ScriptName, 40)}. " +
                          "If it came from a package, install it in File > Package Manager.", Gui.ErrorColor);

        var fields = _scriptFields.For(script.ScriptName);
        if (fields is null)
        {
            g.TextDisabled("Reading the script's fields...");
            return;
        }
        if (_scriptFields.Errors.Length > 0)
            g.TextWrapped("The scripts have compile errors, so these fields may be out of date.", Gui.ErrorColor);
        if (fields.Count == 0)
        {
            g.TextDisabled("No public fields.");
            return;
        }

        foreach (var field in fields)
        {
            bool overridden = script.Fields.TryGetValue(field.Name, out var stored);
            var node = ParseJson(overridden ? stored! : field.DefaultJson) ?? ParseJson(field.DefaultJson);
            string label = NiceName(field.Name);
            string key = $"{obj.Id:N}:{index}:{field.Name}";

            string? edited = field.IsCollection
                ? DrawCollectionField(label, key, field.Kind, node as JsonArray, editing, field.RequiredUi)
                : field.Kind == ScriptFieldKind.Object
                ? DrawObjectField(label, key, node, editing, field.RequiredUi)
                : field.Kind == ScriptFieldKind.Enum
                ? DrawEnumField(label, key, field, node)
                : DrawValueField(label, key, field.Kind, node);
            if (edited is not null && editing)
                script.Fields[field.Name] = edited;

            // Right-click a field to go back to the script's default.
            if (overridden && editing && g.BeginContextMenu($"reset:{key}", forLastItem: true))
            {
                if (g.MenuItem("Reset to script default"))
                    script.Fields.Remove(field.Name);
                g.EndPopup();
            }
        }
    }

    // A SceneObject field, or a UI control field (Button, TextField...) when required is set: the object's name, or None.
    // Drag an object from the Hierarchy onto it to set it; a control field only takes objects with a UI element of that
    // kind. X clears it. Returns the new JSON (the object's id, or null) when it changed.
    private string? DrawObjectField(string label, string key, JsonNode? node, bool editing, UiKind? required = null)
    {
        var g = _gui;
        var id = ScriptFields.ObjectId(node?.ToJsonString());
        var target = id is { } guid ? _scene.Find(guid) : null;
        bool Fits(SceneObject obj) => required is not { } kind || obj.Components.OfType<UiElement>().Any(e => e.Kind == kind);
        string what = required is { } k ? $"a UI {k}" : "an object";
        string shown = target is not null && !Fits(target) ? $"{SafeLabel.Of(target.Name, 24)} (has no UI {required})"
            : target is not null ? SafeLabel.Of(target.Name, 40)
            : id is not null ? "Missing (the object was deleted)"
            : $"None (drag {what} here)";

        g.TextDisabled(label);
        g.Button($"{shown}##obj{key}", width: -34f);
        var field = g.LastItem;
        string? result = null;
        g.SameLine();
        if (g.Button($"X##clear{key}", width: 28f) && editing && id is not null)
            result = "null";

        // While an object that fits is dragged from the Hierarchy, the field is outlined; letting go over it sets it.
        if (editing && (_hierDragId ?? _hierReleasedId) is { } dragged && _scene.Find(dragged) is { } draggedObject &&
            Fits(draggedObject))
        {
            bool over = g.Hovered(field);
            g.C.Outline(field.X, field.Y, field.W, field.H, 2f, over ? Gui.AccentHover : Gui.DimColor);
            if (over && _hierReleasedId is not null)
            {
                result = ScriptFields.ObjectJson(draggedObject);
                _hierReleasedId = null;
            }
        }
        return result;
    }

    // An enum field: a dropdown of its values, labelled by their [Description] when they have one.
    private string? DrawEnumField(string label, string key, ScriptFieldInfo field, JsonNode? node)
    {
        string current = node is JsonValue v && v.TryGetValue<string>(out var name) ? name : field.Options.FirstOrDefault() ?? "";
        int index = Math.Max(0, field.Options.ToList().IndexOf(current));
        if (field.OptionLabels.Count == 0) return null;
        if (!_gui.Combo($"{label}##{key}", index, field.OptionLabels, out int picked) || picked == index) return null;
        return JsonSerializer.Serialize(field.Options[picked]);
    }

    // One value. Returns its new JSON text when it was edited this frame, otherwise null.
    private string? DrawValueField(string label, string key, ScriptFieldKind kind, JsonNode? node)
    {
        var g = _gui;
        string id = $"{label}##{key}";
        switch (kind)
        {
            case ScriptFieldKind.Float:
            case ScriptFieldKind.Double:
            {
                float value = (float)Number(node);
                if (!g.DragFloat(id, ref value, 0.05f)) return null;
                return kind == ScriptFieldKind.Float
                    ? JsonSerializer.Serialize(value, ScriptFields.Json)
                    : JsonSerializer.Serialize((double)value, ScriptFields.Json);
            }
            case ScriptFieldKind.Int:
            case ScriptFieldKind.Long:
            {
                int value = (int)Math.Clamp(Math.Round(Number(node)), int.MinValue, int.MaxValue);
                if (!g.DragInt(id, ref value, 0.2f)) return null;
                return JsonSerializer.Serialize(value);
            }
            case ScriptFieldKind.Vector3:
            {
                var array = node as JsonArray;
                float Part(int i) => array is not null && i < array.Count ? (float)Number(array[i]) : 0f;
                var vector = new Vector3(Part(0), Part(1), Part(2));
                bool changed;
                if (label.EndsWith("Color", StringComparison.Ordinal))
                    changed = g.ColorEdit3(id, ref vector);
                else
                {
                    var field = new float3(vector.X, vector.Y, vector.Z);
                    changed = g.DragFloat3(id, ref field, 0.05f);
                    vector = new Vector3(field.X, field.Y, field.Z);
                }
                return changed ? JsonSerializer.Serialize(vector, ScriptFields.Json) : null;
            }
            case ScriptFieldKind.Bool:
            {
                bool value = node is JsonValue v && v.TryGetValue<bool>(out var b) && b;
                if (!g.Checkbox(id, ref value)) return null;
                return value ? "true" : "false";
            }
            default:
            {
                string text = node is JsonValue v && v.TryGetValue<string>(out var str) ? str : "";
                if (!g.InputText(id, ref text, 512)) return null;
                return JsonSerializer.Serialize(text);
            }
        }
    }

    // An array or list: a size box, then one editor per element. Returns the new JSON array when anything changed.
    private string? DrawCollectionField(string label, string key, ScriptFieldKind kind, JsonArray? array, bool editing = true,
        UiKind? required = null)
    {
        var g = _gui;
        var items = array?.Select(n => n?.DeepClone()).ToList() ?? new List<JsonNode?>();
        bool changed = false;

        g.TextDisabled($"{label} ({items.Count})");
        g.Indent(12f);
        int size = items.Count;
        if (g.DragInt($"Size##{key}", ref size, 0.1f, 0, 1000))
        {
            while (items.Count > size) items.RemoveAt(items.Count - 1);
            while (items.Count < size) items.Add(NewElement(kind, items));
            changed = true;
        }
        int remove = -1;
        for (int i = 0; i < items.Count; i++)
        {
            string? element = kind == ScriptFieldKind.Object
                ? DrawObjectField($"[{i}]", $"{key}:{i}", items[i], editing, required)
                : DrawValueField($"[{i}]", $"{key}:{i}", kind, items[i]);
            if (element is not null)
            {
                items[i] = ParseJson(element);
                changed = true;
            }
            // Right-click an entry to take it out of the list.
            if (editing && g.BeginContextMenu($"remove:{key}:{i}", forLastItem: true))
            {
                if (g.MenuItem("Remove from list")) remove = i;
                g.EndPopup();
            }
        }
        if (remove >= 0)
        {
            items.RemoveAt(remove);
            changed = true;
        }

        if (editing)
        {
            if (g.Button($"+ Add##add{key}", width: kind == ScriptFieldKind.Object ? -1f : 0f))
            {
                items.Add(NewElement(kind, items));
                changed = true;
            }
            // An object dragged from the Hierarchy and let go on Add goes onto the end of the list.
            if (kind == ScriptFieldKind.Object && (_hierDragId ?? _hierReleasedId) is { } dragged && _scene.Find(dragged) is { } draggedObject &&
                (required is not { } need || draggedObject.Components.OfType<UiElement>().Any(e => e.Kind == need)))
            {
                var add = g.LastItem;
                bool over = g.Hovered(add);
                g.C.Outline(add.X, add.Y, add.W, add.H, 2f, over ? Gui.AccentHover : Gui.DimColor);
                if (over && _hierReleasedId is not null)
                {
                    items.Add(ParseJson(ScriptFields.ObjectJson(draggedObject)));
                    _hierReleasedId = null;
                    changed = true;
                }
            }
            if (items.Count > 0)
            {
                g.SameLine();
                if (kind != ScriptFieldKind.Object && g.Button($"- Remove Last##removelast{key}"))
                {
                    items.RemoveAt(items.Count - 1);
                    changed = true;
                }
            }
        }
        g.Unindent(12f);

        return changed ? new JsonArray(items.ToArray()).ToJsonString() : null;
    }

    // A new entry for a list: a copy of the last one (handy for numbers and names), or an empty one for objects.
    private static JsonNode? NewElement(ScriptFieldKind kind, List<JsonNode?> items) =>
        kind != ScriptFieldKind.Object && items.Count > 0 ? items[^1]?.DeepClone() : EmptyElement(kind);

    private static JsonNode? EmptyElement(ScriptFieldKind kind) => kind switch
    {
        ScriptFieldKind.Bool => JsonValue.Create(false),
        ScriptFieldKind.String => JsonValue.Create(""),
        ScriptFieldKind.Vector3 => new JsonArray(0, 0, 0),
        ScriptFieldKind.Object => null,
        _ => JsonValue.Create(0),
    };

    private static double Number(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, out d)) return d;   // "Infinity", "NaN"
        return 0;
    }

    private static JsonNode? ParseJson(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    // "jumpHeight" -> "Jump Height", "_speed" -> "Speed", like Unity's Inspector.
    private static string NiceName(string name)
    {
        name = name.TrimStart('_');
        if (name.StartsWith("m_")) name = name[2..];
        if (name.Length == 0) return "Field";
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
        }
        return sb.ToString();
    }

    // ---- particle systems --------------------------------------------------------------

    // Playback controls, then one collapsible section per module, laid out like Unity's.
    private void DrawParticleSystemBody(ParticleSystem ps, bool editing)
    {
        var g = _gui;
        if (g.Button(ps.IsPlaying && !ps.IsPaused ? "Pause" : "Play"))
        {
            if (ps.IsPlaying && !ps.IsPaused) ps.Pause();
            else ps.Play();
        }
        g.SameLine();
        if (g.Button("Restart")) ps.Restart();
        g.SameLine();
        if (g.Button("Stop")) ps.Stop(clear: true);
        g.TextDisabled($"Particles: {ps.ParticleCount}   Time: {ps.Time:F1}s");

        var match = ParticlePresets.Find(ps);
        int current = match is null ? 0 : Array.IndexOf(ParticlePresetNames, match.Name);
        if (g.Combo("Preset", current, ParticlePresetNames, out int picked) && editing && picked > 0)
            ParticlePresets.All[picked - 1].Apply(ps);
        g.TextWrapped(match?.Description ?? "Custom settings. Pick a preset to replace them.", Gui.DimColor);

        if (g.CollapsingHeader("Main", defaultOpen: true))
        {
            var m = ps.Main;
            Float("Duration", m.Duration, v => m.Duration = MathF.Max(0.05f, v), editing);
            Bool("Looping", m.Looping, v => m.Looping = v, editing);
            Bool("Prewarm", m.Prewarm, v => m.Prewarm = v, editing);
            Float("Start Delay", m.StartDelay, v => m.StartDelay = MathF.Max(0f, v), editing);
            Range("Start Lifetime", m.StartLifetime, editing, min: 0.01f);
            Range("Start Speed", m.StartSpeed, editing);
            Range("Start Size", m.StartSize, editing, min: 0f);
            Range("Start Rotation", m.StartRotation, editing, speed: 1f);
            Color("Start Color", m.StartColor, v => m.StartColor = v, editing);
            Bool("Random Between Two Colors", m.RandomBetweenTwoColors, v => m.RandomBetweenTwoColors = v, editing);
            if (m.RandomBetweenTwoColors)
                Color("Start Color 2", m.StartColor2, v => m.StartColor2 = v, editing);
            Float("Gravity Modifier", m.GravityModifier, v => m.GravityModifier = v, editing);
            Choice("Simulation Space", m.SimulationSpace, v => m.SimulationSpace = v, editing);
            Float("Simulation Speed", m.SimulationSpeed, v => m.SimulationSpeed = MathF.Max(0f, v), editing);
            Bool("Play On Awake", m.PlayOnAwake, v => m.PlayOnAwake = v, editing);
            Int("Max Particles", m.MaxParticles, v => m.MaxParticles = Math.Clamp(v, 0, 100000), editing);
        }

        if (Module("Emission", ps.Emission.Enabled, v => ps.Emission.Enabled = v, editing))
        {
            var e = ps.Emission;
            Float("Rate over Time", e.RateOverTime, v => e.RateOverTime = MathF.Max(0f, v), editing, speed: 0.5f);
            for (int i = 0; i < e.Bursts.Count; i++)
            {
                var burst = e.Bursts[i];
                g.PushId(i);
                g.TextDisabled($"Burst {i + 1}");
                g.SameLine();
                if (editing && g.Button("Remove"))
                {
                    e.Bursts.RemoveAt(i);
                    g.PopId();
                    break;
                }
                Float("Time", burst.Time, v => burst.Time = MathF.Max(0f, v), editing);
                Int("Count", burst.Count, v => burst.Count = Math.Max(0, v), editing);
                Int("Cycles", burst.Cycles, v => burst.Cycles = Math.Max(1, v), editing);
                Float("Interval", burst.Interval, v => burst.Interval = MathF.Max(0f, v), editing);
                g.PopId();
            }
            if (editing && g.Button("Add Burst"))
                e.Bursts.Add(new ParticleBurst());
        }

        if (Module("Shape", ps.Shape.Enabled, v => ps.Shape.Enabled = v, editing))
        {
            var s = ps.Shape;
            Choice("Shape", s.Shape, v => s.Shape = v, editing);
            if (s.Shape == ParticleShapeType.Cone)
                Float("Angle", s.Angle, v => s.Angle = Math.Clamp(v, 0f, 90f), editing, speed: 0.5f);
            if (s.Shape is ParticleShapeType.Cone or ParticleShapeType.Sphere or ParticleShapeType.Hemisphere or ParticleShapeType.Circle)
                Float("Radius", s.Radius, v => s.Radius = MathF.Max(0f, v), editing);
            if (s.Shape is ParticleShapeType.Sphere or ParticleShapeType.Hemisphere)
                Bool("Emit from Shell", s.EmitFromShell, v => s.EmitFromShell = v, editing);
            if (s.Shape == ParticleShapeType.Box)
                Vec3("Box Size", s.BoxSize, v => s.BoxSize = Vector3.Max(v, Vector3.Zero), editing);
            Vec3("Position", s.Position, v => s.Position = v, editing);
            Float("Randomize Direction", s.RandomizeDirection, v => s.RandomizeDirection = Math.Clamp(v, 0f, 1f), editing, speed: 0.01f);
        }

        if (Module("Velocity over Lifetime", ps.VelocityOverLifetime.Enabled, v => ps.VelocityOverLifetime.Enabled = v, editing))
            Vec3("Linear", ps.VelocityOverLifetime.Linear, v => ps.VelocityOverLifetime.Linear = v, editing);

        if (Module("Limit Velocity over Lifetime", ps.LimitVelocityOverLifetime.Enabled, v => ps.LimitVelocityOverLifetime.Enabled = v, editing))
        {
            var l = ps.LimitVelocityOverLifetime;
            Float("Drag", l.Drag, v => l.Drag = MathF.Max(0f, v), editing);
            Float("Speed Limit (0 = none)", l.SpeedLimit, v => l.SpeedLimit = MathF.Max(0f, v), editing);
        }

        if (Module("Color over Lifetime", ps.ColorOverLifetime.Enabled, v => ps.ColorOverLifetime.Enabled = v, editing))
        {
            Color("Start##color", ps.ColorOverLifetime.StartColor, v => ps.ColorOverLifetime.StartColor = v, editing);
            Color("End##color", ps.ColorOverLifetime.EndColor, v => ps.ColorOverLifetime.EndColor = v, editing);
        }

        if (Module("Size over Lifetime", ps.SizeOverLifetime.Enabled, v => ps.SizeOverLifetime.Enabled = v, editing))
        {
            Float("Start##size", ps.SizeOverLifetime.StartSize, v => ps.SizeOverLifetime.StartSize = MathF.Max(0f, v), editing, speed: 0.01f);
            Float("End##size", ps.SizeOverLifetime.EndSize, v => ps.SizeOverLifetime.EndSize = MathF.Max(0f, v), editing, speed: 0.01f);
        }

        if (Module("Rotation over Lifetime", ps.RotationOverLifetime.Enabled, v => ps.RotationOverLifetime.Enabled = v, editing))
            Range("Angular Velocity", ps.RotationOverLifetime.AngularVelocity, editing, speed: 1f);

        if (Module("Collision", ps.Collision.Enabled, v => ps.Collision.Enabled = v, editing))
        {
            var c = ps.Collision;
            Float("Ground Height", c.GroundHeight, v => c.GroundHeight = v, editing);
            Float("Bounce", c.Bounce, v => c.Bounce = Math.Clamp(v, 0f, 1f), editing, speed: 0.01f);
            Float("Dampen", c.Dampen, v => c.Dampen = Math.Clamp(v, 0f, 1f), editing, speed: 0.01f);
            Float("Lifetime Loss", c.LifetimeLoss, v => c.LifetimeLoss = Math.Clamp(v, 0f, 1f), editing, speed: 0.01f);
        }

        if (g.CollapsingHeader("Renderer"))
        {
            var r = ps.Renderer;
            Choice("Blend", r.Blend, v => r.Blend = v, editing);
            Choice("Sprite", r.Sprite, v => r.Sprite = v, editing);
            if (r.Blend == ParticleBlend.Alpha)
                Bool("Sort by Distance", r.SortByDistance, v => r.SortByDistance = v, editing);
        }
    }

    // A module header with its on/off box inside, like Unity's. Returns true when the section is open.
    private bool Module(string title, bool enabled, Action<bool> setEnabled, bool editing)
    {
        if (!_gui.CollapsingHeader($"{title}{(enabled ? "" : " (off)")}##{title}"))
            return false;
        Bool($"Enabled##{title}", enabled, setEnabled, editing);
        return enabled;
    }

    private void Float(string label, float value, Action<float> set, bool editing, float speed = 0.05f)
    {
        if (_gui.DragFloat(label, ref value, speed) && editing) set(value);
    }

    private void Int(string label, int value, Action<int> set, bool editing)
    {
        if (_gui.DragInt(label, ref value, 1f) && editing) set(value);
    }

    private void Bool(string label, bool value, Action<bool> set, bool editing)
    {
        if (_gui.Checkbox(label, ref value) && editing) set(value);
    }

    // Min and max side by side (left is min); set both the same for a constant.
    private void Range(string label, MinMax range, bool editing, float speed = 0.05f, float min = float.MinValue)
    {
        var v = new float2(range.Min, range.Max);
        if (_gui.DragFloat2(label, ref v, speed) && editing)
        {
            range.Min = MathF.Max(min, v.X);
            range.Max = MathF.Max(range.Min, v.Y);
        }
    }

    private void Vec3(string label, Vector3 value, Action<Vector3> set, bool editing)
    {
        var v = new float3(value.X, value.Y, value.Z);
        if (_gui.DragFloat3(label, ref v, 0.05f) && editing) set(new Vector3(v.X, v.Y, v.Z));
    }

    private void Color(string label, Vector4 value, Action<Vector4> set, bool editing)
    {
        if (_gui.ColorEdit(label, ref value) && editing) set(value);
    }

    private void Choice<T>(string label, T value, Action<T> set, bool editing) where T : struct, Enum
    {
        if (_gui.Combo(label, ref value) && editing) set(value);
    }

    // The selected particle system's emitter shape, drawn in orange in the edit view.
    private void DrawParticleShapeGizmos(Canvas2D draw, Matrix4x4 viewProj, float4 viewport)
    {
        if (_selected?.Components.OfType<ParticleSystem>().FirstOrDefault() is not { Shape.Enabled: true } ps) return;
        float2 display = ImGui.GetIO().DisplaySize;
        var size = new float4(viewport.X * display.X, viewport.Y * display.Y, viewport.Z * display.X, viewport.W * display.Y);
        var world = Matrix4x4.CreateTranslation(ps.Shape.Position) * _selected.WorldMatrix;
        var color = new float4(1f, 0.6f, 0.15f, 0.9f);
        var s = ps.Shape;
        const int segments = 32;

        void Circle(float radius, float height)
        {
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * MathF.Tau / segments, a1 = (i + 1) * MathF.Tau / segments;
                DrawWorldLine(draw, world, viewProj, size,
                    new Vector3(MathF.Cos(a0) * radius, height, MathF.Sin(a0) * radius),
                    new Vector3(MathF.Cos(a1) * radius, height, MathF.Sin(a1) * radius), color, 1.5f);
            }
        }

        switch (s.Shape)
        {
            case ParticleShapeType.Cone:
            {
                // The base disc, and where particles from its rim would be one unit up.
                float spread = Math.Clamp(s.Angle, 0f, 90f) * MathF.PI / 180f;
                float top = s.Radius + MathF.Tan(MathF.Min(spread, 1.5f)) * 1f;
                Circle(s.Radius, 0f);
                Circle(top, 1f);
                for (int i = 0; i < 4; i++)
                {
                    float a = i * MathF.PI / 2f;
                    var dir = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a));
                    DrawWorldLine(draw, world, viewProj, size, dir * s.Radius, dir * top + Vector3.UnitY, color, 1.5f);
                }
                break;
            }
            case ParticleShapeType.Sphere:
            case ParticleShapeType.Hemisphere:
            {
                var ball = Matrix4x4.CreateScale(s.Radius) * world;
                bool half = s.Shape == ParticleShapeType.Hemisphere;
                for (int ring = 0; ring < 3; ring++)
                    for (int i = 0; i < segments; i++)
                    {
                        float a0 = i * MathF.Tau / segments, a1 = (i + 1) * MathF.Tau / segments;
                        var p0 = Ring(ring, a0);
                        var p1 = Ring(ring, a1);
                        if (half && (p0.Y < -1e-4f || p1.Y < -1e-4f)) continue;
                        DrawWorldLine(draw, ball, viewProj, size, p0, p1, color, 1.5f);
                    }
                break;
            }
            case ParticleShapeType.Box:
            {
                var h = s.BoxSize / 2f;
                for (int i = 0; i < 8; i++)
                    foreach (int bit in new[] { 1, 2, 4 })
                        if ((i & bit) == 0)
                            DrawWorldLine(draw, world, viewProj, size, Corner(i, h), Corner(i | bit, h), color, 1.5f);
                break;
            }
            case ParticleShapeType.Circle:
                Circle(s.Radius, 0f);
                break;
        }
        // The emit direction.
        DrawWorldLine(draw, world, viewProj, size, Vector3.Zero, Vector3.UnitY * 1.5f, color, 2f);

        static Vector3 Corner(int i, Vector3 h) =>
            new((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z);
    }

    // ---- physics components ----------------------------------------------------------

    private void DrawRigidbodyBody(Rigidbody rb, bool editing)
    {
        var g = _gui;
        float mass = rb.Mass;
        if (g.DragFloat("Mass", ref mass, 0.05f, 0.001f, 100000f) && editing)
            rb.Mass = MathF.Max(0.001f, mass);
        bool gravity = rb.UseGravity;
        if (g.Checkbox("Use Gravity", ref gravity) && editing)
            rb.UseGravity = gravity;
        bool kinematic = rb.IsKinematic;
        if (g.Checkbox("Kinematic", ref kinematic) && editing)
            rb.IsKinematic = kinematic;
        bool freeze = rb.FreezeRotation;
        if (g.Checkbox("Freeze Rotation", ref freeze) && editing)
            rb.FreezeRotation = freeze;
        float drag = rb.Drag;
        if (g.DragFloat("Drag", ref drag, 0.01f, 0f, 100f) && editing)
            rb.Drag = MathF.Max(0f, drag);
        float angularDrag = rb.AngularDrag;
        if (g.DragFloat("Angular Drag", ref angularDrag, 0.01f, 0f, 100f) && editing)
            rb.AngularDrag = MathF.Max(0f, angularDrag);
    }

    private void DrawBoxColliderBody(BoxCollider box, bool editing)
    {
        var g = _gui;
        var center = new float3(box.Center.X, box.Center.Y, box.Center.Z);
        if (g.DragFloat3("Center", ref center, 0.01f) && editing)
            box.Center = new Vector3(center.X, center.Y, center.Z);
        var size = new float3(box.Size.X, box.Size.Y, box.Size.Z);
        if (g.DragFloat3("Size", ref size, 0.01f, 0.001f, 10000f) && editing)
            box.Size = Vector3.Max(new Vector3(size.X, size.Y, size.Z), new Vector3(0.001f));
        DrawColliderMaterial(box, editing);
    }

    private void DrawSphereColliderBody(SphereCollider sphere, bool editing)
    {
        var g = _gui;
        var center = new float3(sphere.Center.X, sphere.Center.Y, sphere.Center.Z);
        if (g.DragFloat3("Center", ref center, 0.01f) && editing)
            sphere.Center = new Vector3(center.X, center.Y, center.Z);
        float radius = sphere.Radius;
        if (g.DragFloat("Radius", ref radius, 0.01f, 0.001f, 10000f) && editing)
            sphere.Radius = MathF.Max(0.001f, radius);
        DrawColliderMaterial(sphere, editing);
    }

    private void DrawMeshColliderBody(SceneObject obj, MeshCollider mesh, bool editing, int index)
    {
        var g = _gui;
        string? model = MeshColliderModel(obj, mesh);
        g.TextWrapped(model is null
            ? "No model: add a Mesh component"
            : "Shape: " + SafeLabel.Of(Path.GetFileName(model), 60) + (mesh.Model.Length == 0 ? " (from Mesh)" : ""), Gui.DimColor);

        // Pick a different model for the collision shape, e.g. a simplified version of the visible one.
        if (g.Button("Choose collision model") && editing)
        {
            _meshColliderTarget = (obj.Id, index);
            _panoramaTarget = null;
            _audioTarget = null;
            _browser.Open(_contentRoot, model ?? "");
        }
        if (mesh.Model.Length > 0)
        {
            g.SameLine();
            if (g.Button("Use Mesh") && editing)
                mesh.Model = "";
        }

        var center = new float3(mesh.Center.X, mesh.Center.Y, mesh.Center.Z);
        if (g.DragFloat3("Offset", ref center, 0.01f) && editing)
            mesh.Center = new Vector3(center.X, center.Y, center.Z);
        DrawColliderMaterial(mesh, editing);

        if (MovesWithPhysics(obj))
            g.TextWrapped("On a moving rigidbody this collides as the mesh's convex hull (shrink-wrapped), so hollows are filled in.", Gui.DimColor);
    }

    // True when the object, or the parent whose rigidbody it belongs to, moves under physics.
    private static bool MovesWithPhysics(SceneObject obj)
    {
        for (var o = obj; o is not null; o = o.Parent)
            if (o.Components.OfType<Rigidbody>().FirstOrDefault() is { } rb)
                return !rb.IsKinematic;
        return false;
    }

    // The model a mesh collider uses: its own, or the object's Mesh component's.
    private static string? MeshColliderModel(SceneObject obj, MeshCollider mesh)
    {
        string path = mesh.Model.Length > 0 ? mesh.Model : obj.Components.OfType<MeshRenderer>().FirstOrDefault()?.Model ?? "";
        return path.Length > 0 ? path : null;
    }

    private void DrawColliderMaterial(Collider collider, bool editing)
    {
        var g = _gui;
        float friction = collider.Friction;
        if (g.DragFloat("Friction", ref friction, 0.01f, 0f, 2f) && editing)
            collider.Friction = Math.Clamp(friction, 0f, 2f);
        float bounciness = collider.Bounciness;
        if (g.DragFloat("Bounciness", ref bounciness, 0.01f, 0f, 1f) && editing)
            collider.Bounciness = Math.Clamp(bounciness, 0f, 1f);
    }

    // A new collider is fitted to the object's model when it has one, otherwise it is a unit shape.
    private void AddCollider(SceneObject obj, bool sphere)
    {
        Vector3? min = null, max = null;
        if (obj.Components.OfType<MeshRenderer>().FirstOrDefault() is { Model.Length: > 0 } mr)
        {
            try
            {
                var model = _assets.GetModel(mr.Model);
                if (model.BoundsMax.X >= model.BoundsMin.X)
                {
                    min = model.BoundsMin;
                    max = model.BoundsMax;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not read {mr.Model} to fit the collider: {ex.Message}");
            }
        }

        var center = min is { } lo && max is { } hi ? (lo + hi) / 2f : Vector3.Zero;
        var size = min is { } lo2 && max is { } hi2 ? Vector3.Max(hi2 - lo2, new Vector3(0.001f)) : Vector3.One;
        if (sphere)
            obj.Components.Add(new SphereCollider { Center = center, Radius = MathF.Max(size.X, MathF.Max(size.Y, size.Z)) / 2f });
        else
            obj.Components.Add(new BoxCollider { Center = center, Size = size });
    }

    // Collider outlines in the edit view: green, brighter on the selected object.
    private void DrawColliderGizmos(Canvas2D draw, Matrix4x4 viewProj, float4 viewport)
    {
        float2 display = ImGui.GetIO().DisplaySize;
        float4 size = new float4(viewport.X * display.X, viewport.Y * display.Y,
                                 viewport.Z * display.X, viewport.W * display.Y);

        foreach (var obj in _scene.All)
        {
            bool selected = obj == _selected;
            var color = selected ? new float4(0.4f, 1f, 0.5f, 1f) : new float4(0.3f, 0.8f, 0.4f, 0.6f);
            float thickness = selected ? 1.75f : 1.1f;
            var world = obj.WorldMatrix;

            foreach (var collider in obj.Components.OfType<Collider>())
            {
                if (collider is BoxCollider box)
                {
                    var h = box.Size / 2f;
                    var c = box.Center;
                    var corners = new Vector3[8];
                    for (int i = 0; i < 8; i++)
                        corners[i] = c + new Vector3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z);
                    for (int i = 0; i < 8; i++)
                        foreach (int bit in new[] { 1, 2, 4 })
                            if ((i & bit) == 0)
                                DrawWorldLine(draw, world, viewProj, size, corners[i], corners[i | bit], color, thickness);
                }
                else if (collider is MeshCollider mesh && MeshColliderModel(obj, mesh) is { } path)
                {
                    var meshWorld = Matrix4x4.CreateTranslation(mesh.Center) * world;
                    if (MovesWithPhysics(obj))
                        DrawHullWireframe(draw, path, meshWorld, viewProj, size, color, thickness);
                    else
                        DrawMeshWireframe(draw, path, meshWorld, viewProj, size, color, thickness);
                }
                else if (collider is SphereCollider sphere &&
                         Matrix4x4.Decompose(world, out var scale, out var rotation, out _))
                {
                    // Matches the physics: the radius scales with the largest axis, and the ball turns with the object.
                    float r = sphere.Radius * MathF.Max(MathF.Abs(scale.X), MathF.Max(MathF.Abs(scale.Y), MathF.Abs(scale.Z)));
                    var ball = Matrix4x4.CreateScale(r) * Matrix4x4.CreateFromQuaternion(rotation) *
                               Matrix4x4.CreateTranslation(Vector3.Transform(sphere.Center, world));
                    const int segments = 32;
                    for (int ring = 0; ring < 3; ring++)
                        for (int i = 0; i < segments; i++)
                        {
                            float a0 = i * MathF.Tau / segments, a1 = (i + 1) * MathF.Tau / segments;
                            DrawWorldLine(draw, ball, viewProj, size, Ring(ring, a0), Ring(ring, a1), color, thickness);
                        }
                }
            }
        }
    }

    private const int MaxWireframeTriangles = 4000;   // keeps huge meshes from slowing the editor

    private void DrawMeshWireframe(Canvas2D draw, string path, Matrix4x4 world, Matrix4x4 viewProj, float4 size,
        float4 color, float thickness)
    {
        GlbModel model;
        try { model = _assets.GetModel(path); }
        catch { return; }   // the Inspector shows a missing model; the physics warns once

        int drawn = 0;
        foreach (var m in model.Meshes)
        {
            var p = m.Positions;
            Vector3 At(uint i) => new(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);
            for (int i = 0; i + 2 < m.Indices.Length && drawn < MaxWireframeTriangles; i += 3, drawn++)
            {
                uint a = m.Indices[i], b = m.Indices[i + 1], c = m.Indices[i + 2];
                if (Math.Max(a, Math.Max(b, c)) * 3 + 2 >= p.Length) continue;
                DrawWorldLine(draw, world, viewProj, size, At(a), At(b), color, thickness);
                DrawWorldLine(draw, world, viewProj, size, At(b), At(c), color, thickness);
                DrawWorldLine(draw, world, viewProj, size, At(c), At(a), color, thickness);
            }
        }
    }

    // The convex hull a moving mesh collider collides with.
    private void DrawHullWireframe(Canvas2D draw, string path, Matrix4x4 world, Matrix4x4 viewProj, float4 size,
        float4 color, float thickness)
    {
        IReadOnlyList<(Vector3 A, Vector3 B)> edges;
        try { edges = Engine.Core.Physics.PhysicsWorld.HullOutline(_assets.GetModel(path)); }
        catch { return; }
        foreach (var (a, b) in edges)
            DrawWorldLine(draw, world, viewProj, size, a, b, color, thickness);
    }

    private static Vector3 Ring(int plane, float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return plane switch { 0 => new Vector3(c, s, 0f), 1 => new Vector3(c, 0f, s), _ => new Vector3(0f, c, s) };
    }

    // The mesh's model button and tint. The model browser picks a model for this object.
    private void DrawMeshBody(SceneObject obj, MeshRenderer mr, bool editing)
    {
        var g = _gui;
        string label = mr.Model.Length == 0 ? "Choose model" : SafeLabel.Of(Path.GetFileName(mr.Model), 60);
        if (g.Button($"{label}##model", width: -1f) && editing)
        {
            _browserTarget = obj.Id;
            _meshColliderTarget = null;
            _panoramaTarget = null;
            _audioTarget = null;
            _browser.Open(_contentRoot, mr.Model);
        }

        var tint = new float3(mr.Tint.X, mr.Tint.Y, mr.Tint.Z);
        if (g.DragFloat3("Tint", ref tint, 0.01f) && editing)
            mr.Tint = new Vector4(tint.X, tint.Y, tint.Z, mr.Tint.W);
        float opacity = mr.Tint.W;
        if (g.DragFloat("Opacity", ref opacity, 0.01f, 0f, 1f) && editing)
            mr.Tint = new Vector4(mr.Tint.X, mr.Tint.Y, mr.Tint.Z, Math.Clamp(opacity, 0f, 1f));
        if (mr.Tint.W < 0.999f)
            g.TextDisabled("See-through: blended over what is behind it.");
    }

    private void DrawCameraBody(SceneObject obj, CameraComponent cam, bool editing)
    {
        var g = _gui;
        float focal = cam.FocalLength;
        if (g.DragFloat("Focal Length (mm)", ref focal, 0.5f, 1f, 300f) && editing)
            cam.FocalLength = focal;
        float near = cam.Near;
        if (g.DragFloat("Near", ref near, 0.01f, 0.01f, 100f) && editing)
            cam.Near = near;
        float far = cam.Far;
        if (g.DragFloat("Far", ref far, 1f, 1f, 10000f) && editing)
            cam.Far = far;

        // Anti-aliasing smooths jagged edges. MSAA draws several samples per pixel and averages them.
        int mode = cam.AntiAliasing == AntiAliasingMode.Msaa ? 1 : 0;
        if (g.Combo("Anti-Aliasing", mode, AntiAliasingNames, out int pickedMode) && editing)
            cam.AntiAliasing = pickedMode == 1 ? AntiAliasingMode.Msaa : AntiAliasingMode.None;
        if (cam.AntiAliasing == AntiAliasingMode.Msaa)
        {
            int stop = Array.IndexOf(MsaaCounts, CameraComponent.NormalizeMsaaSamples(cam.MsaaSamples));
            int unsupportedFrom = MsaaCounts.Count(c => c <= _maxSamples);   // stops from here on aren't available
            if (g.SliderStops("MSAA Samples", ref stop, MsaaNames, unsupportedFrom) && editing)
                cam.MsaaSamples = MsaaCounts[stop];

            if (_maxSamples < 2)
                g.TextWrapped("This GPU can't do MSAA, so it is off.", Gui.DimColor);
            else if (cam.RequestedSamples > _maxSamples)
                g.TextWrapped($"This GPU supports up to {_maxSamples}x, so {_maxSamples}x is used.", Gui.DimColor);
        }

        bool isMainCamera = GameCamera()?.Obj == obj;
        if (isMainCamera)
            g.TextDisabled("Used for play mode");
        else if (cam.AntiAliasing != AntiAliasingMode.None)
            g.TextWrapped("Only the main camera's anti-aliasing is used.", Gui.DimColor);
    }

    private void DrawLightBody(DirectionalLight light, bool editing)
    {
        var g = _gui;
        float brightness = light.Intensity;
        if (g.DragFloat("Brightness", ref brightness, 0.05f, 0f, 20f) && editing)
            light.Intensity = brightness;
        var lightColor = light.Color;
        if (g.ColorEdit3("Light Color", ref lightColor) && editing)
            light.Color = lightColor;
        float yaw = light.Yaw;
        if (g.DragFloat("Yaw", ref yaw, 0.5f, -360f, 360f) && editing)
            light.Yaw = yaw;
        float pitch = light.Pitch;
        if (g.DragFloat("Pitch", ref pitch, 0.5f, -89f, 89f) && editing)
            light.Pitch = pitch;
        if (SceneLighting.FindSun(_scene) != light)
        {
            g.TextWrapped("The scene's first directional light is the sun. This one adds light without a shadow.", Gui.DimColor);
            return;
        }

        Bool("Shadows", light.Shadows, v => light.Shadows = v, editing);
        if (!light.Shadows) return;
        float strength = light.ShadowStrength;
        if (g.DragFloat("Shadow Strength", ref strength, 0.01f, 0f, 1f) && editing)
            light.ShadowStrength = strength;
        float distance = light.ShadowDistance;
        if (g.DragFloat("Shadow Distance", ref distance, 0.5f, 5f, 500f) && editing)
            light.ShadowDistance = distance;
        g.TextDisabled("How far from the camera shadows reach.");
    }

    // A light that shines in every direction from the object, fading to nothing at its range.
    private void DrawPointLightBody(PointLight light, bool editing)
    {
        var g = _gui;
        var color = light.Color;
        if (g.ColorEdit3("Color", ref color) && editing)
            light.Color = color;
        float intensity = light.Intensity;
        if (g.DragFloat("Intensity", ref intensity, 0.05f, 0f, 100f) && editing)
            light.Intensity = intensity;
        float range = light.Range;
        if (g.DragFloat("Range", ref range, 0.1f, 0.1f, 1000f) && editing)
            light.Range = range;
    }

    // A cone of light along the object's -Z axis. The scene view draws the cone and its direction.
    private void DrawSpotLightBody(SpotLight light, bool editing)
    {
        var g = _gui;
        var color = light.Color;
        if (g.ColorEdit3("Color", ref color) && editing)
            light.Color = color;
        float intensity = light.Intensity;
        if (g.DragFloat("Intensity", ref intensity, 0.05f, 0f, 100f) && editing)
            light.Intensity = intensity;
        float range = light.Range;
        if (g.DragFloat("Range", ref range, 0.1f, 0.1f, 1000f) && editing)
            light.Range = range;
        float inner = light.InnerAngle;
        if (g.DragFloat("Inner Angle", ref inner, 0.25f, 0f, 89f) && editing)
            light.InnerAngle = inner;
        float outer = light.OuterAngle;
        if (g.DragFloat("Outer Angle", ref outer, 0.25f, 1f, 89f) && editing)
            light.OuterAngle = MathF.Max(outer, light.InnerAngle);
        g.TextWrapped("Aim it by rotating the object: it shines along the object's -Z axis.", Gui.DimColor);
    }

    // The sky behind the scene and the ambient light. Only the first Environment in the scene is used.
    private static readonly string[] ParticlePresetNames = ParticlePresets.All.Select(p => p.Name).Prepend("Custom").ToArray();
    private static readonly string[] PresetNames = EnvironmentPresets.All.Select(p => p.Name).Prepend("Custom").ToArray();

    private void DrawEnvironmentBody(SceneObject obj, SceneEnvironment env, bool editing)
    {
        var g = _gui;

        // A preset sets the sky, ambient light and sky lighting, and the scene's sun, in one step. Changing any of its
        // settings afterwards makes the dropdown read Custom.
        var match = EnvironmentPresets.Find(env);
        int current = match is null ? 0 : Array.IndexOf(PresetNames, match.Name);
        if (g.Combo("Preset", current, PresetNames, out int picked) && editing && picked > 0)
            EnvironmentPresets.All[picked - 1].Apply(env, SceneLighting.FindSun(_scene));
        g.TextWrapped(match?.Description ?? "Pick a preset to set the sky, ambient light and the scene's sun.", Gui.DimColor);
        g.Separator();

        var sky = env.Sky;
        if (g.Combo("Sky", ref sky) && editing)
            env.Sky = sky;

        if (env.Sky == SkyMode.Color)
        {
            var color = env.SkyColor;
            if (g.ColorEdit3("Sky Color", ref color) && editing) env.SkyColor = color;
        }
        else if (env.Sky == SkyMode.Gradient)
        {
            var top = env.SkyColor;
            if (g.ColorEdit3("Top", ref top) && editing) env.SkyColor = top;
            var horizon = env.HorizonColor;
            if (g.ColorEdit3("Horizon", ref horizon) && editing) env.HorizonColor = horizon;
            var ground = env.GroundColor;
            if (g.ColorEdit3("Ground", ref ground) && editing) env.GroundColor = ground;
        }
        else
        {
            string label = env.Panorama.Length == 0 ? "Choose image" : SafeLabel.Of(Path.GetFileName(env.Panorama), 60);
            if (g.Button($"{label}##panorama", width: -1f) && editing)
            {
                _panoramaTarget = obj.Id;
                _audioTarget = null;
                _browserTarget = null;
                _meshColliderTarget = null;
                _browser.Open(_contentRoot, env.Panorama, ModelBrowser.IsImage, "Select Sky Image");
            }
            if (env.Panorama.Length > 0 && editing && g.Button("Clear##panorama"))
                env.Panorama = "";
            g.TextWrapped("An equirectangular image: PNG, JPEG or Radiance .hdr, twice as wide as it is tall.", Gui.DimColor);
            var fallback = env.SkyColor;
            if (g.ColorEdit3("Fallback Color", ref fallback) && editing) env.SkyColor = fallback;
        }

        float skyIntensity = env.SkyIntensity;
        if (g.DragFloat("Sky Brightness", ref skyIntensity, 0.01f, 0f, 10f) && editing)
            env.SkyIntensity = skyIntensity;
        float skyLighting = env.SkyLighting;
        if (g.DragFloat("Sky Lighting", ref skyLighting, 0.01f, 0f, 4f) && editing)
            env.SkyLighting = skyLighting;

        var ambient = env.AmbientColor;
        if (g.ColorEdit3("Ambient Color", ref ambient) && editing)
            env.AmbientColor = ambient;
        float ambientIntensity = env.AmbientIntensity;
        if (g.DragFloat("Ambient Brightness", ref ambientIntensity, 0.005f, 0f, 2f) && editing)
            env.AmbientIntensity = ambientIntensity;
    }

    // The list under Add Component. Script opens the script picker, which adds a script component.
    private void DrawAddComponentList(SceneObject obj)
    {
        var g = _gui;
        void Add(string label, Action add)
        {
            if (!g.Selectable(label, false)) return;
            add();
            _addingComponent = false;
        }

        Add("Mesh", () => AddMeshComponent(obj));
        Add("Script", () => _pickingScript = true);
        Add("Spin", () => obj.Components.Add(new Spin()));
        Add("Camera", () => obj.Components.Add(new CameraComponent()));
        Add("Directional Light", () => obj.Components.Add(new DirectionalLight()));
        Add("Point Light", () => obj.Components.Add(new PointLight()));
        Add("Spot Light", () => obj.Components.Add(new SpotLight()));
        if (!obj.Components.OfType<SceneEnvironment>().Any())
            Add("Environment", () => obj.Components.Add(new SceneEnvironment()));
        if (!obj.Components.OfType<Rigidbody>().Any())
            Add("Rigidbody", () => obj.Components.Add(new Rigidbody()));
        Add("Box Collider", () => AddCollider(obj, sphere: false));
        Add("Sphere Collider", () => AddCollider(obj, sphere: true));
        Add("Mesh Collider", () => obj.Components.Add(new MeshCollider()));
        Add("Particle System", () => obj.Components.Add(new ParticleSystem()));
        if (!obj.Components.OfType<Terrain>().Any())
            Add("Terrain", () => obj.Components.Add(NewTerrain()));
        if (!obj.Components.OfType<Animator>().Any())
            Add("Animator", () => obj.Components.Add(new Animator()));
        Add("Audio Source", () => obj.Components.Add(new AudioSource()));
        if (!obj.Components.OfType<AudioListener>().Any())
            Add("Audio Listener", () => obj.Components.Add(new AudioListener()));
        foreach (var kind in Enum.GetValues<UiKind>())
            Add($"UI {kind}##add{kind}", () => obj.Components.Add(NewUiElement(kind)));

        // Components written in the project's scripts and in enabled packages.
        foreach (var custom in _scriptFields.Classes.Where(c => c.IsComponent)
                     .OrderBy(c => c.Package is null ? 0 : 1).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            string where = custom.Package is { } package ? $"  ({package} package)" : "  (script)";
            Add($"{custom.Name}{where}##addcustom{custom.Name}", () => obj.Components.Add(new ScriptComponent { ScriptName = custom.Name }));
        }
    }

    // A mesh component starts on the first model in Models/ when there is one. The Inspector can change it.
    private void AddMeshComponent(SceneObject obj)
    {
        if (obj.Components.OfType<MeshRenderer>().Any()) return;
        obj.Components.Add(new MeshRenderer { Model = FirstModel() ?? "" });
    }

    private static UiElement NewUiElement(UiKind kind) => new()
    {
        Kind = kind,
        Label = kind switch
        {
            UiKind.Button => "Button",
            UiKind.TextField => "Text Field",
            UiKind.Checkbox => "Checkbox",
            UiKind.Dropdown => "Dropdown",
            _ => "Bar",
        },
        H = kind == UiKind.Bar ? 0.03f : 0.07f,
    };

    // Uploads a model the first time it is drawn, so models added at runtime show up too.
    // Returns false when the model can't be loaded, so the caller skips drawing it.
    private bool EnsureUploaded(BrutalRenderer renderer, string relPath)
    {
        if (string.IsNullOrEmpty(relPath) || _badModels.Contains(relPath)) return false;
        if (_uploaded.Contains(relPath)) return true;
        try
        {
            renderer.UploadModel(relPath, _assets.GetModel(relPath));
            _uploaded.Add(relPath);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not load {relPath}: {ex.Message}");
            _badModels.Add(relPath);
            return false;
        }
    }

    private void DrawNameField(SceneObject obj, bool editing)
    {
        string name = obj.Name;
        if (_gui.InputText($"Name##{obj.Id:N}", ref name, 255) && editing)
            obj.Name = name;
    }

    // ---- transform tools -------------------------------------------------------------

    // A column of tool buttons just right of the hierarchy, on the left edge of the 3D view.
    // Move and Scale are greyed out until an object is selected.
    private void DrawToolButtons()
    {
        var g = _gui;
        g.BeginWindow("Tools", new Box(SideWidth + 6f, PanelTop, 72f, 3 * 34f + 12f), flags: WindowFlags.NoScroll);
        ToolButton("None", GizmoTool.None, enabled: true);
        ToolButton("Move", GizmoTool.Move, enabled: _selected is not null);
        ToolButton("Scale", GizmoTool.Scale, enabled: _selected is not null);
        g.EndWindow();
    }

    private void ToolButton(string label, GizmoTool tool, bool enabled)
    {
        if (_gui.Button(label, 56f, 30f, enabled, highlighted: _gizmo.Tool == tool))
            _gizmo.Tool = tool;
    }

    // ---- camera gizmos ---------------------------------------------------------------

    // Draws each camera in the edit view: a small body, a frustum from the lens, and a forward line.
    // Selected cameras are yellow, the play camera is green, and the rest are gray.
    // viewport is the part of the window the scene fills (x, y, width, height as fractions),
    // so the outlines line up with the picture even when the window isn't 16:9.
    private void DrawCameraGizmos(Canvas2D draw, Matrix4x4 viewProj, float4 viewport)
    {
        float2 display = ImGui.GetIO().DisplaySize;
        float4 size = new float4(viewport.X * display.X, viewport.Y * display.Y,
                                 viewport.Z * display.X, viewport.W * display.Y);
        var play = GameCamera();
        float aspect = VulkanContext.SceneAspect;

        foreach (var obj in _scene.All)
        {
            var cam = obj.Components.OfType<CameraComponent>().FirstOrDefault();
            if (cam is null) continue;

            bool isPlay = play is { } p && p.Obj == obj;
            float4 color =
                obj == _selected ? new float4(1f, 0.85f, 0.2f, 1f) :
                isPlay ? new float4(0.3f, 1f, 0.4f, 1f) :
                new float4(0.85f, 0.85f, 0.9f, 1f);

            var world = obj.WorldMatrix;
            const float depth = 1f;   // frustum length in local units
            float halfH = depth * MathF.Tan(cam.VerticalFov(aspect) / 2f);
            float halfW = halfH * aspect;
            var apex = Vector3.Zero;   // the lens, at the object's origin

            // Frustum: lines from the lens to the corners of the frame, then the frame itself.
            var corners = new[]
            {
                new Vector3(-halfW, -halfH, -depth),
                new Vector3( halfW, -halfH, -depth),
                new Vector3( halfW,  halfH, -depth),
                new Vector3(-halfW,  halfH, -depth),
            };
            for (int i = 0; i < 4; i++)
            {
                DrawWorldLine(draw, world, viewProj, size, apex, corners[i], color, 1.25f);
                DrawWorldLine(draw, world, viewProj, size, corners[i], corners[(i + 1) % 4], color, 1.25f);
            }

            // Facing: a thicker line straight out of the lens along the view direction.
            DrawWorldLine(draw, world, viewProj, size, apex, new Vector3(0f, 0f, -depth * 1.4f), color, 2.5f);

            // Body: a small box behind the lens.
            var box = new[]
            {
                new Vector3(-0.15f, -0.12f, 0f), new Vector3(0.15f, -0.12f, 0f),
                new Vector3(0.15f,  0.12f, 0f),  new Vector3(-0.15f,  0.12f, 0f),
                new Vector3(-0.15f, -0.12f, 0.35f), new Vector3(0.15f, -0.12f, 0.35f),
                new Vector3(0.15f,  0.12f, 0.35f),  new Vector3(-0.15f,  0.12f, 0.35f),
            };
            for (int i = 0; i < 4; i++)
            {
                DrawWorldLine(draw, world, viewProj, size, box[i], box[(i + 1) % 4], color, 1.25f);
                DrawWorldLine(draw, world, viewProj, size, box[i + 4], box[(i + 1) % 4 + 4], color, 1.25f);
                DrawWorldLine(draw, world, viewProj, size, box[i], box[i + 4], color, 1.25f);
            }

            // Name label next to the lens.
            if (TryProject(Vector3.Transform(apex, world), viewProj, size, out var labelAt))
            {
                var labelPos = new float2(labelAt.X + 10f, labelAt.Y - 18f);
                draw.Text(SafeLabel.Of(obj.Name), labelPos.X, labelPos.Y, color);
            }
        }
    }

    // Lights in the edit view: a small marker on every light, and when selected its reach (rings for a point light,
    // the cone for a spot light, an arrow for a directional light). Colored like the light, yellow when selected.
    private void DrawLightGizmos(Canvas2D draw, Matrix4x4 viewProj, float4 viewport)
    {
        float2 display = ImGui.GetIO().DisplaySize;
        float4 size = new float4(viewport.X * display.X, viewport.Y * display.Y, viewport.Z * display.X, viewport.W * display.Y);
        var identity = Matrix4x4.Identity;
        const int segments = 40;

        foreach (var obj in _scene.All)
        {
            var world = obj.WorldMatrix;
            var origin = world.Translation;
            bool selected = IsSelected(obj);

            foreach (var component in obj.Components)
            {
                Vector3 lightColor;
                switch (component)
                {
                    case PointLight point: lightColor = point.Color; break;
                    case SpotLight spot: lightColor = spot.Color; break;
                    case DirectionalLight sun: lightColor = sun.Color; break;
                    default: continue;
                }
                var color = selected ? new float4(1f, 0.85f, 0.2f, 1f)
                    : new float4(MathF.Max(lightColor.X, 0.5f), MathF.Max(lightColor.Y, 0.5f), MathF.Max(lightColor.Z, 0.5f), 0.85f);
                float thickness = selected ? 2f : 1.25f;

                // A small marker: three axes crossing at the light.
                foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                    DrawWorldLine(draw, identity, viewProj, size, origin - axis * 0.25f, origin + axis * 0.25f, color, thickness);

                if (component is PointLight pl && selected)
                {
                    for (int ring = 0; ring < 3; ring++)
                        for (int i = 0; i < segments; i++)
                        {
                            float a0 = i * MathF.Tau / segments, a1 = (i + 1) * MathF.Tau / segments;
                            DrawWorldLine(draw, identity, viewProj, size, origin + Ring(ring, a0) * pl.Range, origin + Ring(ring, a1) * pl.Range, color, 1.25f);
                        }
                }
                else if (component is SpotLight sl)
                {
                    var forward = Vector3.TransformNormal(-Vector3.UnitZ, world);
                    forward = forward.LengthSquared() > 1e-12f ? Vector3.Normalize(forward) : -Vector3.UnitZ;
                    var right = Vector3.Cross(forward, Vector3.UnitY);
                    right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : Vector3.UnitX;
                    var up = Vector3.Cross(right, forward);
                    float length = selected ? sl.Range : MathF.Min(sl.Range, 3f);

                    void Cone(float degrees, float4 coneColor, bool spokes)
                    {
                        float radius = length * MathF.Tan(Math.Clamp(degrees, 0.5f, 89f) * MathF.PI / 180f);
                        var center = origin + forward * length;
                        for (int i = 0; i < segments; i++)
                        {
                            float a0 = i * MathF.Tau / segments, a1 = (i + 1) * MathF.Tau / segments;
                            DrawWorldLine(draw, identity, viewProj, size,
                                center + (right * MathF.Cos(a0) + up * MathF.Sin(a0)) * radius,
                                center + (right * MathF.Cos(a1) + up * MathF.Sin(a1)) * radius, coneColor, 1.25f);
                        }
                        if (!spokes) return;
                        for (int i = 0; i < 4; i++)
                        {
                            float a = i * MathF.PI / 2f;
                            DrawWorldLine(draw, identity, viewProj, size, origin,
                                center + (right * MathF.Cos(a) + up * MathF.Sin(a)) * radius, coneColor, 1.25f);
                        }
                    }
                    Cone(sl.OuterAngle, color, spokes: true);
                    if (selected) Cone(sl.InnerAngle, color with { W = 0.5f }, spokes: false);
                    DrawWorldLine(draw, identity, viewProj, size, origin, origin + forward * length, color, 2f);
                }
                else if (component is DirectionalLight dl)
                {
                    // An arrow along the way the light travels.
                    var travel = -dl.Direction;
                    var tip = origin + travel * 2.5f;
                    DrawWorldLine(draw, identity, viewProj, size, origin, tip, color, thickness);
                    var side = Vector3.Cross(travel, Vector3.UnitY);
                    side = side.LengthSquared() > 1e-6f ? Vector3.Normalize(side) : Vector3.UnitX;
                    DrawWorldLine(draw, identity, viewProj, size, tip, tip - travel * 0.5f + side * 0.25f, color, thickness);
                    DrawWorldLine(draw, identity, viewProj, size, tip, tip - travel * 0.5f - side * 0.25f, color, thickness);
                }

                // The name next to the marker, like cameras.
                if (selected && TryProject(origin, viewProj, size, out var labelAt))
                    draw.Text(SafeLabel.Of(obj.Name), labelAt.X + 10f, labelAt.Y - 18f, color);
            }
        }
    }

    private static void DrawWorldLine(Canvas2D draw, Matrix4x4 world, Matrix4x4 viewProj, float4 size,
        Vector3 a, Vector3 b, float4 color, float thickness)
    {
        if (!TryProject(Vector3.Transform(a, world), viewProj, size, out var pa)) return;
        if (!TryProject(Vector3.Transform(b, world), viewProj, size, out var pb)) return;
        draw.Line(pa, pb, thickness, color);
    }

    // World point to screen pixels inside the scene rectangle. Returns false for points behind the camera.
    private static bool TryProject(Vector3 point, Matrix4x4 viewProj, float4 size, out float2 screen)
    {
        var clip = Vector4.Transform(new Vector4(point, 1f), viewProj);
        if (clip.W <= 0.01f)
        {
            screen = default;
            return false;
        }

        float x = clip.X / clip.W;
        float y = clip.Y / clip.W;
        screen = new float2(size.X + (x * 0.5f + 0.5f) * size.Z, size.Y + (y * 0.5f + 0.5f) * size.W);
        return true;
    }

    // ---- toolbar ---------------------------------------------------------------------

    // Play / Stop button centered at the top of the window. Drawn in both modes so play can always be stopped.
    private void DrawToolbar()
    {
        var g = _gui;
        const float height = 38f;
        float width = _mode == EditorMode.Edit ? 4 * 92f + 3 * 6f + 16f : 260f;
        float y = _mode == EditorMode.Edit ? PanelTop : 6f;   // below the menu bar while editing

        g.BeginWindow("Toolbar", new Box((g.Size.X - width) / 2f, y, width, height), flags: WindowFlags.NoScroll);
        if (_mode == EditorMode.Edit)
        {
            if (g.Button(_uiMode ? "Done UI" : "UI", 92f, height - 16f, highlighted: _uiMode))
                _uiMode = !_uiMode;
            g.SameLine();
            if (g.Button("Play", 92f, height - 16f, highlighted: true))
                TogglePlay();
            g.SameLine();
            if (g.Button("VC", 92f, height - 16f))
            {
                _vcMode = !_vcMode;
                _codeMode = false;
            }
            g.SameLine();
            if (g.Button("Code", 92f, height - 16f))
            {
                _codeMode = !_codeMode;
                _vcMode = false;
            }
        }
        else if (g.Button("Stop", width: -1f, height: height - 16f, highlighted: true))
            TogglePlay();
        g.EndWindow();
    }

    // ---- scripts ---------------------------------------------------------------------

    private void DrawScriptPicker()
    {
        if (!_pickingScript || _selected is null) return;
        var g = _gui;

        string folder = Path.Combine(_contentRoot, "Scripts");
        var names = Directory.Exists(folder)
            ? Directory.GetFiles(folder).Where(f => f.EndsWith(".cs") || f.EndsWith(".visualcode")).Select(f => Path.GetFileNameWithoutExtension(f)).Distinct().OrderBy(n => n).ToList()
            : new List<string>();

        var rect = new Box((g.Size.X - 380f) / 2f, 80f, 380f, Math.Min(380f, g.Size.Y - 120f));
        if (!g.BeginWindow("Scripts", rect, "Attach Script", WindowFlags.Closable | WindowFlags.NoScroll))
            _pickingScript = false;

        g.TextDisabled("Attach to: " + SafeLabel.Of(_selected.Name));
        g.BeginChild("scripts", 0f, -RowsBelow(3));
        int id = 0;
        foreach (var name in names)
        {
            if (g.Selectable($"{name}##{id++}", false))
            {
                AttachScript(name);
                _pickingScript = false;
            }
        }
        g.EndChild();

        g.InputText("New script", ref _newScriptName, 64);
        if (g.Button("Create and Attach", highlighted: true))
        {
            string newName = ScriptFiles.ToClassName(_newScriptName);
            if (newName.Length > 0)
            {
                ScriptFiles.Create(folder, newName);
                AttachScript(newName);
                _newScriptName = "";
                _pickingScript = false;
            }
        }
        g.SameLine();
        if (g.Button("Close"))
            _pickingScript = false;
        g.EndWindow();
    }

    // Room to leave under a child list for this many rows of widgets.
    private static float RowsBelow(int rows) => rows * (Gui.RowHeight + 4f) + 4f;

    // Compiles the project's scripts for play mode and loads them. Returns the errors, or an empty string.
    private string CompileScripts()
    {
        VisualCode.Compile.VisualScriptSync.Sync(Path.Combine(_contentRoot, "Scripts"));
        bool hasScripts = _scene.All.Any(o => o.Components.OfType<ScriptComponent>().Any());
        if (!hasScripts)
            return ScriptRuntime.Load(_scene, (Assembly?)null);

        // Enabled packages first (built if they changed), then the project's scripts, which may use them.
        string engine = typeof(Behaviour).Assembly.Location;
        var (packageDlls, packageErrors) = Packages.Build(_contentRoot, engine);
        if (packageErrors.Length > 0)
            return packageErrors;
        var packages = PackageAssemblies.Load(packageDlls);

        string dll = Path.Combine(Path.GetTempPath(), "empire-scripts", Guid.NewGuid().ToString("N") + ".dll");
        var references = ScriptBuilder.DefaultReferences(engine);
        references.AddRange(packageDlls);
        if (!ScriptBuilder.Compile(Path.Combine(_contentRoot, "Scripts"), dll, references, out string errors))
            return errors;

        // The project's scripts come first, so a script wins over a package class with the same name.
        return ScriptRuntime.Load(_scene, new[] { Assembly.LoadFrom(dll) }.Concat(packages).ToList());
    }

    private void AttachScript(string scriptName)
    {
        if (_selected is null) return;
        _selected.Components.Add(new ScriptComponent { ScriptName = scriptName });
    }

    // ---- build -----------------------------------------------------------------------

    private void StartBuild()
    {
        _buildResult = null;
        _buildFolder = "";
        _folderBrowser.Open(Path.GetDirectoryName(Path.GetFullPath(_contentRoot)) ?? _contentRoot);
        _buildStage = BuildStage.PickFolder;
    }

    private void CloseBuild()
    {
        _folderBrowser.Close();
        _buildStage = BuildStage.Off;
    }

    private void BeginBuild()
    {
        string name = ProjectBuilder.SanitizeName(_buildName);
        if (name.Length == 0) return;

        // Builds from the saved files on disk. Unsaved edits are not included.
        _buildStage = BuildStage.Running;
        string scenePath = _scenePath;
        string contentRoot = _contentRoot;
        string folder = _buildFolder;
        _buildTask = Task.Run(() => ProjectBuilder.Build(scenePath, contentRoot, folder, name));
    }

    private void DrawBuildWindow()
    {
        if (_buildStage == BuildStage.Off) return;
        var g = _gui;

        var rect = new Box((g.Size.X - 500f) / 2f, 70f, 500f, Math.Min(460f, g.Size.Y - 100f));
        if (!g.BeginWindow("Build Project", rect, "Build Project", WindowFlags.Closable | WindowFlags.NoScroll) &&
            _buildStage != BuildStage.Running)
        {
            CloseBuild();
            g.EndWindow();
            return;
        }

        switch (_buildStage)
        {
            case BuildStage.PickFolder:
                g.Text("Choose the folder to build into.");
                if (_folderBrowser.Draw(g, rect.H - 160f) is { } folder)
                {
                    _buildFolder = folder;
                    _buildName = ProjectBuilder.SanitizeName(_scene.Name);
                    _buildStage = BuildStage.EnterName;
                }
                if (g.Button("Cancel"))
                    CloseBuild();
                break;

            case BuildStage.EnterName:
                g.TextWrapped("Folder: " + _buildFolder, Gui.DimColor);
                g.InputText("Build name", ref _buildName, 128);
                if (_buildName.Trim().Length == 0)
                    g.TextDisabled("Enter a build name.");
                if (_unsaved)
                    g.TextWrapped("Unsaved changes are not included. Save first with Cmd/Ctrl+S.", Gui.ErrorColor);
                g.Separator();
                if (g.Button("Build", highlighted: true))
                    BeginBuild();
                g.SameLine();
                if (g.Button("Back"))
                {
                    _folderBrowser.Open(_buildFolder);
                    _buildStage = BuildStage.PickFolder;
                }
                g.SameLine();
                if (g.Button("Cancel"))
                    CloseBuild();
                break;

            case BuildStage.Running:
                g.Text("Building... this can take a minute.");
                if (_buildTask is { IsCompleted: true } task)
                {
                    _buildResult = task.Result;
                    _buildTask = null;
                    _buildStage = BuildStage.Done;
                }
                break;

            case BuildStage.Done:
                g.TextWrapped(_buildResult?.Message ?? "", _buildResult is { Success: false } ? Gui.ErrorColor : null);
                g.Separator();
                if (_buildResult?.OutputFolder is { } output)
                {
                    if (g.Button("Open Folder"))
                        Process.Start(new ProcessStartInfo(output) { UseShellExecute = true });
                    g.SameLine();
                }
                if (_buildResult is { Success: false } failed)
                {
                    if (g.Button("Copy Errors"))
                        ImGui.SetClipboardText(ProjectBuilder.ErrorReport(failed));
                    g.SameLine();
                }
                if (g.Button("Close"))
                    CloseBuild();
                break;
        }

        g.EndWindow();
    }



    // ---- menu bar --------------------------------------------------------------------

    private void DrawMenuBar()
    {
        var g = _gui;
        // Checked here, after the panels have run, so the result reflects this frame's edits.
        _unsaved = _mode == EditorMode.Edit && SceneSerializer.ToJson(_scene) != _savedJson;

        g.BeginMenuBar();
        if (g.BeginMenu("File"))
        {
            string shortcut = OperatingSystem.IsMacOS() ? "Cmd+S" : "Ctrl+S";
            if (g.MenuItem("Save Scene", shortcut, enabled: _mode == EditorMode.Edit))
                SaveScene();
            if (g.MenuItem("Build Project...", enabled: _mode == EditorMode.Edit && _buildStage == BuildStage.Off))
                StartBuild();
            if (g.MenuItem("Package Manager...", enabled: _mode == EditorMode.Edit))
                _packages.Open();
            g.Separator();
            if (g.MenuItem("Save and Exit", enabled: _mode == EditorMode.Edit))
            {
                SaveScene();
                _code.SaveAll();
                _exitRequested = true;
            }
            if (g.MenuItem("Exit"))
                RequestExit();
            g.EndMenu();
        }
        if (g.BeginMenu("Help"))
        {
            if (g.MenuItem("Credits"))
                _credits.Open();
            g.EndMenu();
        }
        g.EndMenuBar();
        _credits.Draw(g);
        if (_mode == EditorMode.Edit)
            _packages.Draw(g, _scene, _scriptFields.Classes, _scriptFields.Errors, _scriptFields.IsCompiling);
    }

    // ---- exiting ---------------------------------------------------------------------

    // Exits now if the scene is saved; otherwise opens the unsaved-changes prompt.
    // In play mode the scene is restored first, so the check sees the edits made before pressing Play.
    private void RequestExit()
    {
        if (_mode == EditorMode.Play)
            TogglePlay();

        if (SceneSerializer.ToJson(_scene) == _savedJson && !_code.HasUnsavedChanges)
            _exitRequested = true;
        else
            _confirmingExit = true;
    }

    private void DrawExitPrompt()
    {
        if (!_confirmingExit) return;
        var g = _gui;

        var rect = new Box(g.Size.X * 0.5f - 200f, g.Size.Y * 0.5f - 70f, 400f, 120f);
        g.BeginWindow("Unsaved Changes", rect, "Unsaved Changes", WindowFlags.Modal | WindowFlags.NoScroll);

        string unsaved = SceneSerializer.ToJson(_scene) != _savedJson
            ? (_code.HasUnsavedChanges ? $"{_scene.Name} and your scripts" : _scene.Name)
            : "your scripts";
        g.TextWrapped($"Save changes to {unsaved} before exiting?");
        g.Spacing();
        if (g.Button("Save and Exit", highlighted: true))
        {
            SaveScene();
            _code.SaveAll();
            _exitRequested = true;
        }
        g.SameLine();
        if (g.Button("Exit Without Saving"))
            _exitRequested = true;
        g.SameLine();
        if (g.Button("Cancel"))
            _confirmingExit = false;

        g.EndWindow();
    }

    // Escape closes the open dialog, if there is one, before it would close the editor.
    private bool CloseTopDialog()
    {
        if (_credits.IsOpen) { _credits.Close(); return true; }
        if (_packages.IsOpen) { _packages.Close(); return true; }
        if (_assetPanel.CloseDialog()) return true;
        if (_browser.IsOpen) { _browser.Close(); return true; }
        if (_pickingScript) { _pickingScript = false; return true; }
        if (_buildStage is BuildStage.PickFolder or BuildStage.EnterName or BuildStage.Done) { CloseBuild(); return true; }
        return false;
    }

    // ---- saving ----------------------------------------------------------------------

    private void SaveScene()
    {
        if (_mode != EditorMode.Edit)
        {
            Console.WriteLine("Stop play mode before saving.");
            return;
        }

        SceneSerializer.Save(_scene, _scenePath);
        _savedJson = SceneSerializer.ToJson(_scene);
        _unsaved = false;
        Console.WriteLine($"Saved {_scenePath}");
    }

    private static bool IsSaveShortcutHeld(GlfwWindow window)
    {
        if (OperatingSystem.IsMacOS())
            return window.GetKey(GlfwKey.LeftSuper) == GlfwKeyAction.Press ||
                   window.GetKey(GlfwKey.RightSuper) == GlfwKeyAction.Press;

        return window.GetKey(GlfwKey.LeftControl) == GlfwKeyAction.Press ||
               window.GetKey(GlfwKey.RightControl) == GlfwKeyAction.Press;
    }

    // ---- actions ---------------------------------------------------------------------

    // An empty object: no components yet. Add a mesh, script, or other component from the Inspector.
    private void AddObject()
    {
        var obj = new SceneObject { Name = $"Empty {_scene.All.Count() + 1}" };
        _scene.Add(obj);
        _selectedId = obj.Id;
    }

    // Hierarchy and Inspector stop above the asset panel, so they never cover it on short windows.
    // From the menu bar down to the top of the asset panel, so the side panels meet both.
    private float SideHeight() =>
        Math.Max(120f, _gui.Size.Y - Gui.MenuBarHeight - AssetPanel.Height);

    // Where something dropped at the cursor lands in the world: on the ground plane (y = 0) under the cursor, or else
    // 10 units ahead. Null when the cursor is not over the scene view.
    private static Vector3? DropPoint(float2 cursor, float4 viewport, Matrix4x4 viewProj)
    {
        var display = ImGui.GetIO().DisplaySize;
        float left = viewport.X * display.X, top = viewport.Y * display.Y;
        float width = viewport.Z * display.X, height = viewport.W * display.Y;
        if (cursor.X < left || cursor.X > left + width || cursor.Y < top || cursor.Y > top + height)
            return null;

        if (!Matrix4x4.Invert(viewProj, out var inverse))
            return null;

        // Screen point to a world ray. Uses the same mapping the camera gizmos use.
        float ndcX = (cursor.X - left) / width * 2f - 1f;
        float ndcY = (cursor.Y - top) / height * 2f - 1f;
        var nearH = Vector4.Transform(new Vector4(ndcX, ndcY, 0f, 1f), inverse);
        var farH = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), inverse);
        var rayStart = new Vector3(nearH.X, nearH.Y, nearH.Z) / nearH.W;
        var rayEnd = new Vector3(farH.X, farH.Y, farH.Z) / farH.W;
        var dir = Vector3.Normalize(rayEnd - rayStart);

        var spawn = rayStart + dir * 10f;
        if (MathF.Abs(dir.Y) > 1e-4f)
        {
            float t = -rayStart.Y / dir.Y;
            if (t > 0f && t < 500f)
                spawn = rayStart + dir * t;
        }
        return spawn;
    }

    // Makes an object that plays a sound dragged from the asset panel onto the scene, at the drop point.
    private void DropSoundIntoScene(string relPath, float2 cursor, float4 viewport, Matrix4x4 viewProj)
    {
        if (DropPoint(cursor, viewport, viewProj) is not { } spawn)
            return;

        var obj = new SceneObject { Name = Path.GetFileNameWithoutExtension(relPath), Position = spawn };
        obj.Components.Add(new AudioSource { Clip = relPath });
        _scene.Add(obj);
        _selectedId = obj.Id;
        Console.WriteLine($"Placed {relPath} in the scene as an Audio Source.");
    }

    // An empty object with an Audio Source, picked from the hierarchy's right-click menu.
    private void AddAudioSourceObject()
    {
        var obj = new SceneObject { Name = "Audio Source" };
        obj.Components.Add(new AudioSource());
        _scene.Add(obj);
        _selectedId = obj.Id;
    }

    // Places a model dragged from the asset panel where the mouse was released over the scene.
    // Released anywhere else, nothing happens.
    private void DropModelIntoScene(BrutalRenderer renderer, string relPath, float2 cursor, float4 viewport,
        Matrix4x4 viewProj)
    {
        if (DropPoint(cursor, viewport, viewProj) is not { } spawn)
            return;

        try
        {
            if (!_uploaded.Contains(relPath))
            {
                renderer.UploadModel(relPath, _assets.GetModel(relPath));
                _uploaded.Add(relPath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not load {relPath}: {ex.Message}");
            return;
        }

        var obj = new SceneObject { Name = Path.GetFileNameWithoutExtension(relPath), Position = spawn };
        obj.Components.Add(new MeshRenderer { Model = relPath });
        _scene.Add(obj);
        _selectedId = obj.Id;
        Console.WriteLine($"Placed {relPath} in the scene.");
    }

    // The scene area in ImGui pixels: x, y, width, height. Game UI is placed inside it.
    private float4 SceneArea(VulkanContext ctx)
    {
        var vp = ctx.SceneViewport;
        var display = ImGui.GetIO().DisplaySize;
        return new float4(vp.X * display.X, vp.Y * display.Y, vp.Z * display.X, vp.W * display.Y);
    }

    // Adds a UI element as its own object, so it appears in the hierarchy like any other object.
    private void AddUi(UiKind kind)
    {
        int n = _scene.All.Count(o => o.Components.OfType<UiElement>().Any()) + 1;
        var ui = new UiElement
        {
            Kind = kind,
            Label = kind switch
            {
                UiKind.Button => "Button",
                UiKind.TextField => "Text Field",
                UiKind.Checkbox => "Checkbox",
                UiKind.Dropdown => "Dropdown",
                _ => "Bar",
            },
            Y = 0.1f + 0.09f * ((n - 1) % 8),
            H = kind == UiKind.Bar ? 0.03f : 0.07f,
        };
        var obj = new SceneObject { Name = $"{kind} {n}" };
        obj.Components.Add(ui);
        _scene.Add(obj);
        _selectedId = obj.Id;
    }

    // Inspector section for a UI element: its label, screen rectangle, and kind-specific values.
    private void DrawUiInspector(SceneObject obj, UiElement ui, bool editing)
    {
        var g = _gui;
        string label = ui.Label;
        if (g.InputText("Label", ref label) && editing)
            ui.Label = label;

        float x = ui.X, y = ui.Y, w = ui.W, h = ui.H;
        if (g.DragFloat("X", ref x, 0.005f, 0f, 1f) && editing) ui.X = x;
        if (g.DragFloat("Y", ref y, 0.005f, 0f, 1f) && editing) ui.Y = y;
        if (g.DragFloat("Width", ref w, 0.005f, 0.01f, 1f) && editing) ui.W = w;
        if (g.DragFloat("Height", ref h, 0.005f, 0.01f, 1f) && editing) ui.H = h;

        bool visible = ui.Visible;
        if (g.Checkbox("Visible", ref visible) && editing)
            ui.Visible = visible;
        if (ui.Kind != UiKind.Bar)
        {
            bool interactable = ui.Interactable;
            if (g.Checkbox("Interactable", ref interactable) && editing)
                ui.Interactable = interactable;
        }

        if (ui.Kind is UiKind.Button or UiKind.Dropdown)
        {
            bool center = ui.CenterText;
            if (g.Checkbox("Center Text", ref center) && editing)
                ui.CenterText = center;
        }

        switch (ui.Kind)
        {
            case UiKind.TextField:
            {
                string text = ui.Text;
                if (g.InputText("Default text", ref text) && editing)
                    ui.Text = text;
                break;
            }
            case UiKind.Checkbox:
            {
                bool isChecked = ui.Checked;
                if (g.Checkbox("Checked", ref isChecked) && editing)
                    ui.Checked = isChecked;
                break;
            }
            case UiKind.Dropdown:
                DrawOptionsEditor(obj, ui, editing);
                break;
            case UiKind.Bar:
            {
                float value = ui.Value;
                if (g.DragFloat("Value", ref value, 0.01f, 0f, 1f) && editing)
                    ui.Value = value;
                break;
            }
        }
    }

    // Dropdown options are edited as one comma-separated line. Apply turns the line into the list.
    private void DrawOptionsEditor(SceneObject obj, UiElement ui, bool editing)
    {
        var g = _gui;
        if (!_optionText.TryGetValue(obj.Id, out var text))
            text = string.Join(", ", ui.Options);
        g.InputText("Options", ref text, 512);
        _optionText[obj.Id] = text;

        if (editing && g.Button("Apply Options"))
        {
            ui.Options = text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (ui.Options.Count == 0)
                ui.Options.Add("Option 1");
            ui.Selected = Math.Clamp(ui.Selected, 0, ui.Options.Count - 1);
        }
    }

    // Text box for one string. The buffer is rebuilt from the value each frame, so typing flows straight back.

    // Parent row in the Inspector. Change Parent opens a list. Reparenting keeps the object where it is.
    private void DrawParentPicker(SceneObject obj, bool editing)
    {
        var g = _gui;
        string parentName = obj.Parent is null ? "None" : SafeLabel.Of(obj.Parent.Name);
        g.TextDisabled($"Parent: {parentName}");
        if (!editing) return;

        g.SameLine();
        if (g.Button(_pickingParent ? "Close##parent" : "Change##parent"))
            _pickingParent = !_pickingParent;
        if (!_pickingParent) return;

        if (g.Selectable("None##parent-none", obj.Parent is null))
        {
            _scene.SetParent(obj, null);
            _pickingParent = false;
            return;
        }

        int i = 0;
        foreach (var other in _scene.All.ToList())
        {
            if (!_scene.CanParent(obj, other)) continue;
            if (g.Selectable($"{SafeLabel.Of(other.Name)}##parent{i++}", other == obj.Parent))
            {
                _scene.SetParent(obj, other);
                _pickingParent = false;
                return;
            }
        }
    }

    // An empty object placed under the selected object. Its transform is relative to that object.
    private void AddChild()
    {
        if (_selected is null) return;
        var obj = new SceneObject { Name = $"Child {_scene.All.Count() + 1}" };
        _scene.Add(obj, _selected);
        _selectedId = obj.Id;
    }

    // How many parents an object has. Used to indent the hierarchy.
    private static int Depth(SceneObject obj)
    {
        int depth = 0;
        for (var p = obj.Parent; p is not null; p = p.Parent)
            depth++;
        return depth;
    }

    // Called for each hierarchy row. Remembers the row's screen rectangle and starts a press when the mouse
    // goes down inside it.
    private bool IsSelected(SceneObject obj) => obj.Id == _selectedId || _extraSelected.Contains(obj.Id);

    // The selected objects other than the primary one.
    private List<SceneObject> SelectedExtras() =>
        _extraSelected.Select(id => _scene.Find(id)).OfType<SceneObject>().ToList();

    // The selected objects that have no selected ancestor. Acting on these covers the whole selection exactly once,
    // because each one carries its children along.
    private List<SceneObject> SelectedRoots()
    {
        var selected = _scene.InHierarchyOrder.Where(IsSelected).ToList();
        return selected.Where(o => !Ancestors(o).Any(IsSelected)).ToList();
    }

    // A click selects just that row. Shift-click selects every row from the last clicked row to this one.
    // Cmd-click (Ctrl-click on Windows and Linux) adds the row to the selection, or takes it out if already selected.
    // The row clicked last becomes the primary selection, so the Inspector shows it and the gizmo sits on it.
    private void ClickHierarchyRow(SceneObject obj, bool shift, bool toggle)
    {
        if (shift && _selectionAnchor is { } anchorId && _scene.Find(anchorId) is not null)
        {
            var rows = _scene.InHierarchyOrder.ToList();
            int from = rows.FindIndex(o => o.Id == anchorId), to = rows.IndexOf(obj);
            _extraSelected.Clear();
            foreach (var row in rows.GetRange(Math.Min(from, to), Math.Abs(to - from) + 1))
                if (row != obj)
                    _extraSelected.Add(row.Id);
            _selectedId = obj.Id;
        }
        else if (!toggle || _selectedId is null)
        {
            _extraSelected.Clear();
            _selectedId = obj.Id;
            _selectionAnchor = obj.Id;
        }
        else if (obj.Id == _selectedId)
        {
            // Deselect the primary; the most recently added extra (if any) takes its place.
            _selectedId = _extraSelected.Count > 0 ? _extraSelected[^1] : null;
            if (_selectedId is { } next) _extraSelected.Remove(next);
        }
        else if (!_extraSelected.Remove(obj.Id))
        {
            _extraSelected.Add(_selectedId.Value);
            _selectedId = obj.Id;
        }
        if (toggle && !shift)
            _selectionAnchor = obj.Id;
        _extraFor = _selectedId;
        _selected = _selectedId is { } id ? _scene.Find(id) : null;
    }

    private void TrackHierarchyRow(SceneObject obj, bool editing)
    {
        if (!editing) return;
        _hierRows[obj.Id] = _gui.LastItem;
        if (_gui.MousePressed && _gui.LastItemHovered)
        {
            _hierPressId = obj.Id;
            _hierPressCursor = _gui.Mouse;
        }
    }

    // Runs after all rows. A press that moves past a few pixels starts a drag. Releasing over another row
    // parents the dragged object to that row, keeping its world position.
    private void FinishHierarchyDrag(bool editing)
    {
        var g = _gui;
        _hierReleasedId = null;
        if (_hierDragId is null && _hierPressId is { } pressed && g.MouseDown)
        {
            float dx = g.Mouse.X - _hierPressCursor.X, dy = g.Mouse.Y - _hierPressCursor.Y;
            if (MathF.Sqrt(dx * dx + dy * dy) > 6f)
            {
                _hierDragId = pressed;
                _hierPressId = null;
            }
        }

        if (!g.MouseDown)
        {
            // Let go anywhere else: an object field in the Inspector (drawn after this) may take the object.
            if (_hierDragId is { } released && RowUnder(g.Mouse) is null)
                _hierReleasedId = released;
            if (editing && _hierDragId is { } dragId && RowUnder(g.Mouse) is { } targetId && targetId != dragId)
            {
                var child = _scene.Find(dragId);
                var target = _scene.Find(targetId);
                if (child is not null && target is not null && _scene.CanParent(child, target))
                {
                    _scene.SetParent(child, target);
                    _selectedId = child.Id;
                }
            }
            _hierDragId = null;
            _hierPressId = null;
        }

        if (_hierDragId is { } active && _scene.Find(active) is { } dragging)
            g.DragLabel(SafeLabel.Of(dragging.Name));
    }

    // The row whose rectangle contains the point, if any.
    private Guid? RowUnder(float2 point)
    {
        foreach (var (id, rect) in _hierRows)
            if (rect.Contains(point))
                return id;
        return null;
    }

    // A light on its own object, placed in front of the editor camera. pitch tilts it (a spot light aims down).
    private void AddLightObject(string name, Component light, float pitch)
    {
        var obj = new SceneObject
        {
            Name = name,
            Position = _camera.Position + _camera.Forward * 6f + (light is DirectionalLight ? Vector3.Zero : Vector3.UnitY),
            Rotation = Quaternion.CreateFromYawPitchRoll(0f, pitch * MathF.PI / 180f, 0f),
        };
        obj.Components.Add(light);
        _scene.Add(obj);
        _selectedId = obj.Id;
    }

    // The scene's sky and ambient light, on its own object. Only the first one in the scene is used.
    private void AddEnvironmentObject()
    {
        var obj = new SceneObject { Name = "Environment" };
        obj.Components.Add(new SceneEnvironment());
        _scene.Add(obj);
        _selectedId = obj.Id;
    }

    // An empty object with a particle system, placed in front of the editor camera.
    private void AddParticleSystemObject(ParticlePreset? preset = null)
    {
        var obj = new SceneObject { Name = preset?.Name ?? "Particle System", Position = _camera.Position + _camera.Forward * 6f };
        obj.Components.Add(preset?.CreateSystem() ?? new ParticleSystem());
        _scene.Add(obj);
        _selectedId = obj.Id;
    }

    private void AddCamera()
    {
        var obj = new SceneObject { Name = $"Camera {_scene.All.Count() + 1}", Position = new Vector3(0, 2, 6) };
        obj.Components.Add(new CameraComponent());
        _scene.Add(obj);
        _selectedId = obj.Id;
    }

    // Duplicates every selected object, then selects the copies. An object whose parent (or any ancestor) is also
    // selected is not copied on its own, because it is already copied along with that ancestor.
    private void DuplicateSelected()
    {
        if (_selected is not { } primary) return;
        var roots = SelectedRoots();

        var copies = new List<SceneObject>();
        SceneObject? primaryCopy = null;
        foreach (var source in roots)
        {
            var copy = SceneSerializer.Duplicate(_scene, source);
            copies.Add(copy);
            if (source == primary || Ancestors(primary).Contains(source))
                primaryCopy = copy;
        }

        primaryCopy ??= copies[^1];
        _extraSelected.Clear();
        foreach (var copy in copies)
            if (copy != primaryCopy)
                _extraSelected.Add(copy.Id);
        _selectedId = primaryCopy.Id;
        _selected = primaryCopy;
        _extraFor = _selectedId;
        Console.WriteLine(copies.Count == 1
            ? $"Duplicated {roots[0].Name} as {copies[0].Name}."
            : $"Duplicated {copies.Count} objects: {string.Join(", ", copies.Select(c => c.Name))}.");
    }

    private static IEnumerable<SceneObject> Ancestors(SceneObject obj)
    {
        for (var p = obj.Parent; p is not null; p = p.Parent)
            yield return p;
    }

    // Deletes every selected object along with its children.
    private void DeleteSelected()
    {
        if (_selected is null) return;
        foreach (var obj in SelectedRoots())
            _scene.Remove(obj);
        _selectedId = null;
        _selected = null;
        _extraSelected.Clear();
    }

    private string? FirstModel()
    {
        var modelsDir = Path.Combine(_contentRoot, "Models");
        if (!Directory.Exists(modelsDir)) return null;
        return Directory.GetFiles(modelsDir)
            .Where(ModelLoader.IsModel)
            .OrderBy(f => f)
            .Select(f => Path.GetRelativePath(_contentRoot, f).Replace('\\', '/'))
            .FirstOrDefault();
    }

    // Loads a scene from the undo history. Selection is kept by ID when the object still exists.
    private void ApplyHistory(string? json, string action)
    {
        if (json is null)
        {
            Console.WriteLine($"Nothing to {action.ToLowerInvariant()}.");
            return;
        }
        _scene = SceneSerializer.FromJson(json, action.ToLowerInvariant());
        _selected = _selectedId is { } id ? _scene.Find(id) : null;
        if (_selected is null) _selectedId = null;
        _optionText.Clear();       // reload dropdown option text from the restored scene
    }

    private void TogglePlay()
    {
        if (_mode == EditorMode.Edit)
        {
            if (GameCamera() is null)
            {
                Console.WriteLine("Add a camera to the scene before playing.");
                return;
            }

            // Scene loading and saved games work from the project folder, and sounds are read fresh from disk.
            SceneManager.Begin(_scene, Path.GetFileNameWithoutExtension(_scenePath), _contentRoot);
            AudioEngine.StopPreview();
            AudioEngine.ForgetClips();

            var scriptErrors = CompileScripts();
            if (scriptErrors.Length > 0)
            {
                Console.WriteLine("Scripts did not compile, so play mode did not start:\n" + scriptErrors);
                return;
            }

            SceneSerializer.Save(_scene, _snapshotPath);
            Particles.Reset(_scene);   // play starts from a clean slate, like the built game
            Animators.Reset(_scene);
            _uiMode = false;
            _mode = EditorMode.Play;
            Console.WriteLine("Play mode. P to stop and restore the scene.");
        }
        else
        {
            VulkanScripts.Release(_scene);
            AudioEngine.StopAll();
            if (_window is { } w) MouseInput.Release(w);   // the game may have locked or hidden the cursor
            _scene = SceneSerializer.Load(_snapshotPath);
            _mode = EditorMode.Edit;
            Console.WriteLine("Edit mode. Scene restored.");
        }
    }

    private void Simulate(float dt) => SceneUpdate.Update(_scene, dt);

    private DirectionalLight? FindSun() =>
        _scene.All.SelectMany(o => o.Components.OfType<DirectionalLight>()).FirstOrDefault();

    // The camera play mode looks through: the first CameraComponent in the hierarchy.
    private (SceneObject Obj, CameraComponent Cam)? GameCamera()
    {
        foreach (var obj in _scene.All)
            if (obj.Components.OfType<CameraComponent>().FirstOrDefault() is { } cam)
                return (obj, cam);
        return null;
    }

    // Scenes without a camera get one, so play mode always has a view.
    private void EnsureCamera()
    {
        if (GameCamera() is not null) return;
        var obj = new SceneObject { Name = "Main Camera", Position = new Vector3(0, 2, 6) };
        obj.Components.Add(new CameraComponent());
        _scene.Add(obj);
    }

    // Scenes without a sun get one, so lighting always has a source.
    private void EnsureSun()
    {
        if (FindSun() is not null) return;
        var sun = new SceneObject { Name = "Sun" };
        sun.Components.Add(new DirectionalLight());
        _scene.Add(sun);
    }

    private IEnumerable<string> ModelKeys() =>
        _scene.All
            .SelectMany(o => o.Components.OfType<MeshRenderer>())
            .Select(c => c.Model)
            .Where(m => !string.IsNullOrEmpty(m))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private void FitCamera()
    {
        var positions = _scene.All.Select(o => o.Position).ToList();
        if (positions.Count == 0)
        {
            _camera.Position = new Vector3(0, 2, 6);
            _camera.LookAt(Vector3.Zero);
            return;
        }

        var center = Vector3.Zero;
        foreach (var p in positions) center += p;
        center /= positions.Count;
        float radius = Math.Max(4f, positions.Max(p => Vector3.Distance(p, center)) * 1.5f + 3f);

        _camera.Position = center + new Vector3(0, radius * 0.35f, radius);
        _camera.Focus = center;
        _camera.LookAt(center);
    }
}
