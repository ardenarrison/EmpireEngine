namespace Editor;

// Undo and redo for scene edits, kept as whole-scene JSON snapshots so every kind of edit is covered.
// The editor records once an edit has settled (mouse up, no field active), so one drag or one typed name is one step.
public sealed class UndoHistory
{
    public const int MaxSteps = 512;

    private readonly LinkedList<string> _undo = new();   // oldest first; the newest step is Last
    private readonly Stack<string> _redo = new();
    private string _current = "";

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // Forgets every step and starts from this scene.
    public void Reset(string json)
    {
        _undo.Clear();
        _redo.Clear();
        _current = json;
    }

    // Adds a step when the scene differs from the last recorded state. A new edit clears the redo steps.
    public void Record(string json)
    {
        if (json == _current) return;
        Push(_current);
        _redo.Clear();
        _current = json;
    }

    // Returns the scene to load, or null when there is nothing to undo.
    public string? Undo(string json)
    {
        Record(json);
        if (_undo.Last is not { } last) return null;
        _undo.RemoveLast();
        _redo.Push(_current);
        _current = last.Value;
        return _current;
    }

    // Returns the scene to load, or null when there is nothing to redo.
    public string? Redo(string json)
    {
        Record(json);
        if (!_redo.TryPop(out var next)) return null;
        Push(_current);
        _current = next;
        return _current;
    }

    private void Push(string json)
    {
        _undo.AddLast(json);
        if (_undo.Count > MaxSteps)
            _undo.RemoveFirst();
    }
}
