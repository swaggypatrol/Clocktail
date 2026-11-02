#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Clocktail.Mcp.CurveEditor;

/// <summary>
/// The V/F curve editor (the user, 2026-11-01): the curve and nothing else, dragged by hand, dark with light lines, no
/// icon, drawn by itself at the monitor's own DPI (per-monitor v2: crisp at 200 %). On the curve: every crash at the point
/// it happened on, the highest setting each point was marked stable or survived at, and where the card actually ran. The
/// low-voltage cliff is reshaped as a transition band between two draggable lines (<see cref="CurveModel"/>); above the
/// band the person drags points. The person's click on the save button is their own approval: the window hands the
/// offsets to <c>save</c> (a trial, then keep_as_daily) and closes when it lands.
/// </summary>
public sealed class CurveEditorForm : Form
{
    private static Color C(int rgb, int a = 255) => Color.FromArgb(a, (rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);
    private static readonly Color Bg = C(0x111317), PlotBg = C(0x16191e), GridMinor = C(0x1f2329), GridMajor = C(0x2a2f37),
        AxisText = C(0x8a919c), TextC = C(0xd9dde3), Dim = C(0x6f7681), CurveC = C(0xeef1f5), ChangedC = C(0xffd08a),
        Accent = C(0xffb44a), Ghost = C(0x8796ab), StockC = C(0x58606d), CrashC = C(0xff4d6d), RunC = C(0x2ec4b6),
        Good = C(0x6bd98f), PanelC = C(0x22262d), PanelBorder = C(0x3a404a), Primary = C(0x3d8bff), Err = C(0xff6b6b),
        ButtonC = C(0x262a31), ButtonHover = C(0x30353e);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly EditorData _d;
    private readonly Func<SaveRequest, Action<string>, SaveResult> _save;
    private readonly int[] _opened;
    private int[] _off;
    /// <summary>The band: from <see cref="_bottom"/> (the cliff's foot) to <see cref="_top"/> (its top, where the person's
    /// part starts); <see cref="_bottomManual"/> once the person dragged the foot, so moving the top keeps it.</summary>
    private int _top, _bottom;
    private bool _bottomManual;
    private readonly Stack<(int[] Off, int Top, int Bottom, bool Manual)> _undo = new();
    private readonly int _maxIndex;   // the last point the card can reach (NVVDD MAX): the right end of smoothing
    private readonly double _runMax, _peakOpened;

    private float _s = 1;
    private Font _body = null!, _small = null!, _title = null!, _bold = null!;
    private double _vx0, _vx1, _vy0, _vy1;
    private RectangleF _plot;
    private Bitmap? _layer;
    private (double, double, double, double, int, int, float) _layerKey;

    private int? _hover, _sel, _hoverCrash, _hoverRun, _hoverButton;
    /// <summary>1: the foot line, 2: the top line, 0: neither.</summary>
    private int _hoverLine, _dragLine;
    private int? _drag;
    private int[]? _dragStart;
    private bool _dragShift;
    private Point? _pan;
    private (double X0, double X1, double Y0, double Y1) _panView;
    private RectangleF _footTab, _topTab;

    private bool _busy, _saved, _done;
    private string? _saveError;
    private string _status;
    private Color _statusColor;
    private readonly List<(RectangleF R, string Label, Action Do, bool Primary, Func<bool> Enabled)> _buttons = [];

    /// <summary>How the window ended, for the tool's next answer.</summary>
    public string Outcome { get; private set; } = "closed without saving";

    public CurveEditorForm(EditorData data, Func<SaveRequest, Action<string>, SaveResult> save)
    {
        _d = data;
        _save = save;
        _opened = [.. data.Offsets];
        Detect();
        _off = Rules(_opened, _top);
        _peakOpened = CurveModel.PeakSlope(data.Mv, data.BaseKhz, _opened, _bottom, _top);
        int changed = Enumerable.Range(0, _off.Length).Count(i => _off[i] != _opened[i]);
        if (changed > 0)
        {
            var (lift, at) = MaxLift();
            _status = $"过渡带 {Fmt(_d.Mv[_bottom])}–{Fmt(_d.Mv[_top])} mV 已平滑：{changed} 个点改动，比打开时最多 {Signed(lift)} MHz（{Fmt(at)} mV）；尚未写入显卡";
            _statusColor = Accent;
        }
        else
        {
            _status = "曲线无需修正；拖动点开始编辑";
            _statusColor = Dim;
        }
        _maxIndex = data.MaxMv is { } max ? Math.Max(1, Array.FindLastIndex(data.Mv, v => v <= max + 0.01)) : data.Mv.Length - 1;
        _runMax = data.Runs.Count == 0 ? 1 : data.Runs.Max(r => r.Seconds);

        Text = "Clocktail · V/F 曲线";
        ShowIcon = false;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        KeyPreview = true;
        Fonts();
        ResetView();
        _buttons.Add((default, "平滑", Smooth, false, () => !_busy));
        _buttons.Add((default, "撤销", Undo, false, () => !_busy && _undo.Count > 0));
        _buttons.Add((default, "重置", Reset, false, () => !_busy));
        _buttons.Add((default, "取消", Close, false, () => !_busy));
        _buttons.Add((default, "确认并保存为 Daily", Save, true, () => !_busy));
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
        int w = Math.Min((int)(1320 * _s), (int)(area.Width * 0.94)), h = Math.Min((int)(840 * _s), (int)(area.Height * 0.92));
        MinimumSize = new Size((int)(960 * _s), (int)(620 * _s));
        Bounds = new Rectangle(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DPICHANGED)
        {
            _s = Math.Max(96, (int)((long)m.WParam & 0xffff)) / 96f;
            var r = Marshal.PtrToStructure<RECT>(m.LParam);
            Fonts();
            MinimumSize = new Size((int)(960 * _s), (int)(620 * _s));
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
        _body?.Dispose(); _small?.Dispose(); _title?.Dispose(); _bold?.Dispose();
        const string face = "Microsoft YaHei UI";
        _body = new Font(face, 13 * _s, GraphicsUnit.Pixel);
        _bold = new Font(face, 13 * _s, FontStyle.Bold, GraphicsUnit.Pixel);
        _small = new Font(face, 11.5f * _s, GraphicsUnit.Pixel);
        _title = new Font(face, 17 * _s, FontStyle.Bold, GraphicsUnit.Pixel);
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

    private void Push()
    {
        _undo.Push(([.. _off], _top, _bottom, _bottomManual));
        if (_undo.Count > 200) { var keep = _undo.Take(150).Reverse().ToList(); _undo.Clear(); foreach (var u in keep) _undo.Push(u); }
    }

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
        var before = _off;
        Push();
        _off = Rules(CurveModel.Smooth(_d.Mv, _off, _d.BaseKhz, _top, _maxIndex, _d.Writable), _top);
        if (_off.SequenceEqual(before)) { _undo.Pop(); Say("已经很平滑了：没有可改的点", Dim); return; }
        Say("已平滑过渡顶以上的部分（频率只升不降，平顶保持）；可再点一次更平滑", Dim);
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        (_off, _top, _bottom, _bottomManual) = _undo.Pop();
        Say("已撤销", Dim);
    }

    private void Reset()
    {
        var (off, top, bottom, manual) = (_off, _top, _bottom, _bottomManual);
        Push();
        Detect();
        _off = Rules(_opened, _top);
        if (_off.SequenceEqual(off) && (_top, _bottom, _bottomManual) == (top, bottom, manual)) { _undo.Pop(); Say("已经是打开时的曲线", Dim); return; }
        Say("已回到打开时的曲线（过渡带重新平滑）", Dim);
    }

    private void Say(string text, Color color) { _status = text; _statusColor = color; Invalidate(); }

    private void Save()
    {
        if (_done) { Close(); return; }
        if (CurveModel.Violation(_d.Mv, _opened, _off, _d.Writable, _d.LowKhz, _d.HighKhz) is { } bad) { Say("不能保存：" + bad, Err); return; }
        _busy = true;
        Cursor = Cursors.WaitCursor;
        Say("正在写入显卡…", Accent);
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
                    _saved = true;
                    Outcome = "saved: the curve was written and kept as the daily profile";
                    Close();
                }
                else if (result.Saved)
                {
                    // Saved, but the card did not finish switching to it (a trial's wall still on): the person reads why, then closes.
                    _saved = _done = true;
                    Outcome = $"saved with a warning: {result.Warning}";
                    Say("已保存为 Daily，但：" + result.Warning, Accent);
                }
                else
                {
                    _saveError = result.Error;
                    Say("保存失败：" + result.Error, Err);
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
        if (_busy) { e.Cancel = true; Say("正在保存，请稍候…", Accent); return; }
        if (!_saved) Outcome = _saveError == null ? "cancelled: nothing was written" : $"closed after a failed save: {_saveError}";
        base.OnFormClosing(e);
    }

    // ---- input ----

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

    private int? ButtonAt(PointF p)
    {
        for (int k = 0; k < _buttons.Count; k++) if (_buttons[k].R.Contains(p)) return k;
        return null;
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
        if (_busy) return;
        if (e.Button == MouseButtons.Left)
        {
            if (ButtonAt(e.Location) is { } b) { if (_buttons[b].Enabled()) _buttons[b].Do(); Invalidate(); return; }
            int line = TabAt(e.Location);
            int? point = line == 0 ? PointAt(e.Location, draggableOnly: true) : null;
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
                Push();
                _drag = _sel = i;
                _dragStart = [.. _off];
                _dragShift = (ModifierKeys & Keys.Shift) != 0;
                Capture = true;
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
                    Say($"过渡顶移到 {Fmt(_d.Mv[_top])} mV：从这里起由你掌控，以下是过渡带", Dim);
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
                    Say(bottom == _top ? "过渡带已收起：打开时的断崖原样保留" : $"过渡脚移到 {Fmt(_d.Mv[_bottom])} mV：越宽越缓，低电压点抬得越多", Dim);
                }
            }
            Invalidate();
            return;
        }
        if (_pan is { } p0)
        {
            double dx = (e.X - p0.X) / _plot.Width * (_panView.X1 - _panView.X0), dy = (e.Y - p0.Y) / _plot.Height * (_panView.Y1 - _panView.Y0);
            (_vx0, _vx1, _vy0, _vy1) = (_panView.X0 - dx, _panView.X1 - dx, _panView.Y0 + dy, _panView.Y1 + dy);
            Invalidate();
            return;
        }
        var before = (_hover, _hoverCrash, _hoverRun, _hoverButton, _hoverLine);
        _hoverButton = ButtonAt(e.Location);
        _hoverLine = _hoverButton == null ? TabAt(e.Location) : 0;
        _hover = _hoverButton == null && _hoverLine == 0 ? PointAt(e.Location, draggableOnly: false) : null;
        if (_hoverButton == null && _hoverLine == 0 && _hover == null) _hoverLine = LineBodyAt(e.Location);
        _hoverCrash = _hover == null && _hoverButton == null && _hoverLine == 0 ? CrashAt(e.Location) : null;
        _hoverRun = _hover == null && _hoverCrash == null && _hoverLine == 0 && _plot.Contains(e.Location) ? RunAt(e.Location) : null;
        Cursor = _busy ? Cursors.WaitCursor : _hoverLine != 0 ? Cursors.SizeWE : _hover is { } h && Draggable(h) ? Cursors.SizeNS
            : _hoverButton is { } hb && _buttons[hb].Enabled() ? Cursors.Hand : Cursors.Default;
        if (before != (_hover, _hoverCrash, _hoverRun, _hoverButton, _hoverLine)) Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        // A click that moved nothing leaves no undo step.
        if ((_drag != null || _dragLine != 0) && _dragStart != null && _undo.Count > 0 && _undo.Peek() is var u
            && u.Off.SequenceEqual(_off) && u.Top == _top && u.Bottom == _bottom) _undo.Pop();
        _drag = null; _dragStart = null; _dragLine = 0; _pan = null;
        Capture = false;
        Cursor = Cursors.Default;
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
        if (_plot.Contains(e.Location) && PointAt(e.Location, draggableOnly: false) == null && TabAt(e.Location) == 0 && LineBodyAt(e.Location) == 0) { ResetView(); Invalidate(); }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_busy || _drag != null || _dragLine != 0) return base.ProcessCmdKey(ref msg, keyData);   // not under a drag: the next move would undo it
        Keys key = keyData & Keys.KeyCode;
        bool shift = (keyData & Keys.Shift) != 0, ctrl = (keyData & Keys.Control) != 0;
        if (ctrl && key == Keys.Z) { Undo(); return true; }
        if (_sel is { } i && Draggable(i) && key is Keys.Up or Keys.Down)
        {
            Push();
            var next = (int[])_off.Clone();
            next[i] += (key == Keys.Up ? 1 : -1) * (shift ? 2 * CurveModel.GridKhz : CurveModel.GridKhz);
            _off = Rules(next, i);
            Invalidate();
            return true;
        }
        if (_sel is { } j && key is Keys.Left or Keys.Right)
        {
            _sel = Math.Clamp(j + (key == Keys.Right ? 1 : -1), _top, Math.Min(_d.Writable, _off.Length) - 1);
            Invalidate();
            return true;
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
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float s = _s, w = ClientSize.Width, h = ClientSize.Height;
        float head = (_d.AlsoKept != null ? 100 : 80) * s;   // a third header line when a keep would take more than the curve
        _plot = new RectangleF(72 * s, head, Math.Max(100, w - 72 * s - 28 * s), Math.Max(100, h - head - 128 * s));

        // The background, grid, axes and the running points: 4500 dots, drawn again only when the view changes.
        var key = (_vx0, _vx1, _vy0, _vy1, ClientSize.Width, ClientSize.Height, _s);
        if (_layer == null || _layerKey != key)
        {
            _layer?.Dispose();
            _layer = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
            using var lg = Graphics.FromImage(_layer);
            lg.SmoothingMode = SmoothingMode.AntiAlias;
            lg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            DrawStatic(lg);
            _layerKey = key;
        }
        g.DrawImageUnscaled(_layer, 0, 0);

        Header(g, w);

        g.SetClip(_plot);

        // the band
        float xb = X(_d.Mv[_bottom]), xt = X(_d.Mv[_top]);
        bool bandHot = _hoverLine != 0 || _dragLine != 0;
        if (_bottom < _top)
            using (var zb = new SolidBrush(Color.FromArgb(bandHot ? 30 : 16, Accent))) g.FillRectangle(zb, xb, _plot.Top, Math.Max(0, xt - xb), _plot.Height);

        int n = _off.Length;
        PointF[] Line(Func<int, double> mhz) => [.. Enumerable.Range(0, n).Select(i => new PointF(X(_d.Mv[i]), Y(mhz(i))))];
        using (var sp = new Pen(StockC, 1.4f * s) { DashStyle = DashStyle.Dot }) g.DrawLines(sp, Line(i => _d.BaseKhz[i] / 1000.0));
        if (!_opened.SequenceEqual(_off))
            using (var gp = new Pen(Color.FromArgb(170, Ghost), 1.5f * s) { DashStyle = DashStyle.Dash }) g.DrawLines(gp, Line(i => Mhz(i, _opened)));

        // the evidence: the highest setting marked stable (and survived) at each point
        foreach (var ev in _d.Evidence)
        {
            if (ev.Mv < _vx0 - 5 || ev.Mv > _vx1 + 5) continue;
            float x = X(ev.Mv), half = 5 * s;
            if (ev.SurvivedMhz is { } sv && (ev.StableMhz is not { } st0 || sv > st0))
                using (var p = new Pen(Color.FromArgb(150, Good), 1.5f * s) { DashStyle = DashStyle.Dot }) g.DrawLine(p, x - half, Y(sv), x + half, Y(sv));
            if (ev.StableMhz is { } st)
                using (var p = new Pen(Color.FromArgb(230, Good), 2 * s)) g.DrawLine(p, x - half, Y(st), x + half, Y(st));
        }

        using (var cp = new Pen(CurveC, 2.2f * s) { LineJoin = LineJoin.Round }) g.DrawLines(cp, Line(i => Mhz(i, _off)));

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
        for (int k = 0; k < _d.Crashes.Count; k++)
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
        _topTab = Tab(g, xt, $"过渡顶 {Fmt(_d.Mv[_top])} mV ▸", right: true, hot: _hoverLine == 2 || _dragLine == 2);
        _footTab = Tab(g, xb, $"◂ 过渡脚 {Fmt(_d.Mv[_bottom])} mV", right: false, hot: _hoverLine == 1 || _dragLine == 1);

        Tooltip(g);
        Legend(g, _plot.Bottom + 34 * s);
        DrawButtons(g, w, h);
    }

    /// <summary>A draggable label at the top of a band line.</summary>
    private RectangleF Tab(Graphics g, float x, string label, bool right, bool hot)
    {
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

    private void Header(Graphics g, float w)
    {
        float s = _s;
        using (var tb = new SolidBrush(TextC)) g.DrawString("V/F 曲线", _title, tb, 20 * s, 14 * s);
        float tw = g.MeasureString("V/F 曲线", _title).Width;
        using (var db = new SolidBrush(Dim)) g.DrawString(_d.Gpu, _body, db, 20 * s + tw + 10 * s, 18 * s);
        using (var sb = new SolidBrush(_statusColor))
        {
            var sz = g.MeasureString(_status, _body);
            g.DrawString(_status, _body, sb, w - 28 * s - sz.Width, 18 * s);
        }
        // the summary line: the band and what it costs, the steepness, and the crash check
        var parts = new List<(string, Color)>();
        parts.Add(_bottom < _top
            ? ($"过渡带 {Fmt(_d.Mv[_bottom])}–{Fmt(_d.Mv[_top])} mV（{_top - _bottom} 段，自动平滑；以下保持打开时的值）", Accent)
            : ($"无过渡带：{Fmt(_d.Mv[_top])} mV 以下保持打开时的值", Dim));
        double peak = CurveModel.PeakSlope(_d.Mv, _d.BaseKhz, _off, Math.Max(0, _bottom), _top);
        parts.Add(($"最陡 {peak:0} MHz/mV（打开时 {_peakOpened:0}）", Dim));
        var (lift, at) = MaxLift();
        parts.Add((lift > 0.01 ? $"比打开时最高 +{lift:0} MHz（{Fmt(at)} mV）" : "没有点高于打开时", lift > 0.01 ? Accent : Dim));
        int above = AboveCrash();
        parts.Add((above > 0 ? $"⚠ {above} 个点不低于曾崩溃的设定" : "没有点达到曾崩溃的设定", above > 0 ? Err : Good));
        float x = 20 * s, y = 46 * s;
        using var sep = new SolidBrush(Dim);
        for (int k = 0; k < parts.Count; k++)
        {
            if (k > 0) { x += 10 * s; g.DrawString("·", _small, sep, x, y); x += 14 * s; }
            using var b = new SolidBrush(parts[k].Item2);
            g.DrawString(parts[k].Item1, _small, b, x, y);
            x += g.MeasureString(parts[k].Item1, _small).Width - 4 * s;
        }
        // What the save button also commits to: on its own line, never under anything else.
        if (_d.AlsoKept is { } kept)
            using (var ab = new SolidBrush(Accent)) g.DrawString("⚠ 保存为 Daily 时还会" + kept, _small, ab, 20 * s, 66 * s);
    }

    /// <summary>The layer under everything that moves: the background, the plot, the grid with its labels, and where the
    /// card ran (brighter: more time there).</summary>
    private void DrawStatic(Graphics g)
    {
        g.Clear(Bg);
        using (var pb = new SolidBrush(PlotBg)) g.FillRectangle(pb, _plot);
        Grid(g);
        g.SetClip(_plot);
        foreach (var r in _d.Runs.OrderBy(r => r.Seconds))
        {
            if (r.Mv < _vx0 - 5 || r.Mv > _vx1 + 5 || r.Mhz < _vy0 - 50 || r.Mhz > _vy1 + 50) continue;
            double t = Math.Log(1 + r.Seconds) / Math.Log(1 + _runMax);
            float rad = (float)(1.8 + 3.0 * t) * _s;
            using var b = new SolidBrush(Color.FromArgb((int)(35 + 190 * t), RunC));
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
            double now = Mhz(i, _off), was = Mhz(i, _opened);
            lines.Add(($"#{i}  {Fmt(_d.Mv[i])} mV" + (InBand(i) ? "  · 过渡带（自动）" : i < _top ? "  · 过渡带以下（保持打开时）" : ""), TextC));
            lines.Add(($"{Fmt(now)} MHz   offset {Signed(_off[i] / 1000.0)}", CurveC));
            if (Landed(i, _off) < now - 0.01) lines.Add(($"自身落点 {Fmt(Landed(i, _off))} MHz，驱动按左侧点抬到 {Fmt(now)}", Dim));
            if (_off[i] != _opened[i]) lines.Add(($"打开时 {Fmt(was)} MHz（{Signed(now - was)}）", Ghost));
            lines.Add(($"Stock {Fmt(_d.BaseKhz[i] / 1000.0)} MHz（{Signed(now - _d.BaseKhz[i] / 1000.0)}）", Dim));
            if (_d.Evidence.FirstOrDefault(e => e.Index == i) is { } ev)
            {
                if (ev.StableMhz is { } st) lines.Add(($"标为稳定时的最高设定 {Fmt(st)} MHz（{Duration((int)ev.StableSeconds)}）{(now > st + 0.01 ? $"，现在高出 {now - st:0}" : "")}", now > st + 0.01 ? Accent : Good));
                if (ev.SurvivedMhz is { } sv && (ev.StableMhz is not { } st2 || sv > st2)) lines.Add(($"存活过的最高设定 {Fmt(sv)} MHz", Dim));
            }
            var crashes = _d.Crashes.Where(c => c.Index == i).ToList();
            if (crashes.Count > 0)
            {
                int curve = crashes.Count(c => c.CurveTrial);
                lines.Add(($"崩溃 {crashes.Count} 次{(curve < crashes.Count ? $"（{curve} 次在曲线试验中）" : "")}，最低设定 {Fmt(crashes.Min(c => c.SetMhz))} MHz", CrashC));
            }
            var runs = _d.Runs.Where(r => Math.Abs(r.Mv - _d.Mv[i]) < 2.5).ToList();
            if (runs.Count > 0)
            {
                var most = runs.MaxBy(r => r.Seconds)!;
                lines.Add(($"实际运行 {Duration(runs.Sum(r => r.Seconds))}，最常 {Fmt(most.Mhz)} MHz", RunC));
            }
            if (i >= _d.Writable) lines.Add(("驱动无法写这个点", Dim));
            else if (i < _top) lines.Add(("要手动调这里：把「过渡顶」拖到更低电压", Dim));
            at = new PointF(X(_d.Mv[i]), Y(now));
        }
        else if (_hoverCrash is { } k)
        {
            var c = _d.Crashes[k];
            lines.Add(($"崩溃  {c.At.ToLocalTime():yyyy-MM-dd HH:mm}" + (c.CurveTrial ? "" : "  · 非曲线试验"), CrashC));
            lines.Add(($"{c.Game}", TextC));
            lines.Add(($"#{c.Index}  {Fmt(c.Mv)} mV  曲线设定 {Fmt(c.SetMhz)} MHz", TextC));
            if (c.RanMhz is { } ran) lines.Add(($"崩溃前实际 {Fmt(ran)} MHz", Dim));
            lines.Add((c.Change.Length > 64 ? c.Change[..64] + "…" : c.Change, Dim));
            at = new PointF(X(c.Mv), Y(c.SetMhz));
        }
        else if (_hoverRun is { } r0)
        {
            var r = _d.Runs[r0];
            lines.Add(($"实际运行  {Fmt(r.Mv)} mV · {Fmt(r.Mhz)} MHz", RunC));
            lines.Add(($"{Duration(r.Seconds)}（负载 ≥50 % 的采样）", Dim));
            at = new PointF(X(r.Mv), Y(r.Mhz));
        }
        else if (_hoverLine == 2)
        {
            lines.Add(("过渡顶：从这里起由你拖动", Accent));
            lines.Add(("左移：把更多点交给过渡带；右移：接管更多点", TextC));
            at = new PointF(X(_d.Mv[_top]), _plot.Top + 50 * _s);
        }
        else if (_hoverLine == 1)
        {
            lines.Add(("过渡脚：过渡带从这里开始，以下保持打开时的值", Accent));
            lines.Add(("左移：更缓，但低电压点抬得更多；右移到过渡顶：不做过渡", TextC));
            at = new PointF(X(_d.Mv[_bottom]), _plot.Top + 50 * _s);
        }
        else return;

        float s = _s, pad = 9 * s, lh = 0, tw = 0;
        foreach (var l in lines) { var sz = g.MeasureString(l.Text, _body); tw = Math.Max(tw, sz.Width); lh = Math.Max(lh, sz.Height); }
        float bw = tw + 2 * pad, bh = lines.Count * lh + 2 * pad - 2 * s;
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
        for (int k = 0; k < lines.Count; k++)
            using (var b = new SolidBrush(lines[k].Color)) g.DrawString(lines[k].Text, k == 0 ? _bold : _body, b, x + pad, y + pad + k * lh);
    }

    private static string Duration(int seconds) => seconds >= 3600 ? $"{seconds / 3600.0:0.#} 小时" : seconds >= 60 ? $"{seconds / 60.0:0.#} 分钟" : $"{seconds} 秒";

    private void Legend(Graphics g, float y)
    {
        float s = _s, x = _plot.Left;
        void Item(Action<float, float> swatch, string label)
        {
            swatch(x, y + 9 * s);
            using var b = new SolidBrush(AxisText);
            g.DrawString(label, _small, b, x + 26 * s, y);
            x += 26 * s + g.MeasureString(label, _small).Width + 16 * s;
        }
        Item((a, b) => { using var p = new Pen(CurveC, 2.2f * s); g.DrawLine(p, a, b, a + 20 * s, b); }, "当前曲线");
        Item((a, b) => { using var p = new Pen(Ghost, 1.5f * s) { DashStyle = DashStyle.Dash }; g.DrawLine(p, a, b, a + 20 * s, b); }, "打开时");
        Item((a, b) => { using var p = new Pen(StockC, 1.4f * s) { DashStyle = DashStyle.Dot }; g.DrawLine(p, a, b, a + 20 * s, b); }, "Stock");
        Item((a, b) => { using var br = new SolidBrush(ChangedC); g.FillEllipse(br, a + 7 * s, b - 3 * s, 6 * s, 6 * s); }, "已改动");
        Item((a, b) => { using var br = new SolidBrush(Color.FromArgb(60, Accent)); g.FillRectangle(br, a, b - 6 * s, 20 * s, 12 * s); using var p = new Pen(ChangedC, 1.3f * s); g.DrawEllipse(p, a + 7 * s, b - 3 * s, 6 * s, 6 * s); }, "过渡带（自动）");
        Item((a, b) =>
        {
            using var p = new Pen(CrashC, 2 * s);
            g.DrawLine(p, a + 2 * s, b - 5 * s, a + 12 * s, b + 5 * s); g.DrawLine(p, a + 2 * s, b + 5 * s, a + 12 * s, b - 5 * s);
            using var p2 = new Pen(Color.FromArgb(170, CrashC), 1.3f * s);
            g.DrawLine(p2, a + 15 * s, b - 4 * s, a + 23 * s, b + 4 * s); g.DrawLine(p2, a + 15 * s, b + 4 * s, a + 23 * s, b - 4 * s);
        }, $"崩溃过的点（{_d.Crashes.Count}；细线＝非曲线试验中）");
        Item((a, b) => { using var p = new Pen(Good, 2 * s); g.DrawLine(p, a + 4 * s, b, a + 16 * s, b); }, "标为稳定时的最高设定");
        Item((a, b) =>
        {
            using var b1 = new SolidBrush(Color.FromArgb(60, RunC)); using var b2 = new SolidBrush(Color.FromArgb(220, RunC));
            g.FillEllipse(b1, a + 2 * s, b - 2.5f * s, 5 * s, 5 * s); g.FillEllipse(b2, a + 11 * s, b - 4 * s, 8 * s, 8 * s);
        }, "实际运行的点（越亮越常跑）");
    }

    private void DrawButtons(Graphics g, float w, float h)
    {
        float s = _s, bh = 36 * s, y = h - bh - 16 * s, x = w - 28 * s;
        for (int k = _buttons.Count - 1; k >= 0; k--)
        {
            var bt = _buttons[k];
            string label = bt.Primary && _done ? "关闭" : bt.Label;   // saved with a warning: the person reads it, then closes
            float bw = g.MeasureString(label, _body).Width + 32 * s;
            if (k == 2) x -= 18 * s;   // a gap between editing and leaving
            x -= bw;
            var r = new RectangleF(x, y, bw, bh);
            _buttons[k] = (r, bt.Label, bt.Do, bt.Primary, bt.Enabled);
            x -= 10 * s;
            bool on = bt.Enabled(), hover = _hoverButton == k && on;
            using var path = Round(r, 7 * s);
            Color fill = bt.Primary ? (on ? (hover ? C(0x5a9cff) : Primary) : C(0x2a3a55)) : (hover ? ButtonHover : ButtonC);
            using (var fb = new SolidBrush(fill)) g.FillPath(fb, path);
            if (!bt.Primary) using (var bp = new Pen(PanelBorder, 1 * s)) g.DrawPath(bp, path);
            using var tb = new SolidBrush(on ? (bt.Primary ? Color.White : TextC) : Dim);
            var sz = g.MeasureString(label, bt.Primary ? _bold : _body);
            g.DrawString(label, bt.Primary ? _bold : _body, tb, r.X + (r.Width - sz.Width) / 2, r.Y + (r.Height - sz.Height) / 2);
        }
        string hint = "拖动点（过渡顶以上）· Shift+拖动 整段平移 · 拖「过渡脚 / 过渡顶」改过渡带 · 滚轮缩放（Ctrl 只缩频率）· 右键拖动平移 · 双击复位视图 · ↑↓ 微调 · Ctrl+Z 撤销";
        var area = new RectangleF(20 * s, y + (bh - 18 * s) / 2, Math.Max(10, x - 20 * s - 12 * s), 18 * s);
        using var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.LineLimit };
        using (var hb = new SolidBrush(Dim)) g.DrawString(hint, _small, hb, area, fmt);
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
        if (disposing) { _body?.Dispose(); _small?.Dispose(); _title?.Dispose(); _bold?.Dispose(); _layer?.Dispose(); }
        base.Dispose(disposing);
    }

    // ---- the window's own thread ----

    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    private static readonly IntPtr PerMonitorV2 = new(-4);

    /// <summary>Shows the window on its own STA thread and returns at once; <paramref name="closed"/> gets its outcome.</summary>
    public static void Show(EditorData data, Func<SaveRequest, Action<string>, SaveResult> save, Action<string> closed)
    {
        var thread = new Thread(() =>
        {
            string outcome = "closed without saving";
            try
            {
                try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch (InvalidOperationException) { }
                try { SetThreadDpiAwarenessContext(PerMonitorV2); } catch (EntryPointNotFoundException) { }
                // An exception in a handler ends the window with its reason (the catch below), not with WinForms' Continue/Quit dialog.
                try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, true); } catch (InvalidOperationException) { }
                using var form = new CurveEditorForm(data, save);
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
