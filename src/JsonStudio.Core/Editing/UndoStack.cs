using JsonStudio.Core.Model;

namespace JsonStudio.Core.Editing;

/// <summary>
/// Full-snapshot undo stack (deep clones with the same node ids). One snapshot is pushed before each structural
/// change, and one for each typing burst in a field.
/// </summary>
public sealed class UndoStack
{
    private readonly List<JsonNode> _undo = new();
    private readonly List<JsonNode> _redo = new();
    private string? _burst;

    public UndoStack(int max = 200) => Max = max;

    public int Max { get; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    /// <summary>The key of the current typing burst, or null.</summary>
    public string? CurrentBurst => _burst;

    public void Push(JsonNode currentRoot)
    {
        _burst = null;
        PushInternal(currentRoot);
    }

    /// <summary>Pushes a snapshot only if <paramref name="burstKey"/> starts a new burst.</summary>
    public bool PushForBurst(string burstKey, JsonNode currentRoot)
    {
        if (_burst == burstKey) return false;
        _burst = burstKey;
        PushInternal(currentRoot);
        return true;
    }

    public void EndBurst() => _burst = null;

    private void PushInternal(JsonNode root)
    {
        _undo.Add(root.Clone(keepIds: true));
        if (_undo.Count > Max) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public JsonNode? Undo(JsonNode currentRoot)
    {
        if (_undo.Count == 0) return null;
        _burst = null;
        _redo.Add(currentRoot.Clone(keepIds: true));
        var s = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return s.Clone(keepIds: true);
    }

    public JsonNode? Redo(JsonNode currentRoot)
    {
        if (_redo.Count == 0) return null;
        _burst = null;
        _undo.Add(currentRoot.Clone(keepIds: true));
        var s = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        return s.Clone(keepIds: true);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _burst = null;
    }
}
