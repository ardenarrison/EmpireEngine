namespace Engine.Core.Scripting;

// Timing for scripts and animation. Values update once per frame while the scene plays.
public static class Time
{
    public static float deltaTime;          // seconds since the last frame, multiplied by timeScale
    public static float unscaledDeltaTime;  // seconds since the last frame, not affected by timeScale
    public static float timeScale = 1f;     // 0 freezes time, 0.5 runs at half speed, 2 at double speed
    public static float time;               // scaled seconds since play started
    public static float fps;                // frames per second, smoothed so it reads steadily; 0 before the first frame

    private const float FpsSmoothing = 0.1f;   // share of each new frame in the average
    private static float _averageFrame;

    internal static void Advance(float unscaled)
    {
        unscaledDeltaTime = unscaled;
        deltaTime = unscaled * timeScale;
        time += deltaTime;

        if (unscaled > 0f)
        {
            _averageFrame = _averageFrame <= 0f ? unscaled : _averageFrame + (unscaled - _averageFrame) * FpsSmoothing;
            fps = 1f / _averageFrame;
        }
    }

    internal static void Reset()
    {
        deltaTime = 0f;
        unscaledDeltaTime = 0f;
        time = 0f;
        timeScale = 1f;
        fps = 0f;
        _averageFrame = 0f;
    }
}
