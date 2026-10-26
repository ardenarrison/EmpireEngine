using Engine.Core.Scene;

namespace Engine.Core.Scripting;

// Objects that scripts have asked to destroy. Taking an object out of the scene in the middle of a frame would pull it from
// under the loop that is running every script, so a request waits (until the end of the frame, or for the delay asked for)
// and SceneUpdate carries it out once everything else has run.
internal static class Destruction
{
    private static readonly object Gate = new();
    private static readonly List<(SceneObject Obj, float Left)> Pending = new();

    // Asks for obj to be destroyed after delay seconds (0 for the end of this frame). Asking again keeps the sooner time.
    internal static void Request(SceneObject? obj, float delay)
    {
        if (obj is null) return;
        delay = float.IsNaN(delay) ? 0f : Math.Max(0f, delay);
        lock (Gate)
        {
            int index = Pending.FindIndex(p => ReferenceEquals(p.Obj, obj));
            if (index < 0) Pending.Add((obj, delay));
            else if (delay < Pending[index].Left) Pending[index] = (obj, delay);
        }
    }

    // Counts the delays down by dt and takes every object that is due out of the scene, with its children. An object that
    // is not in the scene any more (destroyed twice, or from a scene that was replaced) is dropped.
    internal static void Apply(Scene.Scene scene, float dt)
    {
        List<SceneObject> due = new();
        lock (Gate)
        {
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                float left = Pending[i].Left - dt;
                if (left > 0f) { Pending[i] = (Pending[i].Obj, left); continue; }
                due.Add(Pending[i].Obj);
                Pending.RemoveAt(i);
            }
        }

        due.Reverse();   // in the order they were asked for
        foreach (var obj in due)
            if (scene.Contains(obj))
            {
                ScriptRuntime.NotifyDestroy(obj);   // while it is still in the scene, so it can look around and ask for more
                scene.Remove(obj);
            }
    }

    // A new game starts: nothing asked for in an earlier one carries over.
    internal static void Clear()
    {
        lock (Gate) Pending.Clear();
    }
}
