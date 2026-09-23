using System.Buffers;
using System.Runtime.InteropServices;
using SpaceMonger.Analysis;

namespace SpaceMonger;

public enum ColorMode
{
    /// <summary>Hue by nesting depth, brightness by size.</summary>
    Depth,
    /// <summary>Files coloured by category (video, code, archive...).</summary>
    FileType,
    /// <summary>Files coloured by last-modified age: green = recent, red = old.</summary>
    Age,
}

/// <summary>What is under the mouse: a real node, or the aggregated "N files" block of a directory.</summary>
public readonly record struct TreemapHit(Node? Node, Node Owner, long Size, int FileGroupCount)
{
    public bool IsFileGroup => Node == null;
}

/// <summary>
/// Nested squarified treemap. The back buffer is a GDI DIB section: rectangles are rasterized
/// straight into its memory (Span.Fill per scanline), labels are drawn with DrawTextW on the same
/// DC (no GDI+/GDI copies), and painting is a single BitBlt. Layout is rebuilt only when
/// the data, view root or size changes; hover just repaints an outline over the cached bitmap.
///
/// Colour scheme: hue = nesting depth below the view root, brightness = size on a log scale
/// relative to the view root (bright = dense/large, dark = small).
/// </summary>
public sealed unsafe partial class TreemapView : Control
{
    private const int HeaderHeight = 15;
    private const int BorderColor = unchecked((int)0xFF151515);
    private const int BackColorArgb = unchecked((int)0xFF1E1E1E);

    // Distinct hues per depth level, cycling.
    private static readonly double[] DepthHues = [210, 140, 42, 330, 265, 95, 15, 185];

    private Node? _viewRoot;
    private Node? _selected;
    private ColorMode _colorMode;
    private long _now;
    private bool _showFiles = true;
    private bool _dirty = true;

    private nint _memDC, _dib, _oldBitmap, _bits, _hFont;
    private int _w, _h;
    private Span<int> Pixels => new((void*)_bits, _w * _h);

    private readonly List<Item> _items = new(4096);
    private readonly List<(Rectangle rect, string text, int bg)> _labels = new(512);
    private int _hover = -1;
    private long _rootSize;

    private readonly Font _labelFont = new("Segoe UI", 8f);

    private struct Item
    {
        public Rectangle Rect;
        public Node? Node;
        public Node Owner;
        public long Size;
        public int Count;
    }

    private struct Entry
    {
        public Node? Node;  // null => aggregated files
        public long Size;
        public int Count;
    }

    public event EventHandler<TreemapHit?>? HoverChanged;
    public event EventHandler<TreemapHit>? ItemActivated;
    public event EventHandler<(TreemapHit? hit, int delta)>? WheelZoom;

    public TreemapView()
    {
        // No OptimizedDoubleBuffer: the DIB already is a complete back buffer.
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque |
                 ControlStyles.Selectable, true);
        TabStop = true;
    }

    public Node? ViewRoot
    {
        get => _viewRoot;
        set { _viewRoot = value; Rebuild(); }
    }

    public ColorMode ColorMode
    {
        get => _colorMode;
        set { _colorMode = value; Rebuild(); }
    }

    /// <summary>Item outlined in cyan (e.g. picked from the insights panel).</summary>
    public Node? Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public bool ShowFiles
    {
        get => _showFiles;
        set { _showFiles = value; Rebuild(); }
    }

    /// <summary>True once all children are sorted by size (after a scan completes) — skips per-frame sorting.</summary>
    public bool ChildrenSorted { get; set; }

    public TreemapHit? Hovered => _hover >= 0 && _hover < _items.Count ? ToHit(_items[_hover]) : null;

    /// <summary>Marks the layout stale (data changed) and repaints.</summary>
    public void Rebuild()
    {
        _dirty = true;
        Invalidate();
    }

    public TreemapHit? HitTest(Point p)
    {
        int i = HitIndex(p);
        return i >= 0 ? ToHit(_items[i]) : null;
    }

    private static TreemapHit ToHit(in Item it) => new(it.Node, it.Owner, it.Size, it.Count);

    // ---------------------------------------------------------------- painting

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Rebuild();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_dirty || _memDC == 0) Render();
        var clip = e.ClipRectangle;
        nint hdc = e.Graphics.GetHdc();
        try { BitBlt(hdc, clip.X, clip.Y, clip.Width, clip.Height, _memDC, clip.X, clip.Y, SRCCOPY); }
        finally { e.Graphics.ReleaseHdc(hdc); }

        if (_selected != null)
        {
            // Outline the selection, or its nearest visible ancestor if it's too small to have been drawn.
            for (var n = _selected; n != null; n = n.Parent)
            {
                int idx = _items.FindIndex(it => it.Node == n);
                if (idx < 0) continue;
                using var pen = new Pen(Color.Cyan, 3);
                var r = _items[idx].Rect;
                e.Graphics.DrawRectangle(pen, r.X + 1, r.Y + 1, Math.Max(1, r.Width - 3), Math.Max(1, r.Height - 3));
                break;
            }
        }
        if (_hover >= 0 && _hover < _items.Count)
        {
            var r = _items[_hover].Rect;
            using var pen = new Pen(Color.Yellow, 2);
            e.Graphics.DrawRectangle(pen, r.X + 1, r.Y + 1, Math.Max(1, r.Width - 2), Math.Max(1, r.Height - 2));
        }
    }

    private void EnsureBuffer()
    {
        int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height);
        if (_memDC != 0 && w == _w && h == _h) return;
        FreeBuffer();
        _w = w; _h = h;
        var bmi = new BITMAPINFOHEADER
        {
            biSize = sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, // negative = top-down rows
            biPlanes = 1, biBitCount = 32,
        };
        _memDC = CreateCompatibleDC(0);
        _dib = CreateDIBSection(_memDC, &bmi, 0, out _bits, 0, 0);
        _oldBitmap = SelectObject(_memDC, _dib);
        if (_hFont == 0) _hFont = _labelFont.ToHfont();
        SelectObject(_memDC, _hFont);
        SetBkMode(_memDC, TRANSPARENT);
    }

    private void FreeBuffer()
    {
        if (_memDC == 0) return;
        SelectObject(_memDC, _oldBitmap);
        DeleteObject(_dib);
        DeleteDC(_memDC);
        _memDC = _dib = _bits = 0;
    }

    protected override void Dispose(bool disposing)
    {
        FreeBuffer();
        if (_hFont != 0) { DeleteObject(_hFont); _hFont = 0; }
        if (disposing) _labelFont.Dispose();
        base.Dispose(disposing);
    }

    private void Render()
    {
        _dirty = false;
        EnsureBuffer();
        Pixels.Fill(BackColorArgb);
        _items.Clear();
        _labels.Clear();

        var root = _viewRoot;
        if (root != null)
        {
            _rootSize = Math.Max(1, Interlocked.Read(ref root.Size));
            _now = DateTime.UtcNow.ToFileTimeUtc();
            DrawDirectory(root, root.Size, new Rectangle(0, 0, _w, _h), 0);
        }

        if (root == null)
            DrawLabel(new Rectangle(0, 0, _w, _h), "Choose a drive or folder and press Scan.", 0x808080, DT_CENTER);
        foreach (var (rect, text, bg) in _labels)
            DrawLabel(rect, text, Luma(bg) > 140 ? 0x000000 : 0xFFFFFF, DT_LEFT);

        // Items were rebuilt; re-resolve what's under the cursor.
        var mouse = PointToClient(MousePosition);
        _hover = ClientRectangle.Contains(mouse) ? HitIndex(mouse) : -1;
    }

    private void DrawDirectory(Node dir, long size, Rectangle r, int depth)
    {
        _items.Add(new Item { Rect = r, Node = dir, Owner = dir.Parent ?? dir, Size = size });

        int color = _colorMode == ColorMode.Depth
            ? ColorFor(depth, size, isFile: false)
            : Hsv(220, 0.12, 0.30 + 0.06 * (depth % 3)); // neutral folders so file colours stand out
        FillRect(r, BorderColor);
        var inner = Rectangle.Inflate(r, -1, -1);
        if (inner.Width <= 0 || inner.Height <= 0) return;
        FillBevel(inner, color);

        if (inner.Width < 8 || inner.Height < 8) return;

        Rectangle content;
        if (inner.Height >= HeaderHeight + 14 && inner.Width >= 36)
        {
            _labels.Add((new Rectangle(inner.X + 3, inner.Y, inner.Width - 6, HeaderHeight),
                $"{dir.Name}  {Fmt.Size(size)}", color));
            content = new Rectangle(inner.X + 2, inner.Y + HeaderHeight, inner.Width - 4, inner.Height - HeaderHeight - 2);
        }
        else
        {
            content = Rectangle.Inflate(inner, -2, -2);
        }
        if (content.Width < 3 || content.Height < 3) return;

        // Darker backdrop: whatever stays uncovered is "many tiny items".
        FillRect(content, Scale(color, 0.45));

        var children = Volatile.Read(ref dir.Children);
        if (children == null || children.Length == 0) return;

        var entries = ArrayPool<Entry>.Shared.Rent(children.Length + 1);
        var rects = ArrayPool<Rectangle>.Shared.Rent(children.Length + 1);
        try
        {
            int n = 0;
            long total = 0, groupSize = 0;
            int groupCount = 0;
            foreach (var c in children)
            {
                long s = Interlocked.Read(ref c.Size); // snapshot: sizes can change during a live scan
                if (s <= 0) continue;
                if (!c.IsDirectory && !_showFiles) { groupSize += s; groupCount++; continue; }
                entries[n++] = new Entry { Node = c, Size = s };
                total += s;
            }
            if (groupCount > 0)
            {
                entries[n++] = new Entry { Node = null, Size = groupSize, Count = groupCount };
                total += groupSize;
            }
            if (n == 0) return;

            var span = entries.AsSpan(0, n);
            if (!ChildrenSorted || groupCount > 0)
                span.Sort(static (a, b) => b.Size.CompareTo(a.Size));

            int laid = Squarify(span, total, content, rects);
            for (int i = 0; i < laid; i++)
            {
                var rc = rects[i];
                if (rc.Width <= 0 || rc.Height <= 0) continue;
                ref var e = ref span[i];
                if (e.Node == null) DrawFileGroup(dir, e, rc, depth + 1);
                else if (e.Node.IsDirectory) DrawDirectory(e.Node, e.Size, rc, depth + 1);
                else DrawFile(e.Node, e.Size, rc, depth + 1);
            }
        }
        finally
        {
            ArrayPool<Entry>.Shared.Return(entries, clearArray: true);
            ArrayPool<Rectangle>.Shared.Return(rects);
        }
    }

    private void DrawFile(Node file, long size, Rectangle r, int depth)
    {
        _items.Add(new Item { Rect = r, Node = file, Owner = file.Parent!, Size = size });
        int color = _colorMode switch
        {
            ColorMode.FileType => FileCategories.ColorOf(FileCategories.Of(file.Name)).ToArgb(),
            ColorMode.Age => AgeColor(file.LastWrite),
            _ => ColorFor(depth, size, isFile: true),
        };
        FillRect(r, BorderColor);
        var inner = Rectangle.Inflate(r, -1, -1);
        if (inner.Width <= 0 || inner.Height <= 0) return;
        FillBevel(inner, color);
        if (inner.Width >= 44 && inner.Height >= 14)
            _labels.Add((LabelRect(inner), inner.Height >= 28 ? $"{file.Name} {Fmt.Size(size)}" : file.Name, color));
    }

    private void DrawFileGroup(Node owner, in Entry e, Rectangle r, int depth)
    {
        _items.Add(new Item { Rect = r, Node = null, Owner = owner, Size = e.Size, Count = e.Count });
        int color = _colorMode == ColorMode.Depth ? Scale(ColorFor(depth, e.Size, isFile: true), 0.8) : Hsv(0, 0, 0.45);
        FillRect(r, BorderColor);
        var inner = Rectangle.Inflate(r, -1, -1);
        if (inner.Width <= 0 || inner.Height <= 0) return;
        FillBevel(inner, color);
        if (inner.Width >= 44 && inner.Height >= 14)
            _labels.Add((LabelRect(inner), $"{e.Count:N0} files  {Fmt.Size(e.Size)}", color));
    }

    private static Rectangle LabelRect(Rectangle inner) =>
        new(inner.X + 3, inner.Y + 1, inner.Width - 6, Math.Min(inner.Height - 2, HeaderHeight));

    // ---------------------------------------------------------------- squarified layout (Bruls et al.)

    /// <summary>Lays out sorted-descending entries; returns how many got a rectangle (the rest are sub-pixel).</summary>
    private static int Squarify(Span<Entry> items, long total, Rectangle bounds, Rectangle[] output)
    {
        double x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height;
        double remaining = total;
        int start = 0, n = items.Length;

        while (start < n && w >= 1 && h >= 1 && remaining > 0)
        {
            double scale = w * h / remaining;
            if (items[start].Size * scale < 1.0) break; // everything left is sub-pixel

            double side = Math.Min(w, h);
            double rowSum = 0, worst = double.MaxValue;
            double maxArea = items[start].Size * scale;
            int end = start;
            while (end < n)
            {
                double area = items[end].Size * scale;
                double sum = rowSum + area;
                double s2 = side * side, sum2 = sum * sum;
                double wr = Math.Max(s2 * maxArea / sum2, sum2 / (s2 * area));
                if (end > start && wr > worst) break;
                worst = wr;
                rowSum = sum;
                end++;
            }

            long rowBytes = 0;
            if (w >= h)
            {
                // Column on the left.
                double colW = rowSum / h;
                double cy = y;
                for (int i = start; i < end; i++)
                {
                    double ih = items[i].Size * scale / colW;
                    output[i] = Snap(x, cy, colW, ih);
                    cy += ih;
                    rowBytes += items[i].Size;
                }
                x += colW; w -= colW;
            }
            else
            {
                // Row along the top.
                double rowH = rowSum / w;
                double cx = x;
                for (int i = start; i < end; i++)
                {
                    double iw = items[i].Size * scale / rowH;
                    output[i] = Snap(cx, y, iw, rowH);
                    cx += iw;
                    rowBytes += items[i].Size;
                }
                y += rowH; h -= rowH;
            }
            remaining -= rowBytes;
            start = end;
        }
        return start;
    }

    /// <summary>Rounds edges (not sizes) so adjacent rectangles share borders without gaps.</summary>
    private static Rectangle Snap(double x, double y, double w, double h)
    {
        int x0 = (int)Math.Round(x), y0 = (int)Math.Round(y);
        int x1 = (int)Math.Round(x + w), y1 = (int)Math.Round(y + h);
        return new Rectangle(x0, y0, x1 - x0, y1 - y0);
    }

    // ---------------------------------------------------------------- raster helpers

    private void DrawLabel(Rectangle r, string text, int rgb, uint align)
    {
        // COLORREF is 0x00BBGGRR.
        SetTextColor(_memDC, ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF));
        var rc = new RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
        fixed (char* p = text)
            DrawTextW(_memDC, p, text.Length, &rc, align | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS | DT_NOPREFIX);
    }

    private void FillRect(Rectangle r, int argb)
    {
        int x0 = Math.Max(0, r.X), y0 = Math.Max(0, r.Y);
        int x1 = Math.Min(_w, r.Right), y1 = Math.Min(_h, r.Bottom);
        int len = x1 - x0;
        if (len <= 0) return;
        var px = Pixels;
        for (int y = y0; y < y1; y++)
            px.Slice(y * _w + x0, len).Fill(argb);
    }

    /// <summary>Solid fill plus a 1px light top/left and dark bottom/right edge for depth.</summary>
    private void FillBevel(Rectangle r, int argb)
    {
        FillRect(r, argb);
        if (r.Width < 4 || r.Height < 4) return;
        int light = Scale(argb, 1.3), dark = Scale(argb, 0.7);
        FillRect(new Rectangle(r.X, r.Y, r.Width, 1), light);
        FillRect(new Rectangle(r.X, r.Y, 1, r.Height), light);
        FillRect(new Rectangle(r.X, r.Bottom - 1, r.Width, 1), dark);
        FillRect(new Rectangle(r.Right - 1, r.Y, 1, r.Height), dark);
    }

    private int ColorFor(int depth, long size, bool isFile)
    {
        double hue = DepthHues[depth % DepthHues.Length];
        // 0 → 1-millionth of the view, 1 → the whole view.
        double t = 1 + Math.Log10(Math.Max(1, size) / (double)_rootSize) / 6.0;
        t = Math.Clamp(t, 0, 1);
        double sat = isFile ? 0.35 : 0.7;
        double val = 0.30 + 0.62 * t;
        return Hsv(hue, sat, val);
    }

    /// <summary>Same buckets and colours as the Age tab: green = recent … red = 5+ years.</summary>
    private int AgeColor(long lastWrite) => Breakdown.AgeColors[Breakdown.AgeBucketOf(lastWrite, _now)].ToArgb();

    private static int Hsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (h / 60) switch
        {
            < 1 => (c, x, 0d),
            < 2 => (x, c, 0d),
            < 3 => (0d, c, x),
            < 4 => (0d, x, c),
            < 5 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Rgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    private static int Rgb(int r, int g, int b) =>
        unchecked((int)0xFF000000) | (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);

    private static int Scale(int argb, double f) =>
        Rgb((int)(((argb >> 16) & 0xFF) * f), (int)(((argb >> 8) & 0xFF) * f), (int)((argb & 0xFF) * f));

    private static int Luma(int argb) =>
        (((argb >> 16) & 0xFF) * 299 + ((argb >> 8) & 0xFF) * 587 + (argb & 0xFF) * 114) / 1000;

    // ---------------------------------------------------------------- input

    private int HitIndex(Point p)
    {
        // Children are appended after their parent, so the last hit is the deepest.
        for (int i = _items.Count - 1; i >= 0; i--)
            if (_items[i].Rect.Contains(p)) return i;
        return -1;
    }

    private void SetHover(int index)
    {
        if (index == _hover) return;
        InvalidateItem(_hover);
        _hover = index;
        InvalidateItem(_hover);
        HoverChanged?.Invoke(this, Hovered);
    }

    private void InvalidateItem(int index)
    {
        if (index >= 0 && index < _items.Count) Invalidate(Rectangle.Inflate(_items[index].Rect, 2, 2));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHover(HitIndex(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(-1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && HitTest(e.Location) is { } hit) ItemActivated?.Invoke(this, hit);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        WheelZoom?.Invoke(this, (HitTest(e.Location), e.Delta));
    }

    // ---------------------------------------------------------------- GDI interop

    private const uint SRCCOPY = 0x00CC0020;
    private const int TRANSPARENT = 1;
    private const uint DT_LEFT = 0, DT_CENTER = 1, DT_VCENTER = 4, DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800, DT_END_ELLIPSIS = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateDIBSection(nint hdc, BITMAPINFOHEADER* bmi, uint usage, out nint bits, nint section, uint offset);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint hdc, nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint hdc);
    [LibraryImport("gdi32.dll")] private static partial int SetBkMode(nint hdc, int mode);
    [LibraryImport("gdi32.dll")] private static partial int SetTextColor(nint hdc, int color);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [LibraryImport("user32.dll")] private static partial int DrawTextW(nint hdc, char* text, int count, RECT* rect, uint format);
}
