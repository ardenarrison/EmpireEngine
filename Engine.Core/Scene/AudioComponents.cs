using System.Numerics;
using Engine.Core.Audio;

namespace Engine.Core.Scene;

// How a 3D sound fades with distance from the listener.
public enum AudioRolloffMode
{
    Logarithmic,   // full volume inside MinDistance, then the volume falls as 1 / distance, like real sound
    Linear,        // falls in a straight line from full volume at MinDistance to silence at MaxDistance
}

// Plays a sound file from the project (.mp3 or .wav) while the scene plays. The settings are the ones Unity's
// Audio Source has. A sound is heard from the object's position when SpatialBlend is above 0, as the listener
// (the AudioListener, or else the first camera) would hear it.
public sealed class AudioSource : Component
{
    public string Clip { get; set; } = "";             // path relative to the project folder, like "Audio/Theme.mp3"
    public bool Mute { get; set; }
    public bool PlayOnAwake { get; set; } = true;      // starts when the scene starts playing
    public bool Loop { get; set; }
    public int Priority { get; set; } = 128;           // 0 is the most important and 256 the least; matters when too many sounds play
    public float Volume { get; set; } = 1f;            // 0 to 1
    public float Pitch { get; set; } = 1f;             // 1 is normal speed; -3 to 3, and a negative pitch plays backwards
    public float StereoPan { get; set; }               // -1 is all left and 1 all right, for a 2D sound
    public float SpatialBlend { get; set; }            // 0 is a 2D sound, 1 a 3D sound heard from the object's position

    // 3D sound settings. They matter as SpatialBlend rises above 0.
    public float DopplerLevel { get; set; } = 1f;      // 0 turns the pitch change of moving sounds off; 5 exaggerates it
    public float Spread { get; set; }                  // 0 to 360; widens the sound so it pans less (180 and up is centered)
    public AudioRolloffMode RolloffMode { get; set; } = AudioRolloffMode.Logarithmic;
    public float MinDistance { get; set; } = 1f;       // closer than this the sound is at full volume
    public float MaxDistance { get; set; } = 500f;     // where the sound stops getting quieter

    internal SceneObject? Host;             // the object this source is on, set by AudioEngine.Update and by scripts
    internal Voice? Voice;                  // the sound playing now, if any
    internal bool Awoke;                    // PlayOnAwake has been handled
    internal float ResumeAt = -1f;          // after a save game loads: start playing from this second (negative for none)
    internal int SeenFrame;                 // the last AudioEngine.Update that saw this source in a scene

    // True while the clip plays (also during a PlayDelayed wait), false when stopped, paused or finished.
    public bool IsPlaying => AudioEngine.IsPlaying(this);

    // Where playback is in the clip, in seconds. Setting it jumps there.
    public float Time
    {
        get => AudioEngine.GetTime(this);
        set => AudioEngine.SetTime(this, value);
    }

    // Length of the loaded clip in seconds, or 0 when it is not loaded.
    public float ClipLength => AudioEngine.LoadClip(Clip)?.Length ?? 0f;

    // Starts the clip from the beginning (or from the end when Pitch is negative), replacing what this source was playing.
    public void Play() => AudioEngine.Play(this, 0f);

    // The same after a wait of the given number of seconds.
    public void PlayDelayed(float seconds) => AudioEngine.Play(this, MathF.Max(0f, seconds));

    public void Stop() => AudioEngine.Stop(this);

    // Pauses where it is; UnPause carries on from there.
    public void Pause() => AudioEngine.SetPaused(this, true);

    public void UnPause() => AudioEngine.SetPaused(this, false);

    // Plays another clip once on top of whatever this source is playing, using this source's settings. volumeScale
    // multiplies the volume. Good for effects that can overlap, like footsteps or shots.
    public void PlayOneShot(string clip, float volumeScale = 1f) => AudioEngine.PlayOneShot(this, clip, volumeScale);

    // Plays a clip once from a position in the world, with no object needed.
    public static void PlayClipAtPoint(string clip, Vector3 position, float volume = 1f) =>
        AudioEngine.PlayAtPoint(clip, position, volume);
}

// Where the scene is heard from: 3D sounds are placed relative to this object's position and facing. Put it on the
// camera, as in Unity. A scene without one is heard from its first camera.
public sealed class AudioListener : Component
{
    // Master volume for everything, 0 to 1.
    public static float Volume { get; set; } = 1f;

    // True stops all sound where it is, and false carries on.
    public static bool Pause { get; set; }
}
