using System.Numerics;

namespace Engine.Core.Scene;

public sealed class Scene
{
    private readonly List<SceneObject> _all = new();

    public string Name { get; set; } = "Untitled";

    // Scene-wide settings, such as the shadow map resolution. Saved in the scene file.
    public SceneSettings Settings { get; } = new();

    // Rigid body physics for this scene. Stepped by SceneUpdate while the scene plays.
    public Physics.PhysicsWorld Physics { get; } = new();
    public IEnumerable<SceneObject> All => _all;
    public IEnumerable<SceneObject> Roots => _all.Where(o => o.Parent is null);

    // Every object with each parent directly followed by its children, then their children, and so on.
    public IEnumerable<SceneObject> InHierarchyOrder => Roots.SelectMany(Subtree);

    public void Add(SceneObject obj, SceneObject? parent = null)
    {
        obj.Parent = parent;
        parent?.Children.Add(obj);
        _all.AddRange(Subtree(obj));
    }

    // Detaches obj and its descendants. Their internal links stay intact so Add() can restore them (undo).
    public void Remove(SceneObject obj)
    {
        obj.Parent?.Children.Remove(obj);
        obj.Parent = null;
        foreach (var o in Subtree(obj).ToList()) _all.Remove(o);
    }

    // Takes out every object. Scene loading uses it when a new scene replaces this one.
    public void Clear() => _all.Clear();

    // Removes each object the test picks, with its descendants. Returns how many top-level objects were removed.
    public int RemoveWhere(Func<SceneObject, bool> pick)
    {
        int removed = 0;
        foreach (var obj in _all.Where(pick).ToList())
        {
            if (!_all.Contains(obj)) continue;   // already gone with an ancestor
            Remove(obj);
            removed++;
        }
        return removed;
    }

    public bool Contains(SceneObject obj) => _all.Contains(obj);

    public SceneObject? Find(Guid id) => _all.FirstOrDefault(o => o.Id == id);

    // The first object with exactly this name, top to bottom as the Hierarchy lists them (a parent, then its children,
    // then the next object), or null.
    public SceneObject? FindByName(string name) => InHierarchyOrder.FirstOrDefault(o => o.Name == name);

    // Every object with exactly this name, in hierarchy order.
    public List<SceneObject> FindAllByName(string name) => InHierarchyOrder.Where(o => o.Name == name).ToList();

    // For scripts: the first object with exactly this name in the scene that is playing (top to bottom as the
    // Hierarchy lists them), or null when there is none.
    //     var player = Scene.FindObjectWithName("Player");
    // Names are matched exactly, capital letters included. Searching every object takes a moment in a big scene, so
    // find an object once (in Start) and keep it rather than searching every frame.
    public static SceneObject? FindObjectWithName(string name) => Scripting.SceneManager.World?.FindByName(name);

    // For scripts: every object with exactly this name in the scene that is playing.
    public static List<SceneObject> FindObjectsWithName(string name) =>
        Scripting.SceneManager.World?.FindAllByName(name) ?? new List<SceneObject>();

    // True when parent may become obj's parent: not obj itself and not one of obj's descendants.
    public bool CanParent(SceneObject obj, SceneObject parent) => !Subtree(obj).Contains(parent);

    // Moves obj under parent, or to the top level when parent is null. obj keeps its world position,
    // rotation and scale: its local values are rewritten relative to the new parent. Its children move with it.
    public bool SetParent(SceneObject obj, SceneObject? parent)
    {
        if (parent is not null && !CanParent(obj, parent)) return false;
        if (obj.Parent == parent) return true;

        var world = obj.WorldMatrix;
        Remove(obj);

        var parentWorld = parent?.WorldMatrix ?? Matrix4x4.Identity;
        if (Matrix4x4.Invert(parentWorld, out var parentInverse))
        {
            Matrix4x4.Decompose(world * parentInverse, out var scale, out var rotation, out var position);
            obj.Scale = scale;
            obj.Rotation = rotation;
            obj.Position = position;
        }
        Add(obj, parent);
        return true;
    }

    public static IEnumerable<SceneObject> Subtree(SceneObject root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var d in Subtree(child))
                yield return d;
    }
}
