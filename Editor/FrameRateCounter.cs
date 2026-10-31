namespace Editor;

// Frames per second for the FPS counter in the scene view. It counts frames over half a second and reports their average, so
// the number is steady enough to read instead of changing every frame.
public sealed class FrameRateCounter
{
    private const double Period = 0.5;

    private double _periodStart = double.NaN;
    private int _frames;

    public double Fps { get; private set; }
    public double FrameMilliseconds { get; private set; }

    // Call once per frame with the time in seconds.
    public void Tick(double now)
    {
        if (double.IsNaN(_periodStart))
        {
            _periodStart = now;
            return;
        }
        _frames++;
        double elapsed = now - _periodStart;
        if (elapsed < Period) return;
        Fps = _frames / elapsed;
        FrameMilliseconds = elapsed * 1000.0 / _frames;
        _frames = 0;
        _periodStart = now;
    }
}
