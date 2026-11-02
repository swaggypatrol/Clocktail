#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Clocktail.Domain.Profiles;

namespace Clocktail.Mcp.CurveEditor;

/// <summary>
/// The V/F curve editor (the user, 2026-11-01): the curve and nothing else, dragged by hand, dark with light lines, no
/// icon, drawn by itself at the monitor's own DPI (per-monitor v2: crisp at 200 %). On the curve: every crash at the point
/// it happened on, the highest setting each point was marked stable or survived at, and where the card actually ran. The
/// low-voltage cliff is reshaped as a transition band between two draggable lines (<see cref="CurveModel"/>); above the
/// band the person drags points. The person's click on the save button is their own approval: the window hands the
/// offsets to <c>save</c> (a trial, then keep_as_daily), then shows the verified result.
/// </summary>
public sealed partial class CurveEditorForm : Form
{
    private static Color C(int rgb, int a = 255) => Color.FromArgb(a, (rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);
    // Warm, Claude-style dark palette: charcoal and cream neutrals with a clay accent; crash, stable and unverified marks
    // keep hues of their own so the evidence stays readable against the accent.
    private static readonly Color Bg = C(0x1c1b19), PlotBg = C(0x22211e), GridMinor = C(0x2c2a27), GridMajor = C(0x3a3733),
        AxisText = C(0xaba69c), TextC = C(0xf0eee6), Dim = C(0x8f8a80), CurveC = C(0xfaf9f5), ChangedC = C(0xe08a6a),
        Accent = C(0xd97757), Ghost = C(0xb0a596), StockC = C(0x635e56), CrashC = C(0xf2557a), RunC = C(0x7fb3a3),
        Good = C(0xa3c47e), PanelC = C(0x2a2825), PanelBorder = C(0x48443d), Primary = C(0xc96442), Err = C(0xf07a6a),
        Unverified = C(0xe9c46a);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private EditorData _d;
    private readonly Func<SaveRequest, Action<string>, SaveResult> _save;
    private readonly Func<DataPoint, HistoryData> _delete;
    private ContextMenuStrip? _historyMenu;
    private readonly int[] _opened;
    private int[] _off;
    /// <summary>The band: from <see cref="_bottom"/> (the cliff's foot) to <see cref="_top"/> (its top, where the person's
    /// part starts); <see cref="_bottomManual"/> once the person dragged the foot, so moving the top keeps it.</summary>
    private int _top, _bottom;
    private bool _bottomManual;
    private readonly EditorHistory _history = new();
    private readonly EditorDraft _original;
    private readonly int _maxIndex;   // the last point the card can reach (NVVDD MAX): the right end of smoothing
    private readonly double _runMax;

    private float _s = 1;
    private Font _body = null!, _small = null!, _title = null!, _bold = null!;
    private double _vx0, _vx1, _vy0, _vy1;
    private RectangleF _plot;
    private Bitmap? _layer;
    private (double, double, double, double, int, int, float) _layerKey;

    private int? _hover, _sel, _hoverCrash, _hoverRun, _hoverEvidence;
    /// <summary>1: the foot line, 2: the top line, 0: neither.</summary>
    private int _hoverLine, _dragLine;
    private int? _drag;
    private int[]? _dragStart;
    private bool _dragShift;
    private Point? _pan;
    private Point? _rightDown;
    private bool _rightDragged;
    private (double X0, double X1, double Y0, double Y1) _panView;
    private RectangleF _footTab, _topTab;

    private bool _busy, _saved, _done;
    private string? _saveError;
    private string _status;
    private Color _statusColor;

    /// <summary>How the window ended, for the tool's next answer.</summary>
    public string Outcome { get; private set; } = "closed without saving";

    public CurveEditorForm(EditorData data, Func<SaveRequest, Action<string>, SaveResult> save, Func<DataPoint, HistoryData> delete)
    {
        _d = data;
        _save = save;
        _delete = delete;
        _opened = [.. data.Offsets];
        Detect();
        _off = (int[])_opened.Clone();
        _original = Draft();
        _status = "Draft only. Select a point or drag to edit; the GPU is unchanged.";
        _statusColor = Dim;
        _maxIndex = data.MaxMv is { } max ? Math.Max(1, Array.FindLastIndex(data.Mv, v => v <= max + 0.01)) : data.Mv.Length - 1;
        _runMax = data.Runs.Count == 0 ? 1 : data.Runs.Max(r => r.Seconds);

        Text = "Clocktail · V/F Curve";
        ShowIcon = false;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        KeyPreview = true;
        Fonts();
        ResetView();
        BuildChrome();
        _sel = data.Runs.Count > 0 ? Array.FindIndex(data.Mv, v => v >= data.Runs.MaxBy(r => r.Seconds)!.Mv) : _top;
        if (_sel < 0) _sel = _top;
        RefreshChrome();
    }

    // ---- DPI ----

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int WM_DPICHANGED = 0x02E0;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); } catch (Exception x) when (x is DllNotFoundException or EntryPointNotFoundException) { }
        uint dpi = 96;
        try { dpi = GetDpiForWindow(Handle); } catch (EntryPointNotFoundException) { }
        _s = Math.Max(1, dpi) / 96f;
        Fonts();
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        int w = Math.Min((int)(1320 * _s), (int)(area.Width * 0.94)), h = Math.Min((int)(900 * _s), (int)(area.Height * 0.92));
        MinimumSize = new Size(Math.Min((int)(900 * _s), area.Width), Math.Min((int)(660 * _s), area.Height));
        Bounds = new Rectangle(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DPICHANGED)
        {
            _s = Math.Max(96, (int)((long)m.WParam & 0xffff)) / 96f;
            var r = Marshal.PtrToStructure<RECT>(m.LParam);
            Fonts();
            var area = Screen.FromHandle(Handle).WorkingArea;
            MinimumSize = new Size(Math.Min((int)(900 * _s), area.Width), Math.Min((int)(660 * _s), area.Height));
            Bounds = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            Invalidate();
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private void Fonts()
    {
        // WinForms keeps an equal Font instance instead of replacing it. Rebuilding the same fonts on handle creation
        // would therefore leave native child controls holding disposed instances.
        if (_body != null && Math.Abs(_body.Size - 13 * _s) < 0.01f) { LayoutChrome(); return; }
        var old = (_body, _small, _title, _bold);
        const string face = "Segoe UI";
        _body = new Font(face, 13 * _s, GraphicsUnit.Pixel);
        _bold = new Font(face, 13 * _s, FontStyle.Bold, GraphicsUnit.Pixel);
        _small = new Font(face, 11.5f * _s, GraphicsUnit.Pixel);
        _title = new Font(face, 23 * _s, FontStyle.Bold, GraphicsUnit.Pixel);
        ApplyChromeFonts();
        LayoutChrome();
        old._body?.Dispose(); old._small?.Dispose(); old._title?.Dispose(); old._bold?.Dispose();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        BringToFront2();
    }

    /// <summary>On top of the agent's terminal: the server is a background process, which Windows does not let take the
    /// foreground, so the window goes top-most for a moment.</summary>
    public void BringToFront2()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        TopMost = true;
        Activate();
        var t = new System.Windows.Forms.Timer { Interval = 600 };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); TopMost = false; };
        t.Start();
    }

    // ---- the curve ----

    private (int[]? Off, long[] Eff) _effA = (null, []), _effB = (null, []);

    /// <summary>What point <paramref name="i"/> runs at under <paramref name="off"/>, MHz (the driver's running maximum).
    /// Offset arrays are replaced, never changed in place, so the two most recent are cached by reference.</summary>
    private double Mhz(int i, int[] off)
    {
        if (!ReferenceEquals(_effA.Off, off))
        {
            if (ReferenceEquals(_effB.Off, off)) (_effA, _effB) = (_effB, _effA);
            else (_effB, _effA) = (_effA, (off, CurveModel.Effective(_d.BaseKhz, off)));
        }
        return _effA.Eff[i] / 1000.0;
    }

    private double Landed(int i, int[] off) => CurveModel.Clock(_d.BaseKhz, i, off[i]) / 1000.0;

    private bool InBand(int i) => i > _bottom && i < _top;
    private bool Draggable(int i) => i >= _top && i < _d.Writable;

    private int[] Rules(int[] next, int anchor) =>
        CurveModel.Apply(_d.Mv, _opened, next, _d.BaseKhz, _top, _bottom, anchor, _d.Writable, _d.LowKhz, _d.HighKhz);

    /// <summary>The most any point runs above where it ran when the window opened, MHz, and the voltage it is at.</summary>
    private (double Lift, double Mv) MaxLift()
    {
        double lift = double.NegativeInfinity, at = 0;
        for (int i = 0; i < _off.Length; i++)
        {
            double d = Mhz(i, _off) - Mhz(i, _opened);
            if (d > lift) { lift = d; at = _d.Mv[i]; }
        }
        return (lift, at);
    }

    /// <summary>The points whose setting now is at or above a setting that crashed in a curve trial.</summary>
    private int AboveCrash() => _d.Crashes.Where(c => c.CurveTrial && c.Index < _d.Writable && Mhz(c.Index, _off) >= c.SetMhz - 1e-6).Select(c => c.Index).Distinct().Count();

    private void ResetView()
    {
        double lo = (_d.VminMv ?? _d.Mv.Min()) - 10, hi = Math.Min(_d.Mv.Max(), (_d.MaxMv ?? _d.Mv.Max()) + 10);
        var shown = Enumerable.Range(0, _d.Mv.Length).Where(i => _d.Mv[i] >= lo && _d.Mv[i] <= hi).ToList();
        if (shown.Count < 2) { lo = _d.Mv.Min(); hi = _d.Mv.Max(); shown = [.. Enumerable.Range(0, _d.Mv.Length)]; }
        var ys = shown.SelectMany(i => new[] { Mhz(i, _off), Mhz(i, _opened) })
            .Concat(_d.Crashes.Where(c => c.Mv >= lo && c.Mv <= hi).Select(c => c.SetMhz)).ToList();
        _vx0 = lo; _vx1 = hi;
        _vy0 = Math.Floor((ys.Min() - 80) / 50) * 50;
        _vy1 = Math.Ceiling((ys.Max() + 120) / 50) * 50;
    }

    private float X(double mv) => (float)(_plot.Left + (mv - _vx0) / (_vx1 - _vx0) * _plot.Width);
    private float Y(double mhz) => (float)(_plot.Bottom - (mhz - _vy0) / (_vy1 - _vy0) * _plot.Height);
    private double InvX(float x) => _vx0 + (x - _plot.Left) / _plot.Width * (_vx1 - _vx0);
    private double InvY(float y) => _vy0 + (_plot.Bottom - y) / _plot.Height * (_vy1 - _vy0);

    private EditorDraft Draft() => new(_off, _top, _bottom, _bottomManual);
    private void Restore(EditorDraft draft)
    {
        (_off, _top, _bottom, _bottomManual) = ((int[])draft.Offsets.Clone(), draft.Top, draft.Foot, draft.ManualFoot);
        RefreshChrome();
        Invalidate();
    }
    private bool Dirty => !_original.SameAs(Draft());
    private void Push() => _history.Begin(Draft());
    private bool FinishEdit() { bool changed = _history.Commit(Draft()); RefreshChrome(); Invalidate(); return changed; }

    /// <summary>The band for the opened curve: the one it was saved with while the card still holds that curve, else detected.</summary>
    private void Detect()
    {
        _top = CurveModel.CliffTop(_d.Mv, _opened, _d.Writable);
        _bottom = CurveModel.Foot(_opened, _top, _d.Writable);
        _bottomManual = false;
        if (_d.Band is { } band && band.Foot >= 0 && band.Foot < band.Top && band.Top < Math.Min(_d.Writable, _opened.Length) - 1)
            (_top, _bottom, _bottomManual) = (band.Top, band.Foot, true);
    }

    private void Smooth()
    {
        if (_busy || _done) return;
        Push();
        _off = Rules(CurveModel.Smooth(_d.Mv, _off, _d.BaseKhz, _top, _maxIndex, _d.Writable), _top);
        bool changed = FinishEdit();
        Say(changed ? "Draft smoothed, including its transition band. GPU unchanged." : "Already smooth; no draft points changed.", Dim);
    }

    private void Undo()
    {
        if (_history.Undo(Draft()) is { } before) { Restore(before); Say("Edit undone. GPU unchanged.", Dim); }
    }
    private void Redo()
    {
        if (_history.Redo(Draft()) is { } after) { Restore(after); Say("Edit redone. GPU unchanged.", Dim); }
    }
    private void Reset()
    {
        Push();
        Restore(_original);
        FinishEdit();
        Say("Restored the exact curve read when this window opened. GPU unchanged.", Dim);
    }
    private void Say(string text, Color color) { _status = text; _statusColor = color; RefreshChrome(); Invalidate(); }

    private void Save()
    {
        if (_busy) return;
        if (_done) { Close(); return; }
        _saveButton.Focus(); // Commit any numeric edit before copying the draft.
        if (CurveModel.Violation(_d.Mv, _opened, _off, _d.Writable, _d.LowKhz, _d.HighKhz) is { } bad) { _saveError = bad; Say("Cannot save: " + bad, Err); return; }
        _saveError = null;
        _busy = true;
        Cursor = Cursors.WaitCursor;
        Say("Applying to GPU…", Accent);
        var request = new SaveRequest((int[])_off.Clone(), _bottom, _top);
        Task.Run(() =>
        {
            SaveResult result;
            try { result = _save(request, msg => Ui(() => Say(msg, Accent))); }
            catch (Exception e) when (e is not OutOfMemoryException) { result = new SaveResult(false, null, e.Message); }
            Ui(() =>
            {
                _busy = false;
                Cursor = Cursors.Default;
                if (result.Saved && result.Warning == null)
                {
                    _saved = _done = true;
                    Outcome = "saved: the curve was written and kept as the daily profile";
                    Say("Saved as Daily. Curve applied and readback verified.", Good);
                }
                else if (result.Saved)
                {
                    // Saved, but the card did not finish switching to it (a trial's wall still on): the person reads why, then closes.
                    _saved = _done = true;
                    _saveError = result.Warning;
                    Outcome = $"saved with a warning: {result.Warning}";
                    Say("Saved as Daily, but: " + result.Warning, Accent);
                }
                else
                {
                    _saveError = result.Error;
                    Say("Save failed: " + result.Error, Err);
                }
            });
        });
    }

    private void Ui(Action a)
    {
        try { if (!IsDisposed) BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy) { e.Cancel = true; Say("Operation in progress; please wait…", Accent); return; }
        if (!_saved && Dirty && !_discarding && e.CloseReason == CloseReason.UserClosing)
        {
            var save = new TaskDialogButton("Save as Daily");
            var discard = new TaskDialogButton("Discard draft");
            var page = new TaskDialogPage
            {
                Caption = "Clocktail", Heading = "You have an unsaved curve draft",
                Text = "Save as Daily applies the draft to your GPU and keeps it for future starts. Discard leaves the GPU unchanged.",
                Buttons = { save, discard, TaskDialogButton.Cancel }, DefaultButton = TaskDialogButton.Cancel,
            };
            var choice = TaskDialog.ShowDialog(this, page);
            if (choice == save) { e.Cancel = true; Save(); return; }
            if (choice != discard) { e.Cancel = true; return; }
            _discarding = true;
        }
        if (!_saved) Outcome = _saveError == null ? "cancelled: nothing was written" : $"closed after a failed save: {_saveError}";
        base.OnFormClosing(e);
    }

    // ---- input ----

    private void HistoryMenu(Point location)
    {
        if (!_plot.Contains(location)) return;
        var indices = new HashSet<int>();
        if (PointAt(location, draggableOnly: false) is { } i) indices.Add(i);
        foreach (var ev in _showEvidence.Checked ? _d.Evidence : [])
            if (Math.Abs(X(ev.Mv) - location.X) <= 8 * _s
                && ((ev.StableMhz is { } st && Math.Abs(Y(st) - location.Y) <= 8 * _s)
                    || (ev.StableRanMhz is { } ran && Math.Abs(Y(ran) - location.Y) <= 8 * _s)
                    || (ev.SurvivedMhz is { } sv && Math.Abs(Y(sv) - location.Y) <= 8 * _s)))
                indices.Add(ev.Index);
        var ids = (_showCrashes.Checked ? _d.Crashes : []).Where(c => Math.Abs(X(c.Mv) - location.X) <= 8 * _s
            && Math.Abs(Y(c.SetMhz) - location.Y) <= 8 * _s).Select(c => c.RecordId);
        var records = HistoryData.Select(_d.Tests, indices, ids);
        _historyMenu?.Dispose();
        _historyMenu = new ContextMenuStrip { ShowImageMargin = false, Font = _body };
        if (records.Count == 0)
            _historyMenu.Items.Add("No historical tests at this point").Enabled = false;
        else
        {
            _historyMenu.Items.Add($"Delete historical test ({records.Count})").Enabled = false;
            _historyMenu.Items.Add(new ToolStripSeparator());
            foreach (var record in records)
            {
                string game = record.Game.Length > 48 ? record.Game[..48] + "…" : record.Game;
                var item = new ToolStripMenuItem($"{record.At.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {record.Outcome} · {record.Minutes:0.#} min · {game}".Replace("&", "&&"))
                {
                    ToolTipText = record.Change + (record.Note == null ? "" : "\n" + record.Note),
                };
                item.Click += (_, _) => DeleteHistory(record);
                _historyMenu.Items.Add(item);
            }
        }
        _historyMenu.Show(this, location);
    }

    private void DeleteHistory(DataPoint point)
    {
        if (_busy) return;
        if (MessageBox.Show(this,
            $"Delete this historical test?\n\n{point.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n{point.Game}\n{point.Outcome} · {point.Minutes:0.#} min\n{point.Change}\n\n" +
            "Removes this test's evidence at every curve point, immediately (even if you cancel curve edits).\n" +
            "GPU settings and raw residency samples are unchanged. The original journal is retained for recovery.",
            "Delete historical test", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        _busy = true;
        Cursor = Cursors.WaitCursor;
        Say("Deleting historical test…", Accent);
        Task.Run(() =>
        {
            try
            {
                var history = _delete(point);
                Ui(() =>
                {
                    _d = _d with { Crashes = history.Crashes, Evidence = history.Evidence, Tests = history.Tests };
                    _hoverCrash = _hover = _hoverRun = _hoverEvidence = null;
                    _busy = false;
                    Cursor = Cursors.Default;
                    Say("Historical test deleted; evidence refreshed. GPU settings unchanged.", Good);
                });
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Ui(() =>
                {
                    _busy = false;
                    Cursor = Cursors.Default;
                    Say("Could not delete or refresh history: " + e.Message + "; reopen the window to check.", Err);
                });
            }
        });
    }

    private int? PointAt(PointF p, bool draggableOnly)
    {
        int? best = null;
        double bestD = 10 * _s;
        for (int i = 0; i < _off.Length; i++)
        {
            if (draggableOnly && !Draggable(i)) continue;
            if (_d.Mv[i] < _vx0 || _d.Mv[i] > _vx1) continue;
            double dx = X(_d.Mv[i]) - p.X, dy = Y(Mhz(i, _off)) - p.Y, dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < bestD) { bestD = dist; best = i; }
        }
        return best;
    }

    private int? CrashAt(PointF p)
    {
        for (int k = _d.Crashes.Count - 1; k >= 0; k--)
        {
            var c = _d.Crashes[k];
            if (Math.Abs(X(c.Mv) - p.X) < 7 * _s && Math.Abs(Y(c.SetMhz) - p.Y) < 7 * _s) return k;
        }
        return null;
    }

    private int? RunAt(PointF p)
    {
        int? best = null;
        double bestD = 6 * _s;
        for (int k = 0; k < _d.Runs.Count; k++)
        {
            double dx = X(_d.Runs[k].Mv) - p.X, dy = Y(_d.Runs[k].Mhz) - p.Y, dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < bestD) { bestD = dist; best = k; }
        }
        return best;
    }

    private int? EvidenceAt(PointF p)
    {
        if (!_plot.Contains(p)) return null;
        for (int k = _d.Evidence.Count - 1; k >= 0; k--)
        {
            var ev = _d.Evidence[k];
            if (Math.Abs(X(ev.Mv) - p.X) > 8 * _s) continue;
            if (new[] { ev.StableMhz, ev.StableRanMhz, ev.SurvivedMhz }.Any(mhz => mhz is { } v && Math.Abs(Y(v) - p.Y) < 8 * _s)) return k;
        }
        return null;
    }

    /// <summary>Which band line's tab is under the point (1: the foot's, 2: the top's, 0: neither).</summary>
    private int TabAt(PointF p) => _topTab.Contains(p) ? 2 : _footTab.Contains(p) ? 1 : 0;

    /// <summary>Which band line's body is under the point (the top when both coincide). The points are tested before this,
    /// so the point on the top line stays draggable.</summary>
    private int LineBodyAt(PointF p)
    {
        if (!_plot.Contains(p)) return 0;
        if (Math.Abs(X(_d.Mv[_top]) - p.X) < 6 * _s) return 2;
        if (_bottom < _top && Math.Abs(X(_d.Mv[_bottom]) - p.X) < 6 * _s) return 1;
        return 0;
    }

    /// <summary>The point whose voltage is nearest the mouse (for the band lines).</summary>
    private int IndexAtX(float x)
    {
        double mv = InvX(x);
        int best = 0;
        for (int i = 0; i < _d.Mv.Length; i++) if (Math.Abs(_d.Mv[i] - mv) < Math.Abs(_d.Mv[best] - mv)) best = i;
        return best;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_busy || _done) return;
        if (e.Button == MouseButtons.Right)
        {
            _rightDown = _plot.Contains(e.Location) ? e.Location : null;
            _rightDragged = false;
        }
        if (e.Button == MouseButtons.Left)
        {
            if (_plot.Contains(e.Location)) { ActiveControl = null; Focus(); }
            int line = TabAt(e.Location);
            int? point = line == 0 ? PointAt(e.Location, draggableOnly: false) : null;
            if (line == 0 && point == null) line = LineBodyAt(e.Location);
            if (line != 0)
            {
                Push();
                _dragLine = line;
                _dragStart = [.. _off];
                Capture = true;
                return;
            }
            if (point is { } i)
            {
                _sel = i;
                if (!Draggable(i)) { RefreshChrome(); Invalidate(); return; }
                Push();
                _drag = _sel = i;
                _dragStart = [.. _off];
                _dragShift = (ModifierKeys & Keys.Shift) != 0;
                Capture = true;
                RefreshChrome();
                Invalidate();
                return;
            }
        }
        if (_plot.Contains(e.Location))
        {
            _pan = e.Location;
            _panView = (_vx0, _vx1, _vy0, _vy1);
            Capture = true;
            Cursor = Cursors.SizeAll;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag is { } k && _dragStart != null)
        {
            int want = CurveModel.OffsetFor(_d.BaseKhz, k, InvY(e.Y) * 1000);
            var next = (int[])_dragStart.Clone();
            if (_dragShift)
            {
                int delta = want - _dragStart[k];
                for (int j = k; j < Math.Min(_d.Writable, next.Length); j++) next[j] = _dragStart[j] + delta;
            }
            else next[k] = want;
            _off = Rules(next, k);
            RefreshChrome();
            Invalidate();
            return;
        }
        if (_dragLine != 0 && _dragStart != null)
        {
            int n = Math.Min(_d.Writable, _d.Mv.Length), at = IndexAtX(e.X);
            if (_dragLine == 2)
            {
                int top = Math.Clamp(at, 1, n - 2);
                if (top != _top)
                {
                    _top = top;
                    _bottom = _bottomManual ? Math.Min(_bottom, _top) : CurveModel.Foot(_opened, _top, _d.Writable);
                    _off = Rules(_dragStart, _top);
                    Say($"Band top moved to {Fmt(_d.Mv[_top])} mV: edit freely from here; transition band below", Dim);
                }
            }
            else
            {
                int bottom = Math.Clamp(at, 0, _top);
                if (bottom != _bottom)
                {
                    _bottom = bottom;
                    _bottomManual = true;
                    _off = Rules(_dragStart, _top);
                    Say(bottom == _top ? "Band collapsed: the opened cliff is preserved" : $"Band foot moved to {Fmt(_d.Mv[_bottom])} mV: wider is gentler, but lifts low-voltage points further", Dim);
                }
            }
            Invalidate();
            return;
        }
        if (_pan is { } p0)
        {
            if (_rightDown != null && !_rightDragged)
            {
                if (Math.Abs(e.X - p0.X) <= 4 * _s && Math.Abs(e.Y - p0.Y) <= 4 * _s) return;
                _rightDragged = true;
            }
            double dx = (e.X - p0.X) / _plot.Width * (_panView.X1 - _panView.X0), dy = (e.Y - p0.Y) / _plot.Height * (_panView.Y1 - _panView.Y0);
            (_vx0, _vx1, _vy0, _vy1) = (_panView.X0 - dx, _panView.X1 - dx, _panView.Y0 + dy, _panView.Y1 + dy);
            Invalidate();
            return;
        }
        var before = (_hover, _hoverCrash, _hoverRun, _hoverEvidence, _hoverLine);
        _hoverLine = TabAt(e.Location);
        _hover = _hoverLine == 0 ? PointAt(e.Location, draggableOnly: false) : null;
        if (_hoverLine == 0 && _hover == null) _hoverLine = LineBodyAt(e.Location);
        _hoverCrash = _showCrashes.Checked && _hover == null && _hoverLine == 0 ? CrashAt(e.Location) : null;
        _hoverEvidence = _showEvidence.Checked && _hover == null && _hoverCrash == null && _hoverLine == 0 ? EvidenceAt(e.Location) : null;
        _hoverRun = _showHistory.Checked && _hover == null && _hoverCrash == null && _hoverEvidence == null && _hoverLine == 0 && _plot.Contains(e.Location) ? RunAt(e.Location) : null;
        Cursor = _busy ? Cursors.WaitCursor : !_done && _hoverLine != 0 ? Cursors.SizeWE : !_done && _hover is { } h && Draggable(h) ? Cursors.SizeNS : Cursors.Default;
        if (before != (_hover, _hoverCrash, _hoverRun, _hoverEvidence, _hoverLine)) Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        bool menu = !_busy && e.Button == MouseButtons.Right && _rightDown is { } down && !_rightDragged
            && Math.Abs(e.X - down.X) <= 4 * _s && Math.Abs(e.Y - down.Y) <= 4 * _s;
        if (_drag != null || _dragLine != 0) FinishEdit();
        _drag = null; _dragStart = null; _dragLine = 0; _pan = null;
        Capture = false;
        _rightDown = null;
        Cursor = Cursors.Default;
        Invalidate();
        if (menu) HistoryMenu(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = _hoverCrash = _hoverRun = _hoverEvidence = null;
        _hoverLine = 0;
        if (_drag == null && _dragLine == 0 && _pan == null && !_busy) Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_plot.Contains(e.Location)) return;
        double f = Math.Pow(1.15, -e.Delta / 120.0), cx = InvX(e.X), cy = InvY(e.Y);
        bool onlyY = (ModifierKeys & Keys.Control) != 0, onlyX = (ModifierKeys & Keys.Shift) != 0;
        double nx0 = onlyY ? _vx0 : cx - (cx - _vx0) * f, nx1 = onlyY ? _vx1 : cx + (_vx1 - cx) * f;
        double ny0 = onlyX ? _vy0 : cy - (cy - _vy0) * f, ny1 = onlyX ? _vy1 : cy + (_vy1 - cy) * f;
        // Neither a span the grid would draw a million lines for, nor one whose coordinates overflow GDI+.
        if (nx1 - nx0 is < 2 or > 2000 || ny1 - ny0 is < 20 or > 20000) return;
        (_vx0, _vx1, _vy0, _vy1) = (nx0, nx1, ny0, ny1);
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && _plot.Contains(e.Location) && PointAt(e.Location, draggableOnly: false) == null && TabAt(e.Location) == 0 && LineBodyAt(e.Location) == 0) { ResetView(); Invalidate(); }
    }

    private void CancelGesture()
    {
        if (_drag != null || _dragLine != 0)
        {
            if (_history.Cancel() is { } before) Restore(before);
            _drag = null; _dragStart = null; _dragLine = 0;
            Say("Drag cancelled. Draft restored.", Dim);
        }
        _pan = null; _rightDown = null; Capture = false;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && (_drag != null || _dragLine != 0)) CancelGesture();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        bool shift = (keyData & Keys.Shift) != 0, ctrl = (keyData & Keys.Control) != 0;
        if (key == Keys.Escape && (_drag != null || _dragLine != 0 || _pan != null)) { CancelGesture(); return true; }
        if (_busy || _drag != null || _dragLine != 0) return base.ProcessCmdKey(ref msg, keyData);
        if (ctrl && key == Keys.S) { Save(); return true; }
        if (_done) return base.ProcessCmdKey(ref msg, keyData);
        if (ctrl && key == Keys.Z) { if (shift) Redo(); else Undo(); return true; }
        if (ctrl && key == Keys.Y) { Redo(); return true; }
        if (ctrl && key == Keys.D0) { ResetView(); Invalidate(); return true; }
        if (ActiveControl != null) return base.ProcessCmdKey(ref msg, keyData);
        if (_sel is { } i && Draggable(i) && key is Keys.Up or Keys.Down)
        {
            Push();
            var next = (int[])_off.Clone();
            next[i] += (key == Keys.Up ? 1 : -1) * (shift ? 2 * CurveModel.GridKhz : CurveModel.GridKhz);
            _off = Rules(next, i);
            FinishEdit();
            return true;
        }
        if (_sel is { } j && key is Keys.Left or Keys.Right)
        {
            _sel = Math.Clamp(j + (key == Keys.Right ? 1 : -1), _top, Math.Min(_d.Writable, _off.Length) - 1);
            RefreshChrome(); Invalidate(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---- drawing ----

    private static string Fmt(double v) => v.ToString("0.#", Inv);
    private static string Signed(double v) => (v > 0 ? "+" : "") + v.ToString("0.#", Inv);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float s = _s, w = ClientSize.Width, h = ClientSize.Height;
        _plot = new RectangleF(72 * s, 190 * s, Math.Max(100, w - 100 * s), Math.Max(100, h - 424 * s));

        // The background, grid, axes and the running points: 4500 dots, drawn again only when the view changes.
        var key = (_vx0, _vx1, _vy0, _vy1, ClientSize.Width, ClientSize.Height, _s);
        if (_layer == null || _layerKey != key)
        {
            _layer?.Dispose();
            _layer = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
            using var lg = Graphics.FromImage(_layer);
            lg.SmoothingMode = SmoothingMode.AntiAlias;
            lg.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            DrawStatic(lg);
            _layerKey = key;
        }
        g.DrawImageUnscaled(_layer, 0, 0);

        Header(g, w);
        Inspector(g, w, h);
        Footer(g, w, h);

        g.SetClip(_plot);

        // the band
        float xb = X(_d.Mv[_bottom]), xt = X(_d.Mv[_top]);
        bool bandHot = _hoverLine != 0 || _dragLine != 0;
        if (_bottom < _top)
            using (var zb = new SolidBrush(Color.FromArgb(bandHot ? 30 : 16, Accent))) g.FillRectangle(zb, xb, _plot.Top, Math.Max(0, xt - xb), _plot.Height);

        int n = _off.Length;
        PointF[] Line(Func<int, double> mhz) => [.. Enumerable.Range(0, n).Select(i => new PointF(X(_d.Mv[i]), Y(mhz(i))))];
        if (_showReference.Checked) using (var sp = new Pen(StockC, 1.4f * s) { DashStyle = DashStyle.Dot }) g.DrawLines(sp, Line(i => _d.BaseKhz[i] / 1000.0));
        if (_showOpened.Checked && !_opened.SequenceEqual(_off))
            using (var gp = new Pen(Color.FromArgb(170, Ghost), 1.5f * s) { DashStyle = DashStyle.Dash }) g.DrawLines(gp, Line(i => Mhz(i, _opened)));

        // A stable label does not prove the requested clock was reached. Keep the sampled clock separate.
        foreach (var ev in _showEvidence.Checked ? _d.Evidence : [])
        {
            if (ev.Mv < _vx0 - 5 || ev.Mv > _vx1 + 5) continue;
            float x = X(ev.Mv), half = 5 * s;
            if (ev.SurvivedMhz is { } sv && (ev.StableMhz is not { } st0 || sv > st0))
                using (var p = new Pen(Color.FromArgb(150, AxisText), 1.5f * s) { DashStyle = DashStyle.Dot }) g.DrawLine(p, x - half, Y(sv), x + half, Y(sv));
            if (ev.StableMhz is { } st)
            {
                if (ev.StableRanMhz is { } ran)
                {
                    using var measured = new Pen(Good, 2.2f * s);
                    g.DrawLine(measured, x - half, Y(ran), x + half, Y(ran));
                    if (ev.HeldBelowMhz != null)
                    {
                        using var link = new Pen(Color.FromArgb(125, Unverified), s) { DashStyle = DashStyle.Dot };
                        g.DrawLine(link, x, Y(ran), x, Y(st));
                    }
                }
                if (ev.StableRanMhz == null || ev.HeldBelowMhz != null)
                {
                    using var setting = new Pen(Unverified, 1.7f * s);
                    g.DrawRectangle(setting, x - half, Y(st) - 3 * s, half * 2, 6 * s);
                }
            }
        }

        using (var cp = new Pen(CurveC, 2.2f * s) { LineJoin = LineJoin.Round }) g.DrawLines(cp, Line(i => Mhz(i, _off)));
        using (var changed = new Pen(ChangedC, 2.4f * s))
            for (int i = 1; i < n; i++)
                if (_off[i] != _opened[i] || _off[i - 1] != _opened[i - 1])
                    g.DrawLine(changed, X(_d.Mv[i - 1]), Y(Mhz(i - 1, _off)), X(_d.Mv[i]), Y(Mhz(i, _off)));

        // the points
        for (int i = 0; i < n; i++)
        {
            if (_d.Mv[i] < _vx0 - 5 || _d.Mv[i] > _vx1 + 5) continue;
            float px = X(_d.Mv[i]), py = Y(Mhz(i, _off));
            bool changed = _off[i] != _opened[i], locked = i >= _d.Writable;
            float r = 2.7f * s;
            if (InBand(i))
            {
                using var ring = new Pen(changed ? ChangedC : CurveC, 1.3f * s);
                g.DrawEllipse(ring, px - r, py - r, 2 * r, 2 * r);
            }
            else
            {
                using var b = new SolidBrush(locked ? Dim : changed ? ChangedC : CurveC);
                g.FillEllipse(b, px - r, py - r, 2 * r, 2 * r);
            }
            if (i == _hover || i == _drag || i == _sel)
            {
                float rr = (i == _drag ? 7 : 6) * s;
                using var ring = new Pen(i == _drag || i == _hover ? Accent : TextC, 1.8f * s);
                g.DrawEllipse(ring, px - rr, py - rr, 2 * rr, 2 * rr);
                if (i == _drag) using (var ab = new SolidBrush(Accent)) g.FillEllipse(ab, px - 3.5f * s, py - 3.5f * s, 7 * s, 7 * s);
            }
        }

        // crashes: a solid cross for a curve trial's, a hollow one for a crash under another kind of trial
        for (int k = 0; _showCrashes.Checked && k < _d.Crashes.Count; k++)
        {
            var c = _d.Crashes[k];
            float cx = X(c.Mv), cy = Y(c.SetMhz), a = (k == _hoverCrash ? 7 : 5) * s;
            if (c.CurveTrial)
                using (var glow = new SolidBrush(Color.FromArgb(k == _hoverCrash ? 90 : 45, CrashC))) g.FillEllipse(glow, cx - 2 * a, cy - 2 * a, 4 * a, 4 * a);
            using var xp = new Pen(c.CurveTrial ? CrashC : Color.FromArgb(170, CrashC), (c.CurveTrial ? 2.2f : 1.3f) * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(xp, cx - a, cy - a, cx + a, cy + a);
            g.DrawLine(xp, cx - a, cy + a, cx + a, cy - a);
        }

        // the band's lines
        void BandLine(float x, bool hot)
        {
            using var zp = new Pen(Color.FromArgb(hot ? 255 : 170, Accent), (hot ? 2 : 1.4f) * s) { DashStyle = DashStyle.Dash };
            g.DrawLine(zp, x, _plot.Top, x, _plot.Bottom);
        }
        if (_bottom < _top) BandLine(xb, _hoverLine == 1 || _dragLine == 1);
        BandLine(xt, _hoverLine == 2 || _dragLine == 2);

        g.ResetClip();
        using (var bp = new Pen(GridMajor, 1 * s)) g.DrawRectangle(bp, _plot.X, _plot.Y, _plot.Width, _plot.Height);

        // the lines' tabs, outside the clip so they sit on the plot's top edge: the top's to the right, the foot's to the left
        _topTab = Tab(g, xt, $"Band top {Fmt(_d.Mv[_top])} mV ▸", right: true, hot: _hoverLine == 2 || _dragLine == 2);
        _footTab = Tab(g, xb, $"◂ Band foot {Fmt(_d.Mv[_bottom])} mV", right: false, hot: _hoverLine == 1 || _dragLine == 1);

        Tooltip(g);
        Legend(g, _plot.Bottom + 34 * s);
    }

    /// <summary>A draggable label at the top of a band line.</summary>
    private RectangleF Tab(Graphics g, float x, string label, bool right, bool hot)
    {
        if (x < _plot.Left || x > _plot.Right) return RectangleF.Empty;
        float s = _s;
        var sz = g.MeasureString(label, _small);
        float bw = sz.Width + 12 * s, bh = sz.Height + 6 * s;
        float bx = right ? x + 2 * s : x - bw - 2 * s;
        bx = Math.Clamp(bx, _plot.Left, Math.Max(_plot.Left, _plot.Right - bw));
        var r = new RectangleF(bx, _plot.Top + 4 * s, bw, bh);
        using var path = Round(r, 5 * s);
        using (var fb = new SolidBrush(Color.FromArgb(hot ? 255 : 215, PanelC))) g.FillPath(fb, path);
        using (var pb = new Pen(Color.FromArgb(hot ? 255 : 150, Accent), 1 * s)) g.DrawPath(pb, path);
        using (var tb = new SolidBrush(hot ? TextC : Accent)) g.DrawString(label, _small, tb, r.X + 6 * s, r.Y + 3 * s);
        return r;
    }

    /// <summary>The layer under everything that moves: the background, the plot, the grid with its labels, and where the
    /// card ran (brighter: more time there).</summary>
    private void DrawStatic(Graphics g)
    {
        g.Clear(Bg);
        using (var pb = new SolidBrush(PlotBg)) g.FillRectangle(pb, _plot);
        Grid(g);
        g.SetClip(_plot);
        foreach (var r in (_showHistory.Checked ? _d.Runs : []).OrderBy(r => r.Seconds))
        {
            if (r.Mv < _vx0 - 5 || r.Mv > _vx1 + 5 || r.Mhz < _vy0 - 50 || r.Mhz > _vy1 + 50) continue;
            double t = Math.Log(1 + r.Seconds) / Math.Log(1 + _runMax);
            float rad = (float)(1.2 + 1.8 * t) * _s;
            using var b = new SolidBrush(Color.FromArgb((int)(18 + 100 * t), RunC));
            g.FillEllipse(b, X(r.Mv) - rad, Y(r.Mhz) - rad, rad * 2, rad * 2);
        }
        g.ResetClip();
    }

    private static double Nice(double range, double pixels, double minPx, double[] steps)
    {
        foreach (var st in steps) if (range / st * minPx <= pixels) return st;
        return steps[^1];
    }

    private void Grid(Graphics g)
    {
        float s = _s;
        double xs = Nice(_vx1 - _vx0, _plot.Width, 56 * s, [5, 10, 25, 50, 100, 200]);
        double ys = Nice(_vy1 - _vy0, _plot.Height, 34 * s, [15, 25, 50, 100, 200, 250, 500, 1000]);
        using var minor = new Pen(GridMinor, 1 * s);
        using var major = new Pen(GridMajor, 1 * s);
        using var tb = new SolidBrush(AxisText);
        g.SetClip(_plot);
        for (double v = Math.Ceiling(_vx0 / xs) * xs; v <= _vx1; v += xs)
        {
            float x = X(v);
            g.DrawLine(Math.Abs(v % (xs * 2)) < 1e-6 ? major : minor, x, _plot.Top, x, _plot.Bottom);
        }
        for (double v = Math.Ceiling(_vy0 / ys) * ys; v <= _vy1; v += ys)
        {
            float y = Y(v);
            g.DrawLine(Math.Abs(v % (ys * 2)) < 1e-6 ? major : minor, _plot.Left, y, _plot.Right, y);
        }
        g.ResetClip();
        for (double v = Math.Ceiling(_vx0 / xs) * xs; v <= _vx1; v += xs)
        {
            string t = Fmt(v);
            var sz = g.MeasureString(t, _small);
            g.DrawString(t, _small, tb, X(v) - sz.Width / 2, _plot.Bottom + 5 * s);
        }
        for (double v = Math.Ceiling(_vy0 / ys) * ys; v <= _vy1; v += ys)
        {
            string t = Fmt(v);
            var sz = g.MeasureString(t, _small);
            g.DrawString(t, _small, tb, _plot.Left - sz.Width - 6 * s, Y(v) - sz.Height / 2);
        }
        g.DrawString("mV", _small, tb, _plot.Right - 22 * s, _plot.Bottom + 5 * s + 14 * s);
        g.DrawString("MHz", _small, tb, _plot.Left - 40 * s, _plot.Top - 20 * s);
    }

    private void Tooltip(Graphics g)
    {
        var lines = new List<(string Text, Color Color)>();
        PointF at;
        if ((_drag ?? _hover) is { } i)
        {
            double now = Mhz(i, _off);
            lines.Add(($"#{i}  {Fmt(_d.Mv[i])} mV" + (InBand(i) ? "  · Band (auto)" : i < _top ? "  · Below band (unchanged)" : ""), TextC));
            lines.Add(($"Draft {Fmt(now)} MHz · offset {Signed(_off[i] / 1000.0)} MHz", CurveC));
            if (Landed(i, _off) < now - 0.01) lines.Add(($"Point lands at {Fmt(Landed(i, _off))} MHz; the driver lifts it from the left", Dim));
            if (_d.Evidence.FirstOrDefault(e => e.Index == i) is { } ev)
            {
                if (ev.StableMhz is { } st) lines.Add(($"Marked stable at {Fmt(st)} MHz · {Duration((int)ev.StableSeconds)}", ev.StableRanMhz == null || ev.HeldBelowMhz != null ? Unverified : Good));
                if (ev.StableRanMhz is { } ranSt)
                    lines.Add(($"Sampled {Fmt(ranSt)} MHz (P90)" + (ev.HeldBelowMhz is { } held ? $" · {Fmt(held)} below setting" : ""), Good));
                else if (ev.StableMhz != null) lines.Add(("No sampled clock for this stable record", Unverified));
                if (ev.SurvivedMhz is { } sv && (ev.StableMhz is not { } st2 || sv > st2)) lines.Add(($"Highest survived: {Fmt(sv)} MHz", Dim));
            }
            var crashes = _d.Crashes.Where(c => c.Index == i).ToList();
            if (crashes.Count > 0)
            {
                int curve = crashes.Count(c => c.CurveTrial);
                lines.Add(($"Crashes: {crashes.Count}{(curve < crashes.Count ? $" ({curve} in curve trials)" : "")}; lowest setting {Fmt(crashes.Min(c => c.SetMhz))} MHz", CrashC));
            }
            var runs = _d.Runs.Where(r => Math.Abs(r.Mv - _d.Mv[i]) < 2.5).ToList();
            if (runs.Count > 0)
            {
                var most = runs.MaxBy(r => r.Seconds)!;
                lines.Add(($"Ran for {Duration(runs.Sum(r => r.Seconds))}; most often {Fmt(most.Mhz)} MHz", RunC));
            }
            if (i >= _d.Writable) lines.Add(("Driver cannot write this point", Dim));
            else if (i < _top) lines.Add(("To edit here, drag the band top to a lower voltage", Dim));
            at = new PointF(X(_d.Mv[i]), Y(now));
        }
        else if (_hoverCrash is { } k)
        {
            var c = _d.Crashes[k];
            lines.Add(($"Crash  {c.At.ToLocalTime():yyyy-MM-dd HH:mm}" + (c.CurveTrial ? "" : "  · Non-curve trial"), CrashC));
            lines.Add(($"{c.Game}", TextC));
            lines.Add(($"#{c.Index}  {Fmt(c.Mv)} mV  Curve setting {Fmt(c.SetMhz)} MHz", TextC));
            if (c.RanMhz is { } ran) lines.Add(($"Ran at {Fmt(ran)} MHz before crash", Dim));
            lines.Add((c.Change.Length > 64 ? c.Change[..64] + "…" : c.Change, Dim));
            at = new PointF(X(c.Mv), Y(c.SetMhz));
        }
        else if (_hoverRun is { } r0)
        {
            var r = _d.Runs[r0];
            lines.Add(($"Historical load  {Fmt(r.Mv)} mV · {Fmt(r.Mhz)} MHz", RunC));
            lines.Add(($"{Duration(r.Seconds)} · samples at ≥50% load · not live", Dim));
            at = new PointF(X(r.Mv), Y(r.Mhz));
        }
        else if (_hoverEvidence is { } e0)
        {
            var ev = _d.Evidence[e0];
            lines.Add(($"Test evidence · #{ev.Index} · {Fmt(ev.Mv)} mV", TextC));
            if (ev.StableMhz is { } stable)
            {
                lines.Add(($"Marked stable: setting {Fmt(stable)} MHz · {Duration((int)ev.StableSeconds)}", AxisText));
                if (ev.StableRanMhz is { } ran)
                    lines.Add(($"Sampled {Fmt(ran)} MHz (P90)" + (ev.HeldBelowMhz is { } held ? $" · {Fmt(held)} below setting" : ""), Good));
                else lines.Add(("No sampled clock for this stable record", Unverified));
                if (ev.HeldBelowMhz != null) lines.Add(("The test did not demonstrate stability at the requested setting", Unverified));
            }
            if (ev.SurvivedMhz is { } survived) lines.Add(($"Highest survived setting: {Fmt(survived)} MHz", AxisText));
            lines.Add(("Historical evidence · no guarantee of draft stability", Dim));
            at = new PointF(X(ev.Mv), Y(ev.StableMhz ?? ev.SurvivedMhz ?? _vy0));
        }
        else if (_hoverLine == 2)
        {
            lines.Add(("Band top: edit freely from here", Accent));
            lines.Add(("Move left: extend the band; move right: edit more points by hand", TextC));
            at = new PointF(X(_d.Mv[_top]), _plot.Top + 50 * _s);
        }
        else if (_hoverLine == 1)
        {
            lines.Add(("Band foot: transition starts here; unchanged below", Accent));
            lines.Add(("Move left: gentler, more low-voltage lift; move to top: no band", TextC));
            at = new PointF(X(_d.Mv[_bottom]), _plot.Top + 50 * _s);
        }
        else return;

        float s = _s, pad = 10 * s, tw = Math.Min(430 * s, _plot.Width - 28 * s);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.LineLimit };
        var rows = lines.Select((line, k) => (line.Text, line.Color, Font: k == 0 ? _bold : _body,
            Height: Math.Min(36 * s, g.MeasureString(line.Text, k == 0 ? _bold : _body, (int)tw, format).Height + 2 * s))).ToList();
        float budget = Math.Max(18 * s, _plot.Height - 2 * pad - 8 * s), used = 0;
        rows = rows.TakeWhile(row => { used += row.Height; return used <= budget; }).ToList();
        float bw = tw + 2 * pad, bh = rows.Sum(row => row.Height) + 2 * pad;
        float x = at.X + 16 * s, y = at.Y - bh - 12 * s;
        if (x + bw > _plot.Right) x = at.X - bw - 16 * s;
        if (y < _plot.Top) y = at.Y + 16 * s;
        if (y + bh > _plot.Bottom) y = _plot.Bottom - bh - 4 * s;
        x = Math.Max(_plot.Left + 4 * s, x);
        using (var path = Round(new RectangleF(x, y, bw, bh), 7 * s))
        {
            using var pb = new SolidBrush(Color.FromArgb(245, PanelC));
            using var bp = new Pen(PanelBorder, 1 * s);
            g.FillPath(pb, path);
            g.DrawPath(bp, path);
        }
        float rowY = y + pad;
        foreach (var row in rows)
        {
            using var brush = new SolidBrush(row.Color);
            g.DrawString(row.Text, row.Font, brush, new RectangleF(x + pad, rowY, tw, row.Height), format);
            rowY += row.Height;
        }
    }

    private static string Duration(int seconds) => seconds >= 3600 ? $"{seconds / 3600.0:0.#} h" : seconds >= 60 ? $"{seconds / 60.0:0.#} min" : $"{seconds} s";

    private void Legend(Graphics g, float y)
    {
        float s = _s, x = _plot.Left;
        void Item(Color color, string label, bool dashed = false, char marker = '\0')
        {
            float width = 23 * s + g.MeasureString(label, _small).Width + 18 * s;
            if (x + width > _plot.Right) { x = _plot.Left; y += 18 * s; }
            using var pen = new Pen(color, 2 * s) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid };
            if (marker == 'b') g.DrawRectangle(pen, x + 3 * s, y + 5 * s, 10 * s, 6 * s);
            else if (marker == 'x') { g.DrawLine(pen, x + 4 * s, y + 4 * s, x + 12 * s, y + 12 * s); g.DrawLine(pen, x + 4 * s, y + 12 * s, x + 12 * s, y + 4 * s); }
            else if (marker == 'o') { using var dot = new SolidBrush(color); g.FillEllipse(dot, x + 5 * s, y + 5 * s, 6 * s, 6 * s); }
            else g.DrawLine(pen, x, y + 8 * s, x + 16 * s, y + 8 * s);
            using var brush = new SolidBrush(AxisText);
            g.DrawString(label, _small, brush, x + 23 * s, y);
            x += width;
        }
        Item(CurveC, "Draft");
        if (!_off.SequenceEqual(_opened)) Item(ChangedC, "Edited");
        if (_showOpened.Checked) Item(Ghost, "Opened", true);
        if (_showReference.Checked) Item(StockC, "Base", true);
        if (_showEvidence.Checked) { Item(Good, "Stable sample"); Item(Unverified, "Unverified setting", marker: 'b'); Item(AxisText, "Survived", true); }
        if (_showCrashes.Checked) Item(CrashC, "Crash", marker: 'x');
        if (_showHistory.Checked) Item(RunC, "Historical load", marker: 'o');
    }

    private static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _controlTips.Dispose(); _historyMenu?.Dispose(); }
        base.Dispose(disposing);
        if (disposing) { _body?.Dispose(); _small?.Dispose(); _title?.Dispose(); _bold?.Dispose(); _layer?.Dispose(); }
    }

    // ---- the window's own thread ----

    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    private static readonly IntPtr PerMonitorV2 = new(-4);

    /// <summary>Shows the window on its own STA thread and returns at once; <paramref name="closed"/> gets its outcome.</summary>
    public static void Show(EditorData data, Func<SaveRequest, Action<string>, SaveResult> save, Func<DataPoint, HistoryData> delete, Action<string> closed)
    {
        var thread = new Thread(() =>
        {
            string outcome = "closed without saving";
            try
            {
                try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch (InvalidOperationException) { }
                try { SetThreadDpiAwarenessContext(PerMonitorV2); } catch (EntryPointNotFoundException) { }
                Application.EnableVisualStyles();
                // The rendering default can only be set before this process's first window; later editors keep it.
                try { Application.SetCompatibleTextRenderingDefault(false); } catch (InvalidOperationException) { }
                // An exception in a handler ends the window with its reason (the catch below), not with WinForms' Continue/Quit dialog.
                try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, true); } catch (InvalidOperationException) { }
                using var form = new CurveEditorForm(data, save, delete);
                EditorState.Opened(() => form.BeginInvoke(form.BringToFront2));
                Application.Run(form);
                outcome = form.Outcome;
            }
            catch (Exception e) when (e is not OutOfMemoryException) { outcome = $"the window failed: {e.Message}"; }
            finally { closed(outcome); }
        }) { IsBackground = true, Name = "curve editor" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
#endif
