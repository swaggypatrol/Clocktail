#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Clocktail.Mcp.CurveEditor;

public sealed partial class CurveEditorForm
{
    /// <summary>The caller clips to the plot. Draw only coordinates inside the view; never pin an off-screen
    /// measurement to an edge or move the viewport while the person is editing.</summary>
    private void DrawLiveCrosshair(Graphics g)
    {
        if (_livePoint is not { } point) return;
        bool voltageVisible = point.Mv >= _vx0 && point.Mv <= _vx1;
        bool clockVisible = point.Mhz >= _vy0 && point.Mhz <= _vy1;
        float x = X(point.Mv), y = Y(point.Mhz), s = _s;
        using var dashed = new Pen(Color.FromArgb(175, LiveC), 1.2f * s) { DashPattern = [4, 4] };
        if (voltageVisible) g.DrawLine(dashed, x, _plot.Top, x, _plot.Bottom);
        if (clockVisible) g.DrawLine(dashed, _plot.Left, y, _plot.Right, y);
        if (!voltageVisible || !clockVisible) return;
        using var halo = new SolidBrush(Color.FromArgb(38, LiveC));
        using var fill = new SolidBrush(LiveC);
        using var outline = new Pen(PlotBg, 1.5f * s);
        g.FillEllipse(halo, x - 8 * s, y - 8 * s, 16 * s, 16 * s);
        g.FillEllipse(fill, x - 3.5f * s, y - 3.5f * s, 7 * s, 7 * s);
        g.DrawEllipse(outline, x - 3.5f * s, y - 3.5f * s, 7 * s, 7 * s);
    }

    /// <summary>Small axis tags connect the readout to the dashed guides, above the static axis labels.</summary>
    private void DrawLiveAxisTags(Graphics g)
    {
        if (_livePoint is not { } point) return;
        float s = _s;
        void Tag(string text, bool voltage)
        {
            var size = g.MeasureString(text, _small);
            float width = size.Width + 10 * s, height = size.Height + 4 * s;
            float x = voltage ? Math.Clamp(X(point.Mv) - width / 2, _plot.Left, _plot.Right - width) : _plot.Left - width - 3 * s;
            float y = voltage ? _plot.Bottom + 3 * s : Math.Clamp(Y(point.Mhz) - height / 2, _plot.Top, _plot.Bottom - height);
            var rect = new RectangleF(x, y, width, height);
            using var path = Round(rect, 3 * s);
            using var fill = new SolidBrush(PanelC);
            using var border = new Pen(Color.FromArgb(150, LiveC), s);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            TextLine(g, text, _small, LiveC, rect, StringAlignment.Center);
        }
        if (point.Mv >= _vx0 && point.Mv <= _vx1) Tag(Fmt(point.Mv), voltage: true);
        if (point.Mhz >= _vy0 && point.Mhz <= _vy1) Tag(Fmt(point.Mhz), voltage: false);
    }
}
#endif
