using Clocktail.Mcp.CurveEditor;

namespace Clocktail.Tests.Mcp;

public class EditorHistoryTests
{
    private static EditorDraft Opened() => new([1234, 7500, 15000, 0], 2, 0, false);

    [Fact]
    public void Undo_and_redo_restore_offsets_and_the_complete_band()
    {
        var history = new EditorHistory();
        var before = Opened();
        var after = new EditorDraft([1234, 15000, 22500, 0], 3, 1, true);
        history.Begin(before);
        Assert.True(history.Commit(after));
        var undo = history.Undo(after)!;
        Assert.True(before.SameAs(undo));
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
        Assert.True(after.SameAs(history.Redo(undo)!));
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void A_click_or_cancelled_drag_preserves_redo_and_the_original_imported_values()
    {
        var history = new EditorHistory();
        var original = Opened(); // The original may contain values outside the editor's grid.
        var changed = original with { Offsets = [1234, 15000, 22500, 0] };
        history.Begin(original); history.Commit(changed); history.Undo(changed);
        history.Begin(original);
        Assert.False(history.Commit(original.Copy()));
        Assert.True(history.CanRedo);
        history.Begin(original);
        Assert.True(original.SameAs(history.Cancel()!));
        Assert.True(history.CanRedo);
        Assert.True(changed.SameAs(history.Redo(original)!));
    }

    [Fact]
    public void A_new_edit_after_undo_replaces_the_redo_branch()
    {
        var history = new EditorHistory();
        var original = Opened();
        var first = original with { Top = 3 };
        history.Begin(original); history.Commit(first); history.Undo(first);
        var second = original with { Foot = 1, ManualFoot = true };
        history.Begin(original);
        Assert.True(history.Commit(second));
        Assert.False(history.CanRedo);
        Assert.Null(history.Redo(second));
        Assert.True(original.SameAs(history.Undo(second)!));
    }

    [Fact]
    public void History_owns_its_snapshots_even_when_the_caller_reuses_arrays()
    {
        var history = new EditorHistory();
        var before = Opened();
        history.Begin(before);
        before.Offsets[1] = 99000;
        var after = before with { Offsets = [1234, 15000, 22500, 0] };
        history.Commit(after);
        var undo = history.Undo(after)!;
        after.Offsets[1] = 99000;
        Assert.Equal(7500, undo.Offsets[1]);
        Assert.Equal(15000, history.Redo(undo)!.Offsets[1]);
    }
}
