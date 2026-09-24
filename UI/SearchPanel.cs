using SpaceMonger.Analysis;

namespace SpaceMonger.UI;

/// <summary>Search results (virtual list, biggest first) shown in place of the folder tree while searching.</summary>
public sealed class SearchPanel : UserControl
{
    private readonly Label _summary = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold), Margin = new Padding(3, 4, 3, 0) };
    private readonly CheckBox _scopeToView = new() { Text = "Only in current view", AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
    private readonly Label _help = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 4), MaximumSize = new Size(400, 0) };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
        VirtualMode = true, BorderStyle = BorderStyle.None, ShowItemToolTips = true,
    };
    private List<Node> _results = [];

    public event Action<Node>? NodeSelected;
    public event Action<Node>? NodeActivated;
    /// <summary>The "only in current view" option changed.</summary>
    public event Action? ScopeChanged;

    public bool ScopeToView => _scopeToView.Checked;

    public Node? SelectedResult => _list.SelectedIndices.Count > 0 && _list.SelectedIndices[0] < _results.Count ? _results[_list.SelectedIndices[0]] : null;

    public ContextMenuStrip? ResultMenu { get => _list.ContextMenuStrip; set => _list.ContextMenuStrip = value; }

    public SearchPanel()
    {
        _list.Columns.Add("Name", 170);
        _list.Columns.Add("Size", 70, HorizontalAlignment.Right);
        _list.Columns.Add("Folder", 260);
        typeof(ListView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(_list, true);
        _list.RetrieveVirtualItem += (_, e) =>
        {
            var n = _results[e.ItemIndex];
            e.Item = new ListViewItem([n.IsDirectory ? n.Name + "\\" : n.Name, Fmt.Size(n.Size), n.Parent?.FullPath ?? ""])
            {
                ToolTipText = n.FullPath,
                ForeColor = n.IsDirectory ? Color.FromArgb(0x1E, 0x5A, 0x9C) : SystemColors.WindowText,
            };
        };
        _list.SelectedIndexChanged += (_, _) => { if (SelectedResult is { } n) NodeSelected?.Invoke(n); };
        _list.DoubleClick += (_, _) => { if (SelectedResult is { } n) NodeActivated?.Invoke(n); };
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && SelectedResult is { } n) { e.Handled = true; NodeActivated?.Invoke(n); } };
        _scopeToView.CheckedChanged += (_, _) => ScopeChanged?.Invoke();

        _help.Text = "name words, *.mp4, ext:iso,zip, type:video, size:>1gb, age:>2y, is:folder — combine freely";
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(4) };
        header.Controls.AddRange([_summary, _scopeToView, _help]);
        Controls.Add(_list);
        Controls.Add(header);
    }

    public void FocusResults()
    {
        _list.Focus();
        if (_results.Count > 0 && _list.SelectedIndices.Count == 0) _list.SelectedIndices.Add(0);
    }

    public void ShowMessage(string text)
    {
        _summary.Text = text;
        _summary.ForeColor = SystemColors.ControlText;
        _results = [];
        _list.VirtualListSize = 0;
    }

    public void ShowError(string text)
    {
        ShowMessage(text);
        _summary.ForeColor = Color.Firebrick;
    }

    public void ShowResults(SearchResult r, int max)
    {
        _results = r.Top;
        _list.SelectedIndices.Clear();
        _list.VirtualListSize = _results.Count;
        _list.Invalidate();
        _summary.ForeColor = SystemColors.ControlText;
        string shown = r.Count > r.Top.Count ? $" — showing the {max:N0} largest" : "";
        _summary.Text = r.Count == 0
            ? $"No matches ({r.Elapsed.TotalMilliseconds:0} ms)"
            : $"{r.Count:N0} matches · {Fmt.Size(r.FileBytes)} in files · {r.Elapsed.TotalMilliseconds:0} ms{shown}";
    }

    /// <summary>Drops results that were deleted from the tree.</summary>
    public void OnNodeRemoved(Node removed)
    {
        int before = _results.Count;
        _results = _results.Where(n => n != removed && !removed.IsAncestorOf(n)).ToList();
        if (_results.Count != before) { _list.VirtualListSize = _results.Count; _list.Invalidate(); }
    }
}
