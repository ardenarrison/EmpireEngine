using Engine.Core.Animation;
using Engine.Core.Assets;

namespace Engine.Core.Scene;

// One animation an Animator can play: a clip from a model file, under an id that scripts use to play it.
public sealed class AnimationEntry
{
    public string Id { get; set; } = "";
    public string Model { get; set; } = "";    // the file the clip is in, relative to the project folder, like "Models/Hero.glb"
    public string Clip { get; set; } = "";     // the animation's name inside that file
    public bool PlayAtStart { get; set; }      // starts when the scene starts playing
    public bool Loop { get; set; }             // starts over when it reaches the end; otherwise it stops on its last pose
    public float Speed { get; set; } = 1f;     // 1 is normal speed; 2 is twice as fast
}

// Plays animations on the model of this object's Mesh (or, failing that, the first mesh under it). Give it as many
// animations as you like, each with an id, and play them from a script by id: animator.Play("Walk").
// The clips come from glTF files (.glb, .gltf). A clip from another file than the model's works when the two use the
// same bone names, which is how animations are shared between characters.
public sealed class Animator : Component
{
    public List<AnimationEntry> Animations { get; } = new();

    // Multiplies the speed of everything this animator plays.
    public float Speed { get; set; } = 1f;

    // Reads a model file by its path in the project. The editor and the player set this to their asset cache.
    public static Func<string, GlbModel>? ModelLoader { get; set; }

    // ---- state while playing ----

    private sealed class Playback
    {
        public required AnimationEntry Entry { get; init; }
        public required ClipBinding Binding { get; init; }
        public float Time;
        public bool Finished;
    }

    private Playback? _current;
    private Playback? _fading;          // what was playing before, still showing while the new animation fades in
    private float _fade;                // how much of the fade is done, 0 to 1
    private float _fadeSeconds;
    private bool _paused;
    private bool _started;              // PlayAtStart has been handled (or a script chose what plays)
    private ModelPose? _pose;
    private MeshRenderer? _target;
    private readonly HashSet<string> _reported = new();   // problems already written to the console

    internal SceneObject? Host;         // the object this animator is on, set when it first updates and by scripts and the editor
    public bool Previewing { get; private set; }   // the editor is showing this animator while the scene is stopped
    internal string? ResumeId;          // after a save game loads: carry on with this animation at ResumeTime
    internal float ResumeTime;
    internal bool Started { get => _started; set => _started = value; }

    // The id of the animation playing now, or "" when none is (one that has finished is not playing).
    public string CurrentId => _current is { Finished: false } c ? c.Entry.Id : "";

    // True while any animation is playing, or while the one with this id is.
    public bool IsPlaying() => _current is { Finished: false } && !_paused;
    public bool IsPlaying(string id) => IsPlaying() && Same(_current!.Entry.Id, id);

    public bool IsPaused => _paused;

    // How far into the current animation it is, in seconds, and how long it is.
    public float Time
    {
        get => _current?.Time ?? 0f;
        set { if (_current is { } c) c.Time = Math.Clamp(value, 0f, Duration); }
    }
    public float Duration => _current?.Binding.Clip.Duration ?? 0f;

    // True when an animation with this id has been added.
    public bool Has(string id) => Find(id) is not null;

    // Starts the animation with this id from its beginning. With fadeSeconds above 0 it blends in over that time
    // instead of cutting, which looks better when switching between walking and running, say.
    public void Play(string id, float fadeSeconds = 0f)
    {
        var entry = Find(id) ?? throw new InvalidOperationException(
            $"This Animator has no animation with the id '{id}'" +
            (Animations.Count == 0 ? "; add one in the Inspector" : $". It has: {string.Join(", ", Animations.Select(a => $"'{a.Id}'"))}"));
        _started = true;
        Start(entry, fadeSeconds);
    }

    // Stops all animation and puts the model back in its resting pose.
    public void Stop()
    {
        _current = _fading = null;
        _paused = false;
        _pose = null;
        if (_target is not null) _target.Pose = null;
    }

    // Stops the animation with this id, if it is the one playing.
    public void Stop(string id)
    {
        if (_current is not null && Same(_current.Entry.Id, id)) Stop();
    }

    public void Pause() => _paused = true;
    public void Resume() => _paused = false;

    // ---- the editor's preview ----

    // The editor's preview: plays an animation while the scene is stopped.
    public void Preview(SceneObject host, string id)
    {
        Host = host;
        Previewing = true;
        Play(id);
    }

    public void StopPreview()
    {
        Previewing = false;
        Stop();
        _started = false;
    }

    // ---- internals ----

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private AnimationEntry? Find(string id) => Animations.FirstOrDefault(a => Same(a.Id, id));

    private void Start(AnimationEntry entry, float fadeSeconds)
    {
        var binding = Bind(entry);
        if (binding is null) return;   // already reported
        _paused = false;
        _fading = fadeSeconds > 0f && _current is not null ? _current : null;
        _fadeSeconds = MathF.Max(fadeSeconds, 0f);
        _fade = 0f;
        _current = new Playback { Entry = entry, Binding = binding };
    }

    // Matches an entry's clip to the skeleton it will move. Null (with one message in the console) when it can't be done.
    private ClipBinding? Bind(AnimationEntry entry)
    {
        if (Host is not null) _target = FindTarget(Host);   // a script can play before the first update
        var rig = _target is { Model.Length: > 0 } t ? Load(t.Model) : null;
        if (rig is null) { Report($"no-model", "An Animator needs a Mesh on the same object (or under it) to animate."); return null; }
        if (rig.Nodes.Count == 0) { Report("rig:" + _target!.Model, $"'{_target.Model}' has no skeleton or node tree, so it can't be animated."); return null; }
        if (entry.Model.Length == 0 || entry.Clip.Length == 0) { Report("empty:" + entry.Id, $"Animation '{entry.Id}' has no clip yet; drag one into it in the Inspector."); return null; }

        var source = ModelEquals(entry.Model, _target!.Model) ? rig : Load(entry.Model);
        var clip = source?.FindAnimation(entry.Clip);
        if (clip is null) { Report("clip:" + entry.Id, $"Animation '{entry.Id}': '{entry.Model}' has no animation named '{entry.Clip}'."); return null; }

        // Reuse the match from last time when the files haven't been reloaded.
        if (_current?.Entry == entry && ReferenceEquals(_current.Binding.Rig, rig) && ReferenceEquals(_current.Binding.Source, source))
            return _current.Binding;
        return ClipBinding.Create(rig, source!, clip);
    }

    private static bool ModelEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private GlbModel? Load(string path)
    {
        try
        {
            return ModelLoader?.Invoke(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Report("load:" + path, $"Could not load '{path}': {ex.Message}");
            return null;
        }
    }

    private void Report(string key, string message)
    {
        if (_reported.Add(key)) Console.WriteLine(message);
    }

    // The mesh this animator moves: its own object's, else the first one in the objects below.
    private static MeshRenderer? FindTarget(SceneObject host)
    {
        if (host.Components.OfType<MeshRenderer>().FirstOrDefault() is { } own) return own;
        foreach (var child in host.Children)
            if (FindTarget(child) is { } found) return found;
        return null;
    }

    // Moves time on and works out the pose. Called every frame by Animators.Update.
    internal void Update(SceneObject host, float dt)
    {
        Host = host;
        var target = FindTarget(host);
        if (!ReferenceEquals(target, _target))
        {
            if (_target is not null) _target.Pose = null;
            _target = target;
            _pose = null;
        }
        if (_target is null) return;

        if (!_started)
        {
            _started = true;
            if (ResumeId is { } resume && Find(resume) is { } saved)
            {
                Start(saved, 0f);
                if (_current is not null) _current.Time = ResumeTime;
            }
            else if (Animations.FirstOrDefault(a => a.PlayAtStart) is { } first)
                Start(first, 0f);
            ResumeId = null;
        }

        if (_current is null) return;

        if (!_paused)
        {
            Advance(_current, dt);
            if (_fading is not null)
            {
                Advance(_fading, dt);
                _fade += _fadeSeconds > 0f ? dt / _fadeSeconds : 1f;
                if (_fade >= 1f) _fading = null;
            }
        }

        // A clip played from a file that has since changed gets its match redone.
        var rig = Load(_target.Model);
        if (rig is null || rig.Nodes.Count == 0) return;
        if (!ReferenceEquals(_current.Binding.Rig, rig) || (_fading is not null && !ReferenceEquals(_fading.Binding.Rig, rig)))
        {
            var rebuilt = Bind(_current.Entry);
            if (rebuilt is null) { Stop(); return; }
            _current = new Playback { Entry = _current.Entry, Binding = rebuilt, Time = _current.Time, Finished = _current.Finished };
            _fading = null;
        }

        var layers = new PoseLayer[2];
        int count = 0;
        if (_fading is not null)
            layers[count++] = new PoseLayer(_fading.Binding, _fading.Time, 1f);
        layers[count++] = new PoseLayer(_current.Binding, _current.Time, _fading is not null ? _fade : 1f);
        _pose = PoseEvaluator.Evaluate(rig, layers.AsSpan(0, count), _pose);
        _target.Pose = _pose;
    }

    private void Advance(Playback p, float dt)
    {
        if (p.Finished) return;
        float duration = p.Binding.Clip.Duration;
        p.Time += dt * p.Entry.Speed * Speed;
        if (duration <= 0f) { p.Time = 0f; p.Finished = !p.Entry.Loop; return; }
        if (p.Entry.Loop)
        {
            p.Time %= duration;
            if (p.Time < 0f) p.Time += duration;
        }
        else if (p.Time >= duration)
        {
            p.Time = duration;
            p.Finished = true;
        }
        else if (p.Time < 0f)
        {
            p.Time = 0f;
            p.Finished = true;
        }
    }
}

// Runs every Animator in a scene. The editor and the built game both call this.
public static class Animators
{
    public static void Update(Scene scene, float dt)
    {
        foreach (var obj in scene.All)
            foreach (var animator in obj.Components.OfType<Animator>())
                animator.Update(obj, dt);
    }

    // The editor's preview while the scene is not playing: only animators the editor started move.
    public static void UpdatePreview(Scene scene, float dt)
    {
        foreach (var obj in scene.All)
            foreach (var animator in obj.Components.OfType<Animator>())
                if (animator.Previewing)
                    animator.Update(obj, dt);
    }

    // Play starts from a clean slate: previews end and every animator starts over.
    public static void Reset(Scene scene)
    {
        foreach (var obj in scene.All)
            foreach (var animator in obj.Components.OfType<Animator>())
                animator.StopPreview();
    }
}
