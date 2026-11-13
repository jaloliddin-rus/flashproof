using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using FlashProof.Core;

namespace FlashProof.Views;

/// <summary>
/// Speed over the test (design §6.4): MB/s per 256 MiB slice against the position in the test, one line for writing
/// and one for reading back, on one axis. Hovering shows the slice under the pointer.
/// </summary>
public sealed class SpeedChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<SpeedSlice>?> WriteSlicesProperty =
        AvaloniaProperty.Register<SpeedChart, IReadOnlyList<SpeedSlice>?>(nameof(WriteSlices));

    public static readonly StyledProperty<IReadOnlyList<SpeedSlice>?> ReadSlicesProperty =
        AvaloniaProperty.Register<SpeedChart, IReadOnlyList<SpeedSlice>?>(nameof(ReadSlices));

    /// <summary>The x axis' length in bytes: what the test planned to write.</summary>
    public static readonly StyledProperty<long> TotalBytesProperty = AvaloniaProperty.Register<SpeedChart, long>(nameof(TotalBytes));

    // The reference palette's first two categorical slots, stepped for each theme; validated as a pair in both.
    static readonly Color WriteLight = Color.Parse("#2a78d6"), WriteDark = Color.Parse("#3987e5");
    static readonly Color ReadLight = Color.Parse("#eb6834"), ReadDark = Color.Parse("#d95926");
    static readonly Color InkLight = Color.Parse("#52514e"), InkDark = Color.Parse("#c3c2b7");
    static readonly Color GridLight = Color.Parse("#e0dfdb"), GridDark = Color.Parse("#3a3a38");

    const double Left = 64, Right = 12, Top = 30, Bottom = 26, FontSize = 12;

    double? hoverX;

    static SpeedChart() => AffectsRender<SpeedChart>(WriteSlicesProperty, ReadSlicesProperty, TotalBytesProperty);

    /// <summary>The colors follow the theme, so a theme change (Windows switching to dark, say) redraws the chart.</summary>
    public SpeedChart() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    public IReadOnlyList<SpeedSlice>? WriteSlices
    {
        get => GetValue(WriteSlicesProperty);
        set => SetValue(WriteSlicesProperty, value);
    }

    public IReadOnlyList<SpeedSlice>? ReadSlices
    {
        get => GetValue(ReadSlicesProperty);
        set => SetValue(ReadSlicesProperty, value);
    }

    public long TotalBytes
    {
        get => GetValue(TotalBytesProperty);
        set => SetValue(TotalBytesProperty, value);
    }

    bool Dark => ActualThemeVariant == ThemeVariant.Dark;

    static double MBps(SpeedSlice s) => s.Seconds > 0 ? s.Bytes / s.Seconds / 1e6 : 0;

    /// <summary>The top of the y axis: the fastest slice, rounded up to 1, 2 or 5 × a power of ten.</summary>
    static double NiceMax(double max)
    {
        if (max <= 0)
            return 10;
        double power = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (double step in new[] { 1, 2, 5, 10 })
        {
            if (step * power >= max)
                return step * power;
        }
        return 10 * power;
    }

    (Rect Plot, double MaxSpeed, double Length) Layout()
    {
        var all = (WriteSlices ?? []).Concat(ReadSlices ?? []).ToList();
        double length = Math.Max(TotalBytes, all.Count > 0 ? all.Max(s => s.Offset + s.Bytes) : 0);
        var plot = new Rect(Left, Top, Math.Max(0, Bounds.Width - Left - Right), Math.Max(0, Bounds.Height - Top - Bottom));
        return (plot, NiceMax(all.Count > 0 ? all.Max(MBps) : 0), Math.Max(length, 1));
    }

    FormattedText Label(string text, Color color) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, FontSize, new SolidColorBrush(color));

    public override void Render(DrawingContext context)
    {
        var (plot, maxSpeed, length) = Layout();
        if (plot.Width <= 0 || plot.Height <= 0)
            return;
        var ink = Dark ? InkDark : InkLight;
        var gridPen = new Pen(new SolidColorBrush(Dark ? GridDark : GridLight), 1);

        // Recessive grid and axis labels: 0, half and the top of the y axis; start and end of the x axis.
        foreach (double fraction in new[] { 0, 0.5, 1 })
        {
            double y = plot.Bottom - fraction * plot.Height;
            context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Label(Text.Speed(fraction * maxSpeed * 1e6), ink);
            context.DrawText(label, new Point(plot.Left - label.Width - 8, y - label.Height / 2));
        }
        var start = Label("0 GB", ink);
        context.DrawText(start, new Point(plot.Left, plot.Bottom + 6));
        var end = Label(Text.Bytes((long)length), ink);
        context.DrawText(end, new Point(plot.Right - end.Width, plot.Bottom + 6));

        Point At(SpeedSlice s) => new(plot.Left + (s.Offset + s.Bytes / 2.0) / length * plot.Width, plot.Bottom - Math.Min(MBps(s) / maxSpeed, 1) * plot.Height);

        void Series(IReadOnlyList<SpeedSlice>? slices, Color color)
        {
            if (slices is not { Count: > 0 })
                return;
            var pen = new Pen(new SolidColorBrush(color), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(At(slices[0]), isFilled: false);
                foreach (var s in slices.Skip(1))
                    g.LineTo(At(s));
                g.EndFigure(isClosed: false);
            }
            context.DrawGeometry(null, pen, geometry);
            if (slices.Count == 1)
                context.DrawEllipse(new SolidColorBrush(color), null, At(slices[0]), 4, 4);
        }

        var writeColor = Dark ? WriteDark : WriteLight;
        var readColor = Dark ? ReadDark : ReadLight;
        Series(WriteSlices, writeColor);
        Series(ReadSlices, readColor);

        // The legend, always there for the two series; the text stays in ink, the swatch carries the color.
        double x = plot.Left;
        foreach (var (name, color) in new[] { (Text.ChartWrite, writeColor), (Text.ChartRead, readColor) })
        {
            context.DrawRectangle(new SolidColorBrush(color), null, new Rect(x, 8, 14, 4), 2, 2);
            var label = Label(name, ink);
            context.DrawText(label, new Point(x + 20, 10 - label.Height / 2));
            x += 20 + label.Width + 24;
        }

        if (hoverX is { } hx && hx >= plot.Left && hx <= plot.Right)
            context.DrawLine(new Pen(new SolidColorBrush(ink), 1), new Point(hx, plot.Top), new Point(hx, plot.Bottom));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var (plot, _, length) = Layout();
        double px = e.GetPosition(this).X;
        hoverX = px;
        long offset = (long)((px - plot.Left) / Math.Max(plot.Width, 1) * length);
        string? Under(IReadOnlyList<SpeedSlice>? slices, string name) =>
            slices?.FirstOrDefault(s => offset >= s.Offset && offset < s.Offset + s.Bytes) is { } s ? $"{name}: {Text.Speed(MBps(s) * 1e6)}" : null;
        var lines = new[] { Under(WriteSlices, Text.ChartWrite), Under(ReadSlices, Text.ChartRead) }.OfType<string>().ToList();
        ToolTip.SetTip(this, lines.Count > 0 ? $"At {Text.Bytes(Math.Max(0, offset))}\n{string.Join("\n", lines)}" : null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        hoverX = null;
        ToolTip.SetTip(this, null);
        InvalidateVisual();
    }
}
