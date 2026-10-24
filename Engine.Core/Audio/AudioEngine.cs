using System.Numerics;
using Engine.Core.Scene;

namespace Engine.Core.Audio;

// One sound playing: a clip, a position in it, and the gains and speed the last update worked out.
internal sealed class Voice
{
    public AudioClip Clip = null!;
    public AudioSource? Owner;          // settings come from here each update; null for a fixed sound
    public double Position;             // in clip frames
    public double DelayFrames;          // output frames still to wait before the sound starts
    public bool Loop;
    public bool Paused;
    public bool Stopping;               // fading out over the next block, then removed
    public bool Finished;
    public bool Virtual;                // too many sounds: it keeps its place in the clip but is not mixed
    public bool Preview;                // an editor preview; not tied to a scene
    public bool OneShot;                // PlayOneShot: layered over the source's own sound
    public float Scale = 1f;            // extra volume factor (PlayOneShot, PlayClipAtPoint)
    public Vector3 FixedPosition;       // where a point sound plays
    public bool IsPointSound;           // a 3D sound with no source object
    public int Priority = 128;

    public float TargetLeft, TargetRight;   // gains for the next block
    public float CurrentLeft, CurrentRight; // where the last block ended, so gains ramp instead of clicking
    public float Step = 1f;                 // clip frames per output frame, before the clip's own sample rate
    public Vector3 LastPosition;
    public bool HasLastPosition;
}

// Mixes every playing sound to stereo and works out 3D position, distance fade, doppler and panning.
// Update runs once a frame on the game thread and Mix runs on the audio device's thread; they share one lock.
// Nothing here talks to a sound card: the host feeds Mix's output to a device (see Engine.Brutal's AudioOutput),
// which also keeps this testable without hardware.
public static class AudioEngine
{
    public const int MaxVoices = 64;          // more than this at once and the least important become silent
    public const float SpeedOfSound = 343f;   // world units per second, for doppler

    private static readonly object Gate = new();
    private static readonly List<Voice> Voices = new();
    private static readonly Dictionary<string, AudioClip?> Clips = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Warned = new();
    private static int _frame;
    private static Vector3 _listenerPosition, _listenerVelocity, _lastListenerPosition;
    private static Vector3 _listenerRight = Vector3.UnitX;
    private static bool _hasLastListener;
    private static Voice? _preview;

    // The output device's sample frames per second. The host sets this when it opens the device.
    public static int SampleRate { get; set; } = 48000;

    // Reads a clip by its path relative to the project folder. The editor and the player set it, as they set the
    // model loader; without one, paths are read as they are.
    public static Func<string, AudioClip?>? ClipLoader { get; set; }

    // Sounds playing right now, for debugging and tests.
    public static int ActiveVoices { get { lock (Gate) return Voices.Count; } }

    // The clip at a path, loaded once. Null when the path is empty or the file cannot be read (a warning is printed once).
    public static AudioClip? LoadClip(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        lock (Clips)
        {
            if (Clips.TryGetValue(path, out var cached)) return cached;
            AudioClip? clip = null;
            try
            {
                clip = ClipLoader is { } loader ? loader(path) : AudioClip.Load(path);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                Warn($"Could not load sound '{path}': {ex.Message}");
            }
            return Clips[path] = clip;
        }
    }

    // Forgets loaded clips, so changed files are read again. The editor calls it when play starts.
    public static void ForgetClips()
    {
        lock (Clips) Clips.Clear();
    }

    private static void Warn(string message)
    {
        lock (Warned)
            if (Warned.Add(message))
                Console.WriteLine(message);
    }

    // ---- control, called from AudioSource ----------------------------------------------

    internal static bool IsPlaying(AudioSource source)
    {
        lock (Gate) return source.Voice is { Paused: false, Finished: false, Stopping: false };
    }

    internal static float GetTime(AudioSource source)
    {
        lock (Gate) return source.Voice is { } v ? (float)(v.Position / v.Clip.SampleRate) : 0f;
    }

    internal static void SetTime(AudioSource source, float seconds)
    {
        lock (Gate)
            if (source.Voice is { } v)
                v.Position = Math.Clamp((double)seconds * v.Clip.SampleRate, 0, Math.Max(0, v.Clip.FrameCount - 1));
    }

    internal static void Play(AudioSource source, float delaySeconds)
    {
        var clip = LoadClip(source.Clip);
        lock (Gate)
        {
            Detach(source);
            source.Awoke = true;
            if (clip is null) return;
            var voice = new Voice
            {
                Clip = clip,
                Owner = source,
                Loop = source.Loop,
                Priority = source.Priority,
                DelayFrames = delaySeconds * SampleRate,
                // A negative pitch plays backwards, so it starts at the end.
                Position = source.Pitch < 0f ? clip.FrameCount - 1 : 0,
            };
            source.Voice = voice;
            Voices.Add(voice);
            Prepare(voice, source.Host, 1f / 60f);
        }
    }

    internal static void Stop(AudioSource source)
    {
        lock (Gate) Detach(source);
    }

    internal static void SetPaused(AudioSource source, bool paused)
    {
        lock (Gate)
            if (source.Voice is { } v)
                v.Paused = paused;
    }

    internal static void PlayOneShot(AudioSource source, string clipPath, float scale)
    {
        var clip = LoadClip(clipPath);
        if (clip is null) return;
        lock (Gate)
        {
            var voice = new Voice { Clip = clip, Owner = source, OneShot = true, Scale = scale, Priority = source.Priority };
            Voices.Add(voice);
            Prepare(voice, source.Host, 1f / 60f);
        }
    }

    internal static void PlayAtPoint(string clipPath, Vector3 position, float volume)
    {
        var clip = LoadClip(clipPath);
        if (clip is null) return;
        lock (Gate)
        {
            var voice = new Voice { Clip = clip, Scale = volume, FixedPosition = position, IsPointSound = true };
            Voices.Add(voice);
            Prepare(voice, null, 1f / 60f);
        }
    }

    // Plays a clip as a plain 2D sound, outside any scene, for the editor's preview button. One preview at a time.
    public static bool PlayPreview(string clipPath, float volume = 1f, float pitch = 1f)
    {
        var clip = LoadClip(clipPath);
        if (clip is null) return false;
        lock (Gate)
        {
            StopPreviewLocked();
            float gain = Math.Clamp(volume, 0f, 1f) * AudioListener.Volume;
            _preview = new Voice
            {
                Clip = clip,
                Preview = true,
                Step = pitch == 0f ? 1f : pitch,
                TargetLeft = gain,
                TargetRight = gain,
                CurrentLeft = gain,
                CurrentRight = gain,
            };
            Voices.Add(_preview);
        }
        return true;
    }

    public static void StopPreview()
    {
        lock (Gate) StopPreviewLocked();
    }

    public static bool IsPreviewing
    {
        get { lock (Gate) return _preview is { Finished: false, Stopping: false }; }
    }

    private static void StopPreviewLocked()
    {
        if (_preview is { } p) p.Stopping = true;
        _preview = null;
    }

    // Stops a source's sound. The voice fades out over the next block so there is no click.
    private static void Detach(AudioSource source)
    {
        if (source.Voice is { } v)
        {
            v.Stopping = true;
            v.Owner = null;
        }
        source.Voice = null;
    }

    // Stops everything. The editor calls it when play stops.
    public static void StopAll()
    {
        lock (Gate)
        {
            foreach (var v in Voices)
            {
                if (v.Owner is { } o && o.Voice == v) o.Voice = null;
                v.Finished = true;
            }
            Voices.Clear();
            _preview = null;
            _hasLastListener = false;
        }
    }

    // Start of play: stops sounds and lets every source in the scene wake up again.
    public static void Reset(Scene.Scene scene)
    {
        StopAll();
        foreach (var obj in scene.All)
            foreach (var source in obj.Components.OfType<AudioSource>())
            {
                source.Voice = null;
                source.Awoke = false;
                source.Host = obj;
            }
    }

    // ---- once per frame, on the game thread --------------------------------------------

    public static void Update(Scene.Scene scene, float dt)
    {
        lock (Gate)
        {
            _frame++;
            UpdateListener(scene, dt);

            foreach (var obj in scene.All)
                foreach (var source in obj.Components.OfType<AudioSource>())
                {
                    source.SeenFrame = _frame;
                    source.Host = obj;
                    if (source.ResumeAt >= 0f)
                    {
                        // A loaded save game: carry on from where the sound was.
                        float at = source.ResumeAt;
                        source.ResumeAt = -1f;
                        Play(source, 0f);
                        SetTime(source, at);
                    }
                    else if (!source.Awoke)
                    {
                        source.Awoke = true;
                        if (source.PlayOnAwake && source.Clip.Length > 0)
                            Play(source, 0f);
                    }
                }

            for (int i = Voices.Count - 1; i >= 0; i--)
            {
                var v = Voices[i];
                if (v.Finished)
                {
                    if (v.Owner is { } finishedOwner && finishedOwner.Voice == v) finishedOwner.Voice = null;
                    Voices.RemoveAt(i);
                    continue;
                }
                if (v.Preview || v.Stopping) continue;

                if (v.Owner is { } owner)
                {
                    // Sounds from objects that left the scene stop (for example when a scene unloads).
                    if (owner.SeenFrame != _frame)
                    {
                        if (owner.Voice == v) owner.Voice = null;
                        v.Owner = null;
                        v.Stopping = true;
                        continue;
                    }
                    v.Loop = !v.OneShot && owner.Voice == v && owner.Loop;
                }
                Prepare(v, v.Owner?.Host, dt);
            }

            LimitVoices();
        }
    }

    private static void UpdateListener(Scene.Scene scene, float dt)
    {
        SceneObject? at = null;
        foreach (var obj in scene.All)
            if (obj.Components.OfType<AudioListener>().Any()) { at = obj; break; }
        if (at is null)
            foreach (var obj in scene.All)
                if (obj.Components.OfType<CameraComponent>().Any()) { at = obj; break; }

        var world = at?.WorldMatrix ?? Matrix4x4.Identity;
        _listenerPosition = world.Translation;
        var right = Vector3.TransformNormal(Vector3.UnitX, world);
        _listenerRight = right.LengthSquared() > 1e-10f ? Vector3.Normalize(right) : Vector3.UnitX;
        _listenerVelocity = _hasLastListener && dt > 1e-5f ? (_listenerPosition - _lastListenerPosition) / dt : Vector3.Zero;
        _lastListenerPosition = _listenerPosition;
        _hasLastListener = true;
    }

    // Works out a voice's gains and speed from its source's settings and where the listener is.
    private static void Prepare(Voice v, SceneObject? sourceObject, float dt)
    {
        var s = v.Owner;
        float volume = s?.Volume ?? 1f;
        float pitch = s?.Pitch ?? 1f;
        float pan = s?.StereoPan ?? 0f;
        float blend = s?.SpatialBlend ?? (v.IsPointSound ? 1f : 0f);
        float doppler = s?.DopplerLevel ?? 1f;
        float spread = s?.Spread ?? 0f;
        float min = s?.MinDistance ?? 1f;
        float max = s?.MaxDistance ?? 500f;
        var rolloff = s?.RolloffMode ?? AudioRolloffMode.Logarithmic;
        bool mute = s?.Mute ?? false;

        Vector3 position = sourceObject?.WorldMatrix.Translation ?? v.FixedPosition;
        float gain = Math.Clamp(volume, 0f, 1f) * Math.Max(0f, v.Scale);
        float dopplerFactor = 1f;
        float spatialPan = 0f;
        float attenuation = 1f;

        blend = Math.Clamp(blend, 0f, 1f);
        if (blend > 0f)
        {
            var rel = position - _listenerPosition;
            float dist = rel.Length();
            min = MathF.Max(min, 1e-4f);
            max = MathF.Max(max, min + 1e-4f);
            attenuation = rolloff == AudioRolloffMode.Linear
                ? 1f - Math.Clamp((dist - min) / (max - min), 0f, 1f)
                : min / Math.Clamp(dist, min, max);
            if (dist > 1e-4f)
            {
                var toSource = rel / dist;
                spatialPan = Vector3.Dot(toSource, _listenerRight) * Math.Clamp(1f - spread / 180f, 0f, 1f);

                // The sound's speed away from the listener and the listener's speed toward it, along the line between them.
                var sourceVelocity = v.HasLastPosition && dt > 1e-5f ? (position - v.LastPosition) / dt : Vector3.Zero;
                float d = Math.Clamp(doppler, 0f, 5f);
                float listenerToward = Math.Min(Vector3.Dot(_listenerVelocity, toSource) * d, SpeedOfSound * 0.9f);
                float sourceAway = Math.Max(Vector3.Dot(sourceVelocity, toSource) * d, -SpeedOfSound * 0.9f);
                dopplerFactor = (SpeedOfSound + listenerToward) / (SpeedOfSound + sourceAway);
            }
        }
        v.LastPosition = position;
        v.HasLastPosition = true;

        float panAmount = Math.Clamp(pan + (spatialPan - pan) * blend, -1f, 1f);
        gain *= 1f + (attenuation - 1f) * blend;
        if (mute) gain = 0f;

        v.TargetLeft = gain * MathF.Min(1f, 1f - panAmount);
        v.TargetRight = gain * MathF.Min(1f, 1f + panAmount);
        float speed = pitch * (1f + (dopplerFactor - 1f) * blend);
        v.Step = float.IsFinite(speed) ? speed : 1f;
        if (s is not null) v.Priority = s.Priority;
    }

    // With more than MaxVoices sounds, the least important (a higher Priority number, then the quietest) go virtual:
    // they keep their place in the clip so they can come back, but cost nothing to mix.
    private static void LimitVoices()
    {
        if (Voices.Count <= MaxVoices)
        {
            foreach (var v in Voices) v.Virtual = false;
            return;
        }
        var ranked = Voices.Where(v => !v.Stopping && !v.Preview)
            .OrderBy(v => v.Priority)
            .ThenByDescending(v => MathF.Max(v.TargetLeft, v.TargetRight))
            .ToList();
        for (int i = 0; i < ranked.Count; i++)
            ranked[i].Virtual = i >= MaxVoices;
    }

    // ---- on the audio thread -------------------------------------------------------------

    // Mixes the next `frames` frames of every sound into output as interleaved stereo floats.
    public static void Mix(Span<float> output, int frames)
    {
        output[..(frames * 2)].Clear();
        lock (Gate)
        {
            if (AudioListener.Pause) return;
            float master = Math.Clamp(AudioListener.Volume, 0f, 1f);
            for (int i = Voices.Count - 1; i >= 0; i--)
            {
                var v = Voices[i];
                MixVoice(v, output, frames, master);
                if (v.Finished)
                {
                    if (v.Owner is { } o && o.Voice == v) o.Voice = null;
                    Voices.RemoveAt(i);
                }
            }
        }

        for (int i = 0; i < frames * 2; i++)
            output[i] = Math.Clamp(output[i], -1f, 1f);
    }

    private static void MixVoice(Voice v, Span<float> output, int frames, float master)
    {
        var clip = v.Clip;
        int count = clip.FrameCount;
        var samples = clip.Samples;
        bool stereo = clip.Channels == 2;
        double step = v.Step * clip.SampleRate / (double)SampleRate;

        // A pause or a stop fades to silence over this block. A paused voice that has faded out is skipped.
        bool silent = v.Stopping || v.Paused;
        if (v.Paused && !v.Stopping && v.CurrentLeft == 0f && v.CurrentRight == 0f) return;
        float scale = v.Preview ? 1f : master;
        float targetLeft = silent ? 0f : v.TargetLeft * scale;
        float targetRight = silent ? 0f : v.TargetRight * scale;

        double position = v.Position;
        float left0 = v.CurrentLeft, right0 = v.CurrentRight;
        for (int f = 0; f < frames; f++)
        {
            if (v.DelayFrames > 0)
            {
                v.DelayFrames--;
                continue;
            }

            if (position >= count || position < 0)
            {
                if (!v.Loop)
                {
                    v.Finished = true;
                    break;
                }
                position %= count;
                if (position < 0) position += count;
            }

            if (!v.Virtual)
            {
                int i0 = (int)position;
                float frac = (float)(position - i0);
                int i1 = i0 + 1 >= count ? (v.Loop ? 0 : i0) : i0 + 1;
                float l, r;
                if (stereo)
                {
                    l = (samples[i0 * 2] + (samples[i1 * 2] - samples[i0 * 2]) * frac) * (1f / 32768f);
                    r = (samples[i0 * 2 + 1] + (samples[i1 * 2 + 1] - samples[i0 * 2 + 1]) * frac) * (1f / 32768f);
                }
                else
                {
                    l = r = (samples[i0] + (samples[i1] - samples[i0]) * frac) * (1f / 32768f);
                }
                float t = (f + 1f) / frames;
                output[f * 2] += l * (left0 + (targetLeft - left0) * t);
                output[f * 2 + 1] += r * (right0 + (targetRight - right0) * t);
            }
            position += step;
        }
        v.Position = position;
        v.CurrentLeft = targetLeft;
        v.CurrentRight = targetRight;
        if (v.Stopping) v.Finished = true;
    }
}
