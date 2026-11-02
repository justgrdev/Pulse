using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Pulse;

/// <summary>Scrolling 60-second download/upload area chart.</summary>
public sealed class GraphControl : FrameworkElement
{
    const int N = 60;
    readonly double[] _d = new double[N], _u = new double[N];
    double _max = 125_000;
    public bool UseBits { get; set; }

    static Brush B(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
    static readonly Brush DownFill = Grad(0x60, 0xCD, 0xFF), UpFill = Grad(0x7B, 0xE0, 0x8A);
    static readonly Pen DownPen = Stroke(0x60, 0xCD, 0xFF), UpPen = Stroke(0x7B, 0xE0, 0x8A);
    static readonly Pen GridPen = Stroke(0xFF, 0xFF, 0xFF, 0x14, 1);
    static readonly Brush LabelBrush = B("#8BFFFFFF");

    static Brush Grad(byte r, byte g, byte b)
    {
        var br = new LinearGradientBrush(Color.FromArgb(0x59, r, g, b), Color.FromArgb(0x04, r, g, b), 90);
        br.Freeze(); return br;
    }
    static Pen Stroke(byte r, byte g, byte b, byte a = 255, double t = 1.75)
    {
        var p = new Pen(new SolidColorBrush(Color.FromArgb(a, r, g, b)), t) { LineJoin = PenLineJoin.Round };
        p.Freeze(); return p;
    }

    public void Push(double down, double up)
    {
        Array.Copy(_d, 1, _d, 0, N - 1); _d[N - 1] = down;
        Array.Copy(_u, 1, _u, 0, N - 1); _u[N - 1] = up;
        double peak = Math.Max(_d.Max(), _u.Max());
        double target = Math.Max(peak * 1.25, 125_000);
        _max += (target - _max) * (target > _max ? 0.6 : 0.12);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        for (int i = 1; i < 4; i++) dc.DrawLine(GridPen, new Point(0, Math.Round(h * i / 4) + .5), new Point(w, Math.Round(h * i / 4) + .5));
        Series(dc, _u, w, h, UpFill, UpPen);
        Series(dc, _d, w, h, DownFill, DownPen);
        var ft = new FormattedText(Fmt.Rate(_max, UseBits), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable Text"), 11, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(w - ft.Width - 2, 2));
    }

    void Series(DrawingContext dc, double[] v, double w, double h, Brush fill, Pen pen)
    {
        double step = w / (N - 1);
        Point P(int i) => new(i * step, h - 2 - Math.Min(v[i] / _max, 1) * (h - 8));
        var area = new StreamGeometry();
        var line = new StreamGeometry();
        using (var c = area.Open())
        {
            c.BeginFigure(new Point(0, h), true, true);
            c.LineTo(P(0), true, false);
            for (int i = 1; i < N; i++) { var a = P(i - 1); var b = P(i); c.QuadraticBezierTo(a, new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), true, false); }
            c.LineTo(P(N - 1), true, false);
            c.LineTo(new Point(w, h), true, false);
        }
        using (var c = line.Open())
        {
            c.BeginFigure(P(0), false, false);
            for (int i = 1; i < N; i++) { var a = P(i - 1); var b = P(i); c.QuadraticBezierTo(a, new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), true, false); }
            c.LineTo(P(N - 1), true, false);
        }
        area.Freeze(); line.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, pen, line);
    }
}
