namespace Clocktail.Mcp.CurveEditor;

/// <summary>A draft is local data; history includes the band so undo restores the complete edit.</summary>
public sealed record EditorDraft(int[] Offsets, int Top, int Foot, bool ManualFoot)
{
    public EditorDraft Copy() => this with { Offsets = (int[])Offsets.Clone() };
    public bool SameAs(EditorDraft other) => Top == other.Top && Foot == other.Foot && ManualFoot == other.ManualFoot
        && Offsets.SequenceEqual(other.Offsets);
}

/// <summary>Transactions for clicks and drags. A click or a cancelled drag never consumes redo history.</summary>
public sealed class EditorHistory
{
    private readonly Stack<EditorDraft> _undo = new(), _redo = new();
    private EditorDraft? _pending;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Begin(EditorDraft current) => _pending = current.Copy();
    public bool Commit(EditorDraft current)
    {
        var before = _pending;
        _pending = null;
        if (before == null || before.SameAs(current)) return false;
        _undo.Push(before);
        _redo.Clear();
        if (_undo.Count > 200)
        {
            var keep = _undo.Take(150).Reverse().ToArray();
            _undo.Clear();
            foreach (var item in keep) _undo.Push(item);
        }
        return true;
    }
    public EditorDraft? Cancel() { var before = _pending; _pending = null; return before?.Copy(); }
    public EditorDraft? Undo(EditorDraft current)
    {
        if (!_undo.TryPop(out var before)) return null;
        _redo.Push(current.Copy());
        return before.Copy();
    }
    public EditorDraft? Redo(EditorDraft current)
    {
        if (!_redo.TryPop(out var after)) return null;
        _undo.Push(current.Copy());
        return after.Copy();
    }
}
