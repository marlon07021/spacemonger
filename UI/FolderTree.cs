using System.Runtime.InteropServices;

namespace SpaceMonger.UI;

/// <summary>
/// Explorer-style folder/file tree over the scanned <see cref="Node"/> tree. Children are created
/// lazily on expand (capped per folder) so it stays instant on multi-million-file scans. Rows are
/// owner-drawn with the size and a bar showing the share of the parent folder, largest first.
/// </summary>
public sealed partial class FolderTree : TreeView
{
    private const int MaxChildren = 1000;
    private const int SizeColumn = 72, BarColumn = 46;

    private static readonly object Placeholder = new();
    private readonly Dictionary<Node, TreeNode> _nodes = new();
    private readonly ImageList _icons = new() { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
    private bool _suppressEvents;
    private Node? _root;

    /// <summary>User selected an item (click / keyboard).</summary>
    public event Action<Node>? NodeSelected;
    /// <summary>User double-clicked or pressed Enter on an item.</summary>
    public event Action<Node>? NodeActivated;

    public FolderTree()
    {
        DrawMode = TreeViewDrawMode.OwnerDrawText;
        ShowLines = false;
        FullRowSelect = true;
        HideSelection = false;
        ItemHeight = 20;
        BorderStyle = BorderStyle.None;
        ImageList = _icons;
        Font = new Font("Segoe UI", 9f);
        ShowNodeToolTips = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= TVS_NOHSCROLL; // rows are ellipsized to the width; never scroll sideways
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetWindowTheme(Handle, "Explorer", null);                                  // modern chevrons
        SendMessage(Handle, TVM_SETEXTENDEDSTYLE, TVS_EX_DOUBLEBUFFER, TVS_EX_DOUBLEBUFFER); // no flicker
    }

    // ================================================================ public API

    public void SetRoot(Node? root)
    {
        _root = root;
        _nodes.Clear();
        BeginUpdate();
        Nodes.Clear();
        if (root != null)
        {
            var tn = Create(root);
            Nodes.Add(tn);
            tn.Expand();
        }
        EndUpdate();
    }

    /// <summary>Expands down to <paramref name="node"/> and selects it, without raising <see cref="NodeSelected"/>.</summary>
    public void Reveal(Node node)
    {
        if (_root == null) return;
        var chain = new List<Node>();
        for (var n = node; n != null; n = n.Parent)
        {
            chain.Add(n);
            if (n == _root) break;
        }
        if (chain[^1] != _root) return; // not part of this tree

        _suppressEvents = true;
        try
        {
            TreeNode? current = null;
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                if (!_nodes.TryGetValue(chain[i], out var tn)) break; // beyond the per-folder cap: stop at the nearest shown ancestor
                current = tn;
                if (i > 0) tn.Expand();
            }
            if (current != null)
            {
                SelectedNode = current;
                current.EnsureVisible();
            }
        }
        finally { _suppressEvents = false; }
    }

    /// <summary>Drops a deleted node's row and repaints (parent sizes are read live from the model).</summary>
    public void OnNodeRemoved(Node removed)
    {
        if (_nodes.TryGetValue(removed, out var tn))
        {
            tn.Remove();
            foreach (var n in _nodes.Keys.Where(k => k == removed || removed.IsAncestorOf(k)).ToList()) _nodes.Remove(n);
        }
        Invalidate();
    }

    // ================================================================ population

    private TreeNode Create(Node n)
    {
        var tn = new TreeNode(n.Name) { Tag = n, ToolTipText = n.FullPath };
        tn.ImageKey = tn.SelectedImageKey = IconFor(n, open: false);
        if (n.IsDirectory && Volatile.Read(ref n.Children) is { Length: > 0 })
            tn.Nodes.Add(new TreeNode { Tag = Placeholder });
        _nodes[n] = tn;
        return tn;
    }

    private void Populate(TreeNode tn)
    {
        if (tn.Tag is not Node n || tn.Nodes.Count != 1 || tn.Nodes[0].Tag != Placeholder) return;
        var children = Volatile.Read(ref n.Children) ?? [];
        // Sizes can still be changing during a live scan: snapshot before sorting.
        var ordered = children.Select(c => (c, size: Interlocked.Read(ref c.Size)))
            .OrderByDescending(x => x.size).Select(x => x.c).ToArray();

        BeginUpdate();
        tn.Nodes.Clear();
        var items = new List<TreeNode>(Math.Min(ordered.Length, MaxChildren + 1));
        foreach (var c in ordered.Take(MaxChildren)) items.Add(Create(c));
        if (ordered.Length > MaxChildren)
        {
            var rest = ordered.Skip(MaxChildren).ToList();
            items.Add(new TreeNode($"… {rest.Count:N0} more items ({Fmt.Size(rest.Sum(r => r.Size))})") { ForeColor = SystemColors.GrayText });
        }
        tn.Nodes.AddRange(items.ToArray());
        EndUpdate();
    }

    protected override void OnBeforeExpand(TreeViewCancelEventArgs e)
    {
        Populate(e.Node!);
        base.OnBeforeExpand(e);
    }

    protected override void OnAfterExpand(TreeViewEventArgs e)
    {
        if (e.Node!.Tag is Node { IsDirectory: true } n) e.Node.ImageKey = e.Node.SelectedImageKey = IconFor(n, open: true);
        base.OnAfterExpand(e);
    }

    protected override void OnAfterCollapse(TreeViewEventArgs e)
    {
        if (e.Node!.Tag is Node { IsDirectory: true } n) e.Node.ImageKey = e.Node.SelectedImageKey = IconFor(n, open: false);
        base.OnAfterCollapse(e);
    }

    // ================================================================ input

    protected override void OnAfterSelect(TreeViewEventArgs e)
    {
        base.OnAfterSelect(e);
        if (!_suppressEvents && e.Node?.Tag is Node n) NodeSelected?.Invoke(n);
    }

    protected override void OnNodeMouseClick(TreeNodeMouseClickEventArgs e)
    {
        // Right-click selects first, so the context menu acts on the clicked row.
        if (e.Button == MouseButtons.Right) SelectedNode = e.Node;
        base.OnNodeMouseClick(e);
    }

    protected override void OnNodeMouseDoubleClick(TreeNodeMouseClickEventArgs e)
    {
        base.OnNodeMouseDoubleClick(e);
        if (e.Node.Tag is Node n) NodeActivated?.Invoke(n);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Enter && SelectedNode?.Tag is Node n)
        {
            e.Handled = e.SuppressKeyPress = true;
            NodeActivated?.Invoke(n);
        }
    }

    /// <summary>The model node of the selected row (null for the "more items" row).</summary>
    public Node? SelectedModelNode => SelectedNode?.Tag as Node;

    // ================================================================ drawing

    protected override void OnDrawNode(DrawTreeNodeEventArgs e)
    {
        var tn = e.Node!;
        var g = e.Graphics;
        int right = ClientSize.Width;
        var row = new Rectangle(e.Bounds.X, e.Bounds.Y, Math.Max(0, right - e.Bounds.X), e.Bounds.Height);
        bool selected = (e.State & TreeNodeStates.Selected) != 0;
        bool focused = selected && Focused;

        using (var bg = new SolidBrush(focused ? SystemColors.Highlight : selected ? Color.FromArgb(0xDD, 0xE8, 0xF5) : BackColor))
            g.FillRectangle(bg, row);
        var fore = focused ? SystemColors.HighlightText : tn.ForeColor.IsEmpty ? ForeColor : tn.ForeColor;

        if (tn.Tag is not Node n)
        {
            TextRenderer.DrawText(g, tn.Text, Font, row, fore, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            return;
        }

        int columns = SizeColumn + BarColumn + 8;
        var nameRect = new Rectangle(e.Bounds.X, e.Bounds.Y, Math.Max(10, right - columns - e.Bounds.X), e.Bounds.Height);
        TextRenderer.DrawText(g, n.Name, Font, nameRect, fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        // Share-of-parent bar.
        long size = Interlocked.Read(ref n.Size);
        long parentSize = n.Parent != null ? Math.Max(1, Interlocked.Read(ref n.Parent.Size)) : size;
        double share = parentSize > 0 ? Math.Clamp((double)size / parentSize, 0, 1) : 0;
        var bar = new Rectangle(right - columns + 2, e.Bounds.Y + 6, BarColumn, e.Bounds.Height - 12);
        using (var track = new SolidBrush(focused ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(0xE6, 0xE6, 0xE6))) g.FillRectangle(track, bar);
        using (var fill = new SolidBrush(focused ? Color.White : BarColor(share)))
            g.FillRectangle(fill, bar.X, bar.Y, Math.Max(share > 0 ? 1 : 0, (int)(bar.Width * share)), bar.Height);

        var sizeRect = new Rectangle(right - SizeColumn - 4, e.Bounds.Y, SizeColumn, e.Bounds.Height);
        TextRenderer.DrawText(g, Fmt.Size(size), Font, sizeRect, focused ? fore : SystemColors.GrayText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
    }

    /// <summary>Blue for small shares, warming to orange/red as a child dominates its parent.</summary>
    private static Color BarColor(double share) => share switch
    {
        >= 0.5 => Color.FromArgb(0xE0, 0x62, 0x3A),
        >= 0.2 => Color.FromArgb(0xE8, 0x9A, 0x30),
        >= 0.05 => Color.FromArgb(0x4C, 0x9A, 0xD8),
        _ => Color.FromArgb(0x9C, 0xB8, 0xD0),
    };

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate(); // right-aligned columns move with the width
    }

    // ================================================================ shell icons

    private string IconFor(Node n, bool open)
    {
        string key = n.IsDirectory ? (n.Parent == null ? "drive" : open ? "folder-open" : "folder") : "ext:" + Path.GetExtension(n.Name).ToLowerInvariant();
        if (!_icons.Images.ContainsKey(key))
        {
            // SHGFI_USEFILEATTRIBUTES: look up by name/attributes only, never touch the disk (fast, cached per extension).
            string probe = n.IsDirectory ? (n.Parent == null ? n.Name : "folder") : "x" + Path.GetExtension(n.Name);
            uint attrs = n.IsDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            uint flags = SHGFI_ICON | SHGFI_SMALLICON | (n.Parent == null && n.IsDirectory ? 0 : SHGFI_USEFILEATTRIBUTES) | (open ? SHGFI_OPENICON : 0);
            var info = new SHFILEINFOW();
            if (SHGetFileInfoW(probe, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), flags) != 0 && info.hIcon != 0)
            {
                using (var icon = Icon.FromHandle(info.hIcon)) _icons.Images.Add(key, icon.ToBitmap());
                DestroyIcon(info.hIcon);
            }
            else _icons.Images.Add(key, SystemIcons.WinLogo.ToBitmap());
        }
        return key;
    }

    private const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_OPENICON = 0x2, SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
    private const int TVM_SETEXTENDEDSTYLE = 0x1100 + 44;
    private const int TVS_EX_DOUBLEBUFFER = 0x0004;
    private const int TVS_NOHSCROLL = 0x8000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfoW(string path, uint attrs, ref SHFILEINFOW info, uint size, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTheme(nint hwnd, string app, string? idList);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);
}
