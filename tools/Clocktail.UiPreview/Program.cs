using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using Clocktail.Domain.Vf;
using Clocktail.Mcp.CurveEditor;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(CurveEditorForm form, string name) => (T)typeof(CurveEditorForm).GetField(name, Private)!.GetValue(form)!;
    private static void Set(CurveEditorForm form, string name, object value) => typeof(CurveEditorForm).GetField(name, Private)!.SetValue(form, value);
    private static void Call(CurveEditorForm form, string name, params object[] args) => typeof(CurveEditorForm).GetMethod(name, Private)!.Invoke(form, args);
    private static T Control<T>(CurveEditorForm form, string name) where T : Control => (T)form.Controls[name]!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            using var watchdog = new System.Threading.Timer(_ =>
            {
                Console.Error.WriteLine("UI preview timed out; a modal dialog or native operation did not return.");
                Environment.Exit(1);
            }, null, TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string output = Path.GetFullPath(args.Length == 2 && args[0] == "--output" ? args[1] : "out/ui-preview");
            Directory.CreateDirectory(output);
            var data = Fixture();
            SaveRequest? saved = null;
            using (var form = new CurveEditorForm(data, (request, progress) =>
                { saved = request; progress("Checking readback…"); return new(true, null, null); }, _ => new([], [], [])))
            {
                Console.WriteLine("Opening synthetic editor");
                form.Show(); Application.DoEvents();
                Check(Field<int[]>(form, "_off").SequenceEqual(data.Offsets), "Opening must preserve every imported offset.");
                Render(form, output, "opened", new(1320, 862));
                Set(form, "_sel", 66); Call(form, "RefreshChrome");
                var input = Control<NumericUpDown>(form, "PointOffset");
                input.Value += 30;
                var edited = (int[])Field<int[]>(form, "_off").Clone();
                Check(!edited.SequenceEqual(data.Offsets), "Numeric input must edit the draft.");
                Control<Button>(form, "Undo").PerformClick();
                Check(Field<int[]>(form, "_off").SequenceEqual(data.Offsets), "Undo must restore the exact opened curve.");
                Control<Button>(form, "Redo").PerformClick();
                Check(Field<int[]>(form, "_off").SequenceEqual(edited), "Redo must restore the edit.");
                Check(saved == null, "Edits and undo must never write to the GPU.");
                Render(form, output, "edited", new(1320, 862));
                Render(form, output, "compact", new(900, 660));
                // Hover the measured mark below a requested stable setting, away from the editable curve.
                Set(form, "_hoverEvidence", 1);
                Render(form, output, "evidence-detail", new(900, 660));
                Set(form, "_hoverEvidence", null!);
                Control<Button>(form, "RestoreOpened").PerformClick();
                Check(Field<int[]>(form, "_off").SequenceEqual(data.Offsets), "Restore opened must preserve imported values exactly.");
                Check(!Control<Button>(form, "RestoreOpened").Enabled, "A restored draft must be clean.");
                Control<Button>(form, "Smooth").PerformClick();
                var smooth = (int[])Field<int[]>(form, "_off").Clone();
                Check(!smooth.SequenceEqual(data.Offsets), "Smoothing should be an explicit edit.");
                Control<Button>(form, "Undo").PerformClick();
                Check(Field<int[]>(form, "_off").SequenceEqual(data.Offsets), "Smoothing must be undoable.");
                Control<Button>(form, "Redo").PerformClick();
                // Exercise cancel on a real captured drag; its transaction must not erase undo history.
                Render(form, output, "smooth", new(1320, 862));
                float x = (float)typeof(CurveEditorForm).GetMethod("X", Private)!.Invoke(form, [data.Mv[66]])!;
                double mhz = CurveModel.Effective(data.BaseKhz, smooth)[66] / 1000.0;
                float y = (float)typeof(CurveEditorForm).GetMethod("Y", Private)!.Invoke(form, [mhz])!;
                Call(form, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, (int)x, (int)y, 0));
                Call(form, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, (int)x, (int)y - 20, 0));
                Call(form, "ProcessCmdKey", Message.Create(form.Handle, 0x100, IntPtr.Zero, IntPtr.Zero), Keys.Escape);
                Check(Field<int[]>(form, "_off").SequenceEqual(smooth), "Esc / capture cancellation must restore the pre-drag draft.");
                Call(form, "ProcessCmdKey", Message.Create(form.Handle, 0x100, IntPtr.Zero, IntPtr.Zero), Keys.Up);
                Check(!Field<int[]>(form, "_off").SequenceEqual(smooth), "The selected canvas point must accept arrow-key edits after a click.");
                Call(form, "ProcessCmdKey", Message.Create(form.Handle, 0x100, IntPtr.Zero, IntPtr.Zero), Keys.Control | Keys.Z);
                Check(Field<int[]>(form, "_off").SequenceEqual(smooth), "Keyboard undo must restore an arrow-key edit.");
                var history = Control<CheckBox>(form, "ShowHistory");
                history.Checked = false;
                Check(Field<Bitmap?>(form, "_layer") == null, "Hiding residency must invalidate its cached drawing.");
                Render(form, output, "curve-only", new(900, 660));
                history.Checked = true;
                // Scale the same layout to exercise explicit per-monitor drawing and control bounds.
                Set(form, "_s", 1.5f); Call(form, "Fonts");
                Render(form, output, "layout-150pct", new(1980, 1293));
                Set(form, "_s", 1f); Call(form, "Fonts");
                form.ClientSize = new(1320, 862);
                // Typed text is committed by focus change before the save request is copied.
                decimal typed = input.Value + 15;
                int expectedOffset = CurveModel.Snap((double)typed * 1000);
                input.Focus(); input.Text = typed.ToString(System.Globalization.CultureInfo.CurrentCulture);
                Call(form, "ProcessCmdKey", Message.Create(form.Handle, 0x100, IntPtr.Zero, IntPtr.Zero), Keys.Control | Keys.S);
                PumpUntil(() => !Field<bool>(form, "_busy"));
                Check(saved != null && saved.Offsets.SequenceEqual(Field<int[]>(form, "_off")), "Save must copy the committed draft.");
                Check(saved!.Offsets[66] == expectedOffset, "Save must include the numeric text that was still being edited.");
                Check(Field<bool>(form, "_done"), "Successful save must display a final result.");
                Check(!input.Enabled && Control<Button>(form, "SaveAsDaily").Text == "Close", "Saved draft must be locked with a Close action.");
                Render(form, output, "saved", new(1320, 862));
                form.Close();
            }
            using (var form = new CurveEditorForm(data, (_, _) => new(false, null,
                "Synthetic readback mismatch. The broker restored the previous curve. This long diagnostic remains available in full through Details and can be copied."), _ => new([], [], [])))
            {
                form.Show(); Application.DoEvents();
                Control<Button>(form, "SaveAsDaily").PerformClick();
                PumpUntil(() => !Field<bool>(form, "_busy"));
                Check(!Field<bool>(form, "_done") && Control<Button>(form, "Details").Visible, "Failed saves must leave a readable diagnostic and editable draft.");
                Render(form, output, "save-error", new(900, 660));
                bool detailsChecked = false;
                using var timer = new System.Windows.Forms.Timer { Interval = 100 };
                timer.Tick += (_, _) =>
                {
                    var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(window => window != form);
                    if (dialog == null) return;
                    timer.Stop();
                    TextBox? FindText(Control root) => root is TextBox box ? box : root.Controls.Cast<Control>().Select(FindText).FirstOrDefault(box => box != null);
                    var diagnostic = FindText(dialog);
                    Check(diagnostic != null && diagnostic.ReadOnly && diagnostic.Text.Contains("This long diagnostic remains available in full"), "Details must preserve the entire diagnostic in a selectable field.");
                    WindowScreenshot(dialog, output, "save-details");
                    detailsChecked = true;
                    dialog.Close();
                };
                timer.Start();
                Control<Button>(form, "Details").PerformClick();
                Check(detailsChecked, "Details must open with the complete save diagnostic.");
                Set(form, "_discarding", true);
                form.Close();
            }
            Console.WriteLine("Windows editor smoke checks passed; synthetic previews: " + output);
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(10); }
        Check(done(), "Timed out waiting for the synthetic save callback.");
        Application.DoEvents();
    }

    private static void Render(CurveEditorForm form, string output, string name, Size size)
    {
        Console.WriteLine($"Rendering {name}: {size.Width} × {size.Height}");
        form.ClientSize = size;
        form.PerformLayout(); form.Refresh(); Application.DoEvents();
        foreach (Control control in form.Controls)
        {
            if (!control.Visible) continue;
            Check(form.ClientRectangle.Contains(control.Bounds), $"{name}: {control.Name} is outside the window.");
            Check(control.TabStop && !string.IsNullOrWhiteSpace(control.AccessibleName), $"{control.Name} must support keyboard navigation and an accessible name.");
            foreach (Control other in form.Controls)
                if (other != control && other.Visible) Check(!control.Bounds.IntersectsWith(other.Bounds), $"{name}: {control.Name} overlaps {other.Name}.");
        }
        WindowScreenshot(form, output, name);
    }

    private static void WindowScreenshot(Form form, string output, string name)
    {
        using var full = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(full, new(Point.Empty, form.Size));
        var client = form.PointToScreen(Point.Empty) - (Size)form.Location;
        using var image = full.Clone(new Rectangle(client, form.ClientSize), PixelFormat.Format32bppArgb);
        image.Save(Path.Combine(output, name + ".png"));
    }

    private static EditorData Fixture()
    {
        const int n = 101;
        double[] mv = [.. Enumerable.Range(0, n).Select(i => 700 + 5.0 * i)];
        int[] baseline = [.. Enumerable.Range(0, n).Select(i => CurveModel.Snap((i < 28 ? 1800 + 15.0 * i : 2220 + 12.5 * (i - 28)) * 1000))];
        int[] offsets = [.. Enumerable.Range(0, n).Select(i => i < 20 ? 0 : i <= 32 ? CurveModel.Snap(600000 * Math.Pow((i - 20) / 12.0, 1.8)) : CurveModel.Snap(600000 - (i - 32) * 6600))];
        var effective = CurveModel.Effective(baseline, offsets);
        double Clock(int i) => effective[i] / 1000.0;
        var random = new Random(42);
        var runs = Enumerable.Range(0, 1400).Select(_ =>
        {
            int i = random.Next(34, 83);
            return new RunBin(mv[i], Clock(i) - random.Next(0, 9) * 15, random.Next(1, 400));
        }).Append(new(mv[46], Clock(46) - 15, 2400)).ToArray();
        EvidenceMark[] evidence = [.. new[] { 38, 42, 46, 50, 54, 58, 62, 66, 70, 74, 78 }.Select(i =>
            new EvidenceMark(i, mv[i], Clock(i) + 15, 1800, Clock(i) + 45,
                i == 42 ? Clock(i) - 90 : i == 58 ? null : Clock(i) + 7.5))];
        var at = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        CrashMark[] crashes = [new(58, mv[58], Clock(58) + 90, Clock(58) + 45, at, "Synthetic game test", "V/F offset trial", true, "preview-a"),
            new(74, mv[74], Clock(74) + 75, Clock(74) + 45, at, "Synthetic game test", "V/F offset trial", true, "preview-b"),
            new(50, mv[50], Clock(50) + 30, null, at, "Synthetic memory test", "Memory offset trial", false, "preview-c")];
        var points = Enumerable.Range(0, n).Select(i => new VfPoint(i, (uint)(mv[i] * 1000), (uint)baseline[i], (uint)effective[i], offsets[i], (uint)(mv[i] * 1000))).ToArray();
        var curve = new VfCurveSnapshot(VfMode.Regional, VfLayoutId.Regional132, [], points, 0, [], [], []);
        return new("RTX 5090 · synthetic preview", mv, baseline, offsets, n, -1000000, 1000000, 780, 1100,
            crashes, evidence, runs, (20, 32), null, [], null, [], curve);
    }
}
