using System;

namespace Engine.Core.Scripting;

// Something a script coroutine can wait on. Update returns true once the wait has finished.
public abstract class Wait
{
    public abstract bool Update(float deltaTime);
}

internal sealed class TimerWait : Wait
{
    private float _remaining;
    public TimerWait(float seconds) => _remaining = seconds;
    public override bool Update(float deltaTime)
    {
        _remaining -= deltaTime;
        return _remaining <= 0f;
    }
}

internal sealed class PredicateWait : Wait
{
    private readonly Func<bool> _condition;
    public PredicateWait(Func<bool> condition) => _condition = condition;
    public override bool Update(float deltaTime) => _condition();
}

internal sealed class GlideWait : Wait
{
    private readonly Func<System.Numerics.Vector3> _get;
    private readonly Action<System.Numerics.Vector3> _set;
    private readonly System.Numerics.Vector3 _target;
    private readonly float _seconds;
    private System.Numerics.Vector3? _start;
    private float _elapsed;

    public GlideWait(Func<System.Numerics.Vector3> get, Action<System.Numerics.Vector3> set,
                     System.Numerics.Vector3 target, float seconds)
    {
        _get = get;
        _set = set;
        _target = target;
        _seconds = seconds;
    }

    public override bool Update(float deltaTime)
    {
        _start ??= _get();
        _elapsed += deltaTime;
        float t = _seconds <= 0f ? 1f : MathF.Min(1f, _elapsed / _seconds);
        _set(System.Numerics.Vector3.Lerp(_start.Value, _target, t));
        return t >= 1f;
    }
}
