namespace SpaceMonger.UI;

/// <summary>Minimal owner-drawn horizontal bar chart: swatch, label, value text, proportional bar.</summary>
public sealed class BarList : Control
{
    public sealed record Bar(string Label, string Value, double Fraction, Color Color, object? Tag = null);

    private IReadOnlyList<Bar> _bars = [];
    private const int RowHeight = 24;
    private readonly Tween _grow;
    private double _progress = 1;

    public BarList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = SystemColors.Window;
        _grow = new Tween(450, t => { _progress = t; Invalidate(); });
    }

    /// <summary>Setting new bars replays the grow-in animation.</summary>
    public IReadOnlyList<Bar> Bars
    {
        get => _bars;
        set
        {
            _bars = value;
            Height = Math.Max(RowHeight, value.Count * RowHeight + 6);
            if (value.Count > 0) _grow.Start(); else Invalidate();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _grow.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        double max = _bars.Count > 0 ? Math.Max(1e-9, _bars.Max(b => b.Fraction)) : 1;
        int labelW = Math.Min(130, Width / 3), valueW = 150;
        int barX = labelW + 22, barW = Math.Max(10, Width - barX - valueW - 8);
        for (int i = 0; i < _bars.Count; i++)
        {
            var b = _bars[i];
            int y = 3 + i * RowHeight;
            using (var sw = new SolidBrush(b.Color)) g.FillRectangle(sw, 6, y + 6, 11, 11);
            TextRenderer.DrawText(g, b.Label, Font, new Rectangle(22, y, labelW, RowHeight), ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            // Rows start slightly staggered so the chart "pours" in from the top.
            double rowT = Math.Clamp(_progress * 1.3 - i * 0.04, 0, 1);
            int w = (int)(barW * b.Fraction / max * rowT);
            using (var bb = new SolidBrush(Color.FromArgb(200, b.Color))) g.FillRectangle(bb, barX, y + 5, Math.Max(w, 1), RowHeight - 10);
            TextRenderer.DrawText(g, b.Value, Font, new Rectangle(barX + barW + 6, y, valueW, RowHeight), SystemColors.GrayText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
