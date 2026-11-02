#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Clocktail.Mcp.CurveEditor;

public sealed partial class CurveEditorForm
{
    private Button _undoButton = null!, _redoButton = null!, _smoothButton = null!, _restoreButton = null!,
        _fitButton = null!, _saveButton = null!, _closeButton = null!, _detailsButton = null!,
        _previousButton = null!, _nextButton = null!;
    private CheckBox _showOpened = null!, _showReference = null!, _showHistory = null!, _showEvidence = null!, _showCrashes = null!;
    private NumericUpDown _offsetInput = null!;
    private readonly ToolTip _controlTips = new() { AutoPopDelay = 12000, InitialDelay = 400, ReshowDelay = 100 };
    private bool _syncControls, _discarding;

    private void BuildChrome()
    {
        Button Action(string text, string name, Action click, string tip)
        {
            var button = new Button
            {
                Text = text, Name = name, AccessibleName = text, FlatStyle = FlatStyle.Flat,
                BackColor = PanelC, ForeColor = TextC, UseVisualStyleBackColor = false,
                TabIndex = Controls.Count, Cursor = Cursors.Hand,
            };
            button.FlatAppearance.BorderColor = PanelBorder;
            button.FlatAppearance.MouseOverBackColor = C(0x29374a);
            button.FlatAppearance.MouseDownBackColor = C(0x314763);
            button.Click += (_, _) => { if (!_busy) click(); };
            _controlTips.SetToolTip(button, tip);
            Controls.Add(button);
            return button;
        }
        _undoButton = Action("Undo", "Undo", Undo, "Undo the last edit · Ctrl+Z");
        _redoButton = Action("Redo", "Redo", Redo, "Redo an undone edit · Ctrl+Y / Ctrl+Shift+Z");
        _smoothButton = Action("Smooth", "Smooth", Smooth, "Smooth the draft and reshape its transition band. GPU settings stay unchanged.");
        _restoreButton = Action("Restore opened", "RestoreOpened", Reset, "Restore the exact curve and band read when this window opened. This can be undone.");
        _fitButton = Action("Fit view", "FitView", () => { ResetView(); Invalidate(); }, "Fit the draft and opened curve · Ctrl+0 / double-click empty canvas");

        CheckBox Layer(string text, string name, bool shown, string tip)
        {
            var box = new CheckBox
            {
                Text = text, Name = name, AccessibleName = text + " layer", Checked = shown,
                Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AxisText, BackColor = Bg, TabIndex = Controls.Count, Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
            };
            box.FlatAppearance.BorderColor = PanelBorder;
            box.FlatAppearance.CheckedBackColor = C(0x24374b);
            box.FlatAppearance.MouseOverBackColor = PanelC;
            box.CheckedChanged += (_, _) =>
            {
                box.ForeColor = box.Checked ? TextC : Dim;
                _hoverCrash = _hoverRun = _hoverEvidence = null;
                _layer?.Dispose(); _layer = null;
                Invalidate();
            };
            box.ForeColor = shown ? TextC : Dim;
            _controlTips.SetToolTip(box, tip);
            Controls.Add(box);
            return box;
        }
        _showOpened = Layer("Opened", "ShowOpened", true, "The GPU curve read when this window opened (dashed). Hidden where identical to the draft.");
        _showReference = Layer("Base", "ShowReference", false, "Base clocks at the opening temperature, including any P0 core offset. This is not necessarily factory stock.");
        _showHistory = Layer("Load history", "ShowHistory", true, "Historical residency at ≥50% load. Brighter dots mean more time. This is a snapshot, not live telemetry.");
        _showEvidence = Layer("Test evidence", "ShowEvidence", true, "Green: sampled clock in a stable test. Amber: the stable setting was not reached, or has no samples. Marks do not certify the current draft.");
        _showCrashes = Layer("Crashes", "ShowCrashes", true, "Crosses at crash settings; solid for curve trials, lighter for other trials.");

        _previousButton = Action("‹", "PreviousPoint", () => SelectPoint(-1), "Select the previous curve point");
        _nextButton = Action("›", "NextPoint", () => SelectPoint(1), "Select the next curve point");
        _previousButton.AccessibleName = "Previous point";
        _nextButton.AccessibleName = "Next point";
        _offsetInput = new NumericUpDown
        {
            Name = "PointOffset", AccessibleName = "Selected point offset in MHz", DecimalPlaces = 1,
            Increment = CurveModel.GridKhz / 1000m,
            Minimum = Math.Min(_d.LowKhz, _opened.Min()) / 1000m,
            Maximum = Math.Max(_d.HighKhz, _opened.Max()) / 1000m,
            BackColor = PlotBg, ForeColor = TextC, BorderStyle = BorderStyle.FixedSingle,
            TextAlign = HorizontalAlignment.Right, TabIndex = Controls.Count,
        };
        _offsetInput.ValueChanged += (_, _) =>
        {
            if (_syncControls || _busy || _done || _sel is not { } i || !Draggable(i)) return;
            int offset = CurveModel.Snap((double)_offsetInput.Value * 1000);
            if (offset == _off[i]) return;
            Push();
            var next = (int[])_off.Clone();
            next[i] = offset;
            _off = Rules(next, i);
            FinishEdit();
            Say("Point offset updated. Draft follows the curve's transition and driver rules.", Dim);
        };
        _controlTips.SetToolTip(_offsetInput, "Enter an offset in MHz. Edits snap to 7.5 MHz; the driver's effective clock is shown beside it. Up/Down steps by 7.5 MHz.");
        Controls.Add(_offsetInput);

        _detailsButton = Action("Details…", "Details", ShowDetails, "Read or copy the complete save result and the other settings kept as Daily.");
        _closeButton = Action("Cancel", "Cancel", Close, "Close the editor. An unsaved draft prompts before it is discarded.");
        _saveButton = Action("Save as Daily", "SaveAsDaily", Save, "Apply the draft to the GPU, verify readback and keep it as the Daily profile · Ctrl+S");
        _saveButton.BackColor = Primary;
        _saveButton.ForeColor = Color.White;
        _saveButton.FlatAppearance.BorderColor = Primary;
        _saveButton.FlatAppearance.MouseOverBackColor = C(0x559aff);
        _saveButton.FlatAppearance.MouseDownBackColor = C(0x2872df);
        CancelButton = _closeButton;
        ApplyChromeFonts();
        LayoutChrome();
    }

    private void SelectPoint(int delta)
    {
        _sel = Math.Clamp((_sel ?? _top) + delta, 0, _off.Length - 1);
        RefreshChrome(); Invalidate();
    }

    private void ApplyChromeFonts()
    {
        if (_undoButton == null) return;
        foreach (Control control in Controls) control.Font = _body;
        if (_saveButton != null) _saveButton.Font = _bold;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        LayoutChrome();
    }

    private void LayoutChrome()
    {
        if (_saveButton == null) return;
        float s = _s, w = ClientSize.Width, h = ClientSize.Height;
        void Place(Control control, float x, float y, float width, float height) =>
            control.Bounds = Rectangle.Round(new RectangleF(x * s, y * s, width * s, height * s));
        float width = w / s, height = h / s;
        Place(_undoButton, 24, 88, 68, 33);
        Place(_redoButton, 100, 88, 68, 33);
        Place(_smoothButton, 184, 88, 86, 33);
        Place(_restoreButton, 278, 88, 132, 33);
        Place(_fitButton, 426, 88, 82, 33);
        float x = 80;
        foreach (var (box, size) in new[] { (_showOpened, 80), (_showReference, 64), (_showHistory, 108), (_showEvidence, 114), (_showCrashes, 78) })
        {
            Place(box, x, 136, size, 28); x += size + 8;
        }
        Place(_previousButton, 186, height - 132, 28, 28);
        Place(_nextButton, 220, height - 132, 28, 28);
        Place(_offsetInput, width - 198, height - 117, 150, 30);
        Place(_saveButton, width - 212, height - 58, 188, 36);
        Place(_closeButton, width - 310, height - 58, 86, 36);
        Place(_detailsButton, width - 408, height - 58, 86, 36);
    }

    private void RefreshChrome()
    {
        if (_saveButton == null) return;
        _undoButton.Enabled = !_busy && !_done && _history.CanUndo;
        _redoButton.Enabled = !_busy && !_done && _history.CanRedo;
        _smoothButton.Enabled = !_busy && !_done;
        _restoreButton.Enabled = !_busy && !_done && Dirty;
        _fitButton.Enabled = !_busy;
        _closeButton.Enabled = !_busy;
        _previousButton.Enabled = !_busy && _sel > 0;
        _nextButton.Enabled = !_busy && _sel < _off.Length - 1;
        _offsetInput.Enabled = !_busy && !_done && _sel is { } selected && Draggable(selected);
        _syncControls = true;
        try { if (_sel is { } i) _offsetInput.Value = Math.Clamp(_off[i] / 1000m, _offsetInput.Minimum, _offsetInput.Maximum); }
        finally { _syncControls = false; }
        _saveButton.Enabled = !_busy;
        _saveButton.Text = _busy ? "Saving…" : _done ? "Close" : "Save as Daily";
        _closeButton.Visible = !_done;
        _detailsButton.Visible = _saveError != null || _d.AlsoKept != null;
        _saveButton.AccessibleDescription = _done ? _status : "Writes the draft to the GPU and keeps it as Daily. " + (_d.AlsoKept ?? "");
    }

    private static void TextLine(Graphics g, string text, Font font, Color color, RectangleF bounds, StringAlignment align = StringAlignment.Near)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, Alignment = align, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, bounds, format);
    }

    private void Header(Graphics g, float w)
    {
        float s = _s;
        TextLine(g, "V/F Curve", _title, TextC, new(24 * s, 14 * s, 150 * s, 34 * s));
        TextLine(g, _d.Gpu, _body, AxisText, new(192 * s, 20 * s, Math.Max(50, w - 408 * s), 26 * s));
        string badge = _busy ? "SAVING" : _done ? "SAVED AS DAILY" : "UNAPPLIED DRAFT";
        var badgeRect = new RectangleF(w - 182 * s, 20 * s, 158 * s, 26 * s);
        using (var path = Round(badgeRect, 5 * s))
        using (var brush = new SolidBrush(Color.FromArgb(28, _done ? Good : Accent))) g.FillPath(brush, path);
        TextLine(g, badge, _small, _done ? Good : Accent, badgeRect, StringAlignment.Center);
        TextLine(g, _status, _body, _statusColor, new(24 * s, 52 * s, w - 48 * s, 24 * s));
        TextLine(g, "LAYERS", _small, Dim, new(24 * s, 136 * s, 54 * s, 28 * s));
        if (w / s >= 1050)
            TextLine(g, "Drag a point · Shift: move tail · Scroll: zoom", _small, Dim, new(540 * s, 88 * s, w - 564 * s, 33 * s), StringAlignment.Far);
        if (w / s >= 970)
            TextLine(g, "History snapshot · not live", _small, Dim, new(660 * s, 136 * s, w - 684 * s, 28 * s), StringAlignment.Far);
        using var pen = new Pen(C(0x253140), s);
        g.DrawLine(pen, 24 * s, 176 * s, w - 24 * s, 176 * s);
    }

    private void Inspector(Graphics g, float w, float h)
    {
        float s = _s, top = h - 154 * s;
        using (var path = Round(new(24 * s, top, w - 48 * s, 80 * s), 8 * s))
        using (var brush = new SolidBrush(PanelC))
        using (var pen = new Pen(PanelBorder, s)) { g.FillPath(brush, path); g.DrawPath(pen, path); }
        if (_sel is not { } i) return;
        string mode = i >= _d.Writable ? "Read only" : InBand(i) ? "Transition · automatic" : i < _top ? "Below band · preserved" : "Editable point";
        TextLine(g, $"POINT {i} · {mode}", _small, AxisText, new(40 * s, top + 10 * s, 210 * s, 20 * s));
        TextLine(g, $"{Fmt(_d.Mv[i])} mV", _bold, TextC, new(40 * s, top + 34 * s, 140 * s, 27 * s));
        float middle = 278 * s, available = w - 514 * s;
        TextLine(g, $"Draft  {Fmt(Mhz(i, _off))} MHz", _bold, TextC, new(middle, top + 10 * s, available, 24 * s));
        double delta = Mhz(i, _off) - Mhz(i, _opened);
        TextLine(g, $"Opened {Fmt(Mhz(i, _opened))} MHz  ·  {Signed(delta)} MHz", _small, Math.Abs(delta) > 0.01 ? Accent : AxisText,
            new(middle, top + 37 * s, available, 22 * s));
        TextLine(g, "OFFSET · MHz", _small, AxisText, new(w - 198 * s, top + 9 * s, 150 * s, 23 * s));
    }

    private void Footer(Graphics g, float w, float h)
    {
        float s = _s;
        int changed = _off.Where((value, i) => value != _opened[i]).Count();
        int above = AboveCrash();
        string summary = _done ? (_saveError == null ? "Readback verified · Daily profile saved" : "Saved with a warning · see Details")
            : _d.AlsoKept != null ? "Also keeps other GPU settings · see Details"
            : above > 0 ? $"{above} points at / above a crashed curve setting"
            : changed == 0 ? "GPU unchanged · edits apply only when you save" : $"{changed} points changed · max lift {Signed(MaxLift().Lift)} MHz";
        float right = _detailsButton.Visible ? w - 424 * s : w - 326 * s;
        TextLine(g, summary, _small, above > 0 && !_done ? C(0xefc078) : Dim, new(24 * s, h - 58 * s, Math.Max(20, right - 24 * s), 36 * s));
    }

    private void ShowDetails()
    {
        using var detailFont = (Font)_body.Clone();
        using var dialog = new Form
        {
            Text = "Clocktail · Draft and save details", ShowIcon = false, BackColor = Bg, ForeColor = TextC,
            StartPosition = FormStartPosition.CenterParent, Size = new(720, 470), MinimumSize = new(480, 320),
            Font = detailFont, AutoScaleMode = AutoScaleMode.Dpi,
        };
        string details = _status + "\r\n\r\n";
        if (_saveError != null) details += "Save result\r\n" + _saveError + "\r\n\r\n";
        details += _d.AlsoKept == null ? "Save as Daily applies this curve and keeps it as the Daily profile.\r\n"
            : "Other settings included in Save as Daily\r\n" + _d.AlsoKept + "\r\n";
        details += "\r\nOpened curve = the GPU snapshot read when the editor opened.\r\nLoad history and test evidence = historical records, not live telemetry.\r\nGreen marks show sampled clocks in stable tests; amber marks are settings without matching samples.\r\nHistorical evidence does not guarantee that the current draft is stable.";
        var text = new TextBox { Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, BackColor = PlotBg, ForeColor = TextC, BorderStyle = BorderStyle.None,
            Text = details, AccessibleName = "Complete draft and save details" };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new(8) };
        Button DetailAction(string caption) => new()
        {
            Text = caption, AccessibleName = caption, AutoSize = true, MinimumSize = new(110, 30),
            FlatStyle = FlatStyle.Flat, BackColor = PanelC, ForeColor = TextC, UseVisualStyleBackColor = false,
        };
        var close = DetailAction("Close"); close.DialogResult = DialogResult.Cancel;
        var copy = DetailAction("Copy details");
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(details); copy.Text = "Copied"; }
            catch (System.Runtime.InteropServices.ExternalException) { copy.Text = "Try copy again"; }
        };
        buttons.Controls.Add(close); buttons.Controls.Add(copy);
        var content = new Panel { Dock = DockStyle.Fill, Padding = new(20) };
        content.Controls.Add(text);
        dialog.Controls.Add(content); dialog.Controls.Add(buttons);
        dialog.CancelButton = close;
        dialog.ShowDialog(this);
    }
}
#endif
