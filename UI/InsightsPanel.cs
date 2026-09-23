using SpaceMonger.Analysis;

namespace SpaceMonger.UI;

/// <summary>Right-hand panel: Cleanup (rules + ML), Types, Age, Duplicates, Changes.</summary>
public sealed class InsightsPanel : UserControl
{
    private readonly LabelStore _labels;

    private Node? _scanRoot;
    private Node? _view;
    private string? _currentSnapshot;
    private CancellationTokenSource? _cleanupCts, _breakdownCts, _dupCts;

    // Cleanup
    private readonly Label _cleanupSummary = BoldLabel();
    private readonly Label _modelInfo = GrayLabel();
    private readonly Button _reanalyze = new() { Text = "Re-analyze", AutoSize = true, Enabled = false };
    private readonly Button _deleteSelected = new() { Text = "Delete selected…", AutoSize = true, Enabled = false };
    private readonly ListView _cleanupList = NewList(("Size", 80), ("Folder", 260), ("What", 200), ("Safety", 90), ("Source", 70));

    // Types / Age
    private readonly Label _typesScope = GrayLabel();
    private readonly BarList _categoryBars = new() { Dock = DockStyle.Top };
    private readonly ListView _extList = NewList(("Extension", 90), ("Category", 90), ("Size", 80), ("Files", 80), ("Share", 60));
    private readonly Label _ageScope = GrayLabel();
    private readonly BarList _ageBars = new() { Dock = DockStyle.Top };

    // Duplicates
    private readonly ComboBox _dupMinSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly Button _dupFind = new() { Text = "Find duplicates", AutoSize = true, Enabled = false };
    private readonly Button _dupCancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly Label _dupStatus = GrayLabel();
    private readonly ListView _dupList = NewList(("File", 360), ("Size", 80), ("Modified", 110));

    // Changes
    private readonly ComboBox _snapshotBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly Button _compare = new() { Text = "Compare", AutoSize = true, Enabled = false };
    private readonly Label _changeSummary = BoldLabel();
    private readonly ListView _changeList = NewList(("Change", 70), ("Delta", 85), ("Before", 80), ("After", 80), ("Folder", 300));

    /// <summary>Single click on an item: highlight it in the map.</summary>
    public event Action<Node>? NodeSelected;
    /// <summary>Double click: zoom the map to it.</summary>
    public event Action<Node>? NodeActivated;
    /// <summary>User asked to delete these (the form confirms and performs it).</summary>
    public event Action<IReadOnlyList<Node>>? DeleteRequested;

    public InsightsPanel(LabelStore labels)
    {
        _labels = labels;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildCleanupTab());
        tabs.TabPages.Add(BuildTypesTab());
        tabs.TabPages.Add(BuildAgeTab());
        tabs.TabPages.Add(BuildDuplicatesTab());
        tabs.TabPages.Add(BuildChangesTab());
        Controls.Add(tabs);
    }

    // ================================================================ public API

    /// <summary>A scan started: drop everything tied to the old tree.</summary>
    public void Reset()
    {
        _cleanupCts?.Cancel(); _breakdownCts?.Cancel(); _dupCts?.Cancel();
        _scanRoot = _view = null;
        _cleanupList.Items.Clear(); _extList.Items.Clear(); _dupList.Items.Clear(); _changeList.Items.Clear(); _dupList.Groups.Clear();
        _categoryBars.Bars = []; _ageBars.Bars = [];
        _cleanupSummary.Text = "Scanning…"; _modelInfo.Text = ""; _dupStatus.Text = ""; _changeSummary.Text = "";
        _reanalyze.Enabled = _dupFind.Enabled = _compare.Enabled = false;
    }

    /// <summary>A scan finished. <paramref name="complete"/> is false for a cancelled (partial) scan.</summary>
    public void SetScan(Node root, bool complete)
    {
        _scanRoot = root;
        _reanalyze.Enabled = _dupFind.Enabled = true;
        SetView(root);
        RunCleanup();
        _ = SaveSnapshotAndListAsync(root, complete);
    }

    public void SetView(Node view)
    {
        _view = view;
        RunBreakdown();
    }

    /// <summary>A node was deleted from the tree: drop stale rows and refresh numbers.</summary>
    public void OnNodeRemoved(Node removed)
    {
        bool Gone(Node n) => n == removed || removed.IsAncestorOf(n);
        foreach (ListViewItem it in _cleanupList.Items.Cast<ListViewItem>().ToList())
            if (it.Tag is CleanupItem ci && Gone(ci.Node)) _cleanupList.Items.Remove(it);
        foreach (ListViewItem it in _dupList.Items.Cast<ListViewItem>().ToList())
            if (it.Tag is Node n && Gone(n)) _dupList.Items.Remove(it);
        foreach (var g in _dupList.Groups.Cast<ListViewGroup>().ToList())
            if (g.Items.Count < 2) { foreach (ListViewItem it in g.Items.Cast<ListViewItem>().ToList()) _dupList.Items.Remove(it); _dupList.Groups.Remove(g); }
        UpdateCleanupSummary();
        if (_view != null) RunBreakdown();
    }

    // ================================================================ Cleanup

    private TabPage BuildCleanupTab()
    {
        var page = new TabPage("Cleanup");
        var buttons = Flow(_reanalyze, _deleteSelected);
        var help = GrayLabel();
        help.Text = "Rule = known pattern. Model = ML classifier trained on this disk; right-click to teach it (junk / keep).";
        var header = Stack(_cleanupSummary, _modelInfo, buttons, help);

        _reanalyze.Click += (_, _) => RunCleanup();
        _deleteSelected.Click += (_, _) => RequestDelete(_cleanupList);
        _cleanupList.SelectedIndexChanged += (_, _) =>
        {
            _deleteSelected.Enabled = _cleanupList.SelectedItems.Count > 0;
            if (_cleanupList.SelectedItems.Count == 1 && _cleanupList.SelectedItems[0].Tag is CleanupItem ci) NodeSelected?.Invoke(ci.Node);
        };
        _cleanupList.DoubleClick += (_, _) => { if (SelectedNodes(_cleanupList).FirstOrDefault() is { } n) NodeActivated?.Invoke(n); };

        var menu = new ContextMenuStrip();
        menu.Opening += (_, e) =>
        {
            menu.Items.Clear();
            var nodes = SelectedNodes(_cleanupList);
            if (nodes.Count == 0) { e.Cancel = true; return; }
            menu.Items.Add("Show in map", null, (_, _) => NodeActivated?.Invoke(nodes[0]));
            menu.Items.Add("Show in Explorer", null, (_, _) => Shell.Reveal(nodes[0].FullPath));
            menu.Items.Add("Delete to Recycle Bin…", null, (_, _) => DeleteRequested?.Invoke(nodes));
            menu.Items.Add(new ToolStripSeparator());
            var junk = new ToolStripMenuItem("Teach: this is junk");
            foreach (var c in Enum.GetValues<FolderClass>().Where(CleanupRules.IsReclaimable))
                junk.DropDownItems.Add(CleanupAdvisor.Describe(c), null, (_, _) => Teach(nodes, c));
            menu.Items.Add(junk);
            menu.Items.Add("Teach: keep this (never suggest)", null, (_, _) => Teach(nodes, FolderClass.UserKeep));
            menu.Items.Add("Forget my label", null, (_, _) => Teach(nodes, null));
        };
        _cleanupList.ContextMenuStrip = menu;

        page.Controls.Add(_cleanupList);
        page.Controls.Add(header);
        return page;
    }

    private void Teach(IReadOnlyList<Node> nodes, FolderClass? label)
    {
        foreach (var n in nodes) _labels.Set(n.FullPath, label);
        RunCleanup();
    }

    private async void RunCleanup()
    {
        var root = _scanRoot;
        if (root == null) return;
        _cleanupCts?.Cancel();
        var cts = _cleanupCts = new CancellationTokenSource();
        _cleanupSummary.Text = "Analyzing (rules + training model)…";
        _modelInfo.Text = "";
        _reanalyze.Enabled = false;
        try
        {
            var result = await Task.Run(() => CleanupAdvisor.Analyze(root, _labels, cts.Token));
            if (cts.IsCancellationRequested || root != _scanRoot) return;
            _cleanupList.BeginUpdate();
            _cleanupList.Items.Clear();
            foreach (var item in result.Items)
            {
                var lvi = new ListViewItem([Fmt.Size(item.Node.Size), item.Node.FullPath, item.Reason, item.Safety.ToString(), item.Source.ToString()])
                {
                    Tag = item,
                    ForeColor = SafetyColor(item.Safety),
                    ToolTipText = item.Node.FullPath,
                };
                _cleanupList.Items.Add(lvi);
            }
            _cleanupList.EndUpdate();
            _modelInfo.Text = $"{result.ModelInfo}. Analysis took {result.Elapsed.TotalSeconds:0.0}s. {_labels.Count} folder label(s) from you.";
            UpdateCleanupSummary();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _cleanupSummary.Text = "Analysis failed: " + ex.Message; }
        finally { if (cts == _cleanupCts) _reanalyze.Enabled = _scanRoot != null; }
    }

    private void UpdateCleanupSummary()
    {
        var items = _cleanupList.Items.Cast<ListViewItem>().Select(i => (CleanupItem)i.Tag!).ToList();
        if (_scanRoot == null) return;
        long total = items.Sum(i => i.Node.Size);
        long safe = items.Where(i => i.Safety is Safety.Safe or Safety.Regenerable).Sum(i => i.Node.Size);
        _cleanupSummary.Text = $"Potentially reclaimable: {Fmt.Size(total)} in {items.Count} folders  (safe/regenerable: {Fmt.Size(safe)})";
    }

    private static Color SafetyColor(Safety s) => s switch
    {
        Safety.Safe => Color.SeaGreen,
        Safety.Regenerable => Color.SteelBlue,
        Safety.Review => Color.DarkOrange,
        _ => Color.Firebrick,
    };

    // ================================================================ Types & Age

    private TabPage BuildTypesTab()
    {
        var page = new TabPage("Types");
        var scroll = new Panel { Dock = DockStyle.Top, Height = 230, AutoScroll = true };
        scroll.Controls.Add(_categoryBars);
        page.Controls.Add(_extList);
        page.Controls.Add(scroll);
        page.Controls.Add(Stack(_typesScope));
        return page;
    }

    private TabPage BuildAgeTab()
    {
        var page = new TabPage("Age");
        var note = GrayLabel();
        note.Text = "By last-modified date. Tip: switch the map colour to “Age” to see where old data lives.";
        page.Controls.Add(_ageBars);
        page.Controls.Add(Stack(_ageScope, note));
        return page;
    }

    private async void RunBreakdown()
    {
        var view = _view;
        if (view == null) return;
        _breakdownCts?.Cancel();
        var cts = _breakdownCts = new CancellationTokenSource();
        try
        {
            var b = await Task.Run(() => Breakdown.Compute(view, cts.Token));
            if (cts.IsCancellationRequested) return;
            long total = Math.Max(1, b.TotalBytes);

            _typesScope.Text = _ageScope.Text = $"Scope: {view.FullPath}  ({Fmt.Size(b.TotalBytes)})";
            _categoryBars.Bars = Enum.GetValues<FileCategory>()
                .Select(c => (c, bytes: b.CategoryBytes[(int)c], count: b.CategoryCounts[(int)c]))
                .Where(x => x.count > 0)
                .OrderByDescending(x => x.bytes)
                .Select(x => new BarList.Bar(x.c.ToString(), $"{Fmt.Size(x.bytes)}  ·  {x.count:N0} files  ·  {100.0 * x.bytes / total:0.#}%",
                    (double)x.bytes / total, FileCategories.ColorOf(x.c)))
                .ToList();

            _extList.BeginUpdate();
            _extList.Items.Clear();
            foreach (var e in b.Extensions.Take(300))
                _extList.Items.Add(new ListViewItem([e.Extension, e.Category.ToString(), Fmt.Size(e.Bytes), e.Count.ToString("N0"), $"{100.0 * e.Bytes / total:0.0}%"])
                { ForeColor = FileCategories.ColorOf(e.Category) == FileCategories.ColorOf(FileCategory.Other) ? SystemColors.WindowText : Darken(FileCategories.ColorOf(e.Category)) });
            _extList.EndUpdate();

            var ageColors = Breakdown.AgeColors;
            _ageBars.Bars = b.Ages.Select((a, i) => new BarList.Bar(a.Label, $"{Fmt.Size(a.Bytes)}  ·  {a.Count:N0} files  ·  {100.0 * a.Bytes / total:0.#}%",
                (double)a.Bytes / total, ageColors[Math.Min(i, ageColors.Length - 1)])).ToList();
        }
        catch (OperationCanceledException) { }
    }

    private static Color Darken(Color c) => Color.FromArgb(c.R * 7 / 10, c.G * 7 / 10, c.B * 7 / 10);

    // ================================================================ Duplicates

    private TabPage BuildDuplicatesTab()
    {
        var page = new TabPage("Duplicates");
        foreach (var s in new[] { "100 KB", "1 MB", "10 MB", "100 MB" }) _dupMinSize.Items.Add(s);
        _dupMinSize.SelectedIndex = 1;
        var minLabel = new Label { Text = "Min size:", AutoSize = true, Margin = new Padding(3, 7, 0, 0) };
        _dupList.ShowGroups = true;

        _dupFind.Click += (_, _) => RunDuplicates();
        _dupCancel.Click += (_, _) => _dupCts?.Cancel();
        _dupList.SelectedIndexChanged += (_, _) => { if (SelectedNodes(_dupList).FirstOrDefault() is { } n) NodeSelected?.Invoke(n); };
        _dupList.DoubleClick += (_, _) => { if (SelectedNodes(_dupList).FirstOrDefault() is { } n) NodeActivated?.Invoke(n.Parent ?? n); };

        var menu = new ContextMenuStrip();
        menu.Opening += (_, e) =>
        {
            menu.Items.Clear();
            var nodes = SelectedNodes(_dupList);
            if (nodes.Count == 0) { e.Cancel = true; return; }
            menu.Items.Add("Show in map", null, (_, _) => NodeActivated?.Invoke(nodes[0].Parent ?? nodes[0]));
            menu.Items.Add("Show in Explorer", null, (_, _) => Shell.Reveal(nodes[0].FullPath));
            menu.Items.Add("Select all but the first in each group", null, (_, _) => SelectExtraCopies());
            menu.Items.Add("Delete to Recycle Bin…", null, (_, _) => DeleteRequested?.Invoke(nodes));
        };
        _dupList.ContextMenuStrip = menu;

        page.Controls.Add(_dupList);
        page.Controls.Add(Stack(Flow(minLabel, _dupMinSize, _dupFind, _dupCancel), _dupStatus));
        return page;
    }

    private void SelectExtraCopies()
    {
        _dupList.BeginUpdate();
        foreach (ListViewItem it in _dupList.Items) it.Selected = false;
        foreach (ListViewGroup g in _dupList.Groups)
            for (int i = 1; i < g.Items.Count; i++) g.Items[i].Selected = true;
        _dupList.EndUpdate();
        _dupList.Focus();
    }

    private async void RunDuplicates()
    {
        var root = _scanRoot;
        if (root == null) return;
        long minSize = _dupMinSize.SelectedIndex switch { 0 => 100L << 10, 1 => 1L << 20, 2 => 10L << 20, _ => 100L << 20 };
        _dupCts?.Cancel();
        var cts = _dupCts = new CancellationTokenSource();
        _dupFind.Enabled = false;
        _dupCancel.Enabled = true;
        _dupList.Items.Clear();
        _dupList.Groups.Clear();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var progress = new Progress<string>(s => { if (!cts.IsCancellationRequested) _dupStatus.Text = s; });
        try
        {
            var groups = await Task.Run(() => DuplicateFinder.FindAsync(root, minSize, progress, cts.Token));
            _dupList.BeginUpdate();
            foreach (var g in groups.Take(300))
            {
                var lvg = new ListViewGroup($"{g.Files.Count} × {Fmt.Size(g.Size)}  —  {Fmt.Size(g.Wasted)} wasted");
                _dupList.Groups.Add(lvg);
                foreach (var f in g.Files)
                    _dupList.Items.Add(new ListViewItem([f.FullPath, Fmt.Size(f.Size), f.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd")], lvg) { Tag = f });
            }
            _dupList.EndUpdate();
            _dupStatus.Text = $"{groups.Count:N0} duplicate sets, {Fmt.Size(groups.Sum(g => g.Wasted))} wasted " +
                              $"({sw.Elapsed.TotalSeconds:0.0}s){(groups.Count > 300 ? " — showing the top 300" : "")}";
        }
        catch (OperationCanceledException) { _dupStatus.Text = "Cancelled"; }
        catch (Exception ex) { _dupStatus.Text = "Failed: " + ex.Message; }
        finally
        {
            _dupFind.Enabled = _scanRoot != null;
            _dupCancel.Enabled = false;
        }
    }

    // ================================================================ Changes

    private TabPage BuildChangesTab()
    {
        var page = new TabPage("Changes");
        var label = new Label { Text = "Compare with:", AutoSize = true, Margin = new Padding(3, 7, 0, 0) };
        var note = GrayLabel();
        note.Text = "A snapshot of folder sizes is saved after every complete scan. Lists the folders that explain each change.";
        _compare.Click += (_, _) => RunCompare();
        _snapshotBox.SelectedIndexChanged += (_, _) => _compare.Enabled = _snapshotBox.SelectedItem != null && _scanRoot != null;
        _changeList.SelectedIndexChanged += (_, _) => { if (SelectedNodes(_changeList).FirstOrDefault() is { } n) NodeSelected?.Invoke(n); };
        _changeList.DoubleClick += (_, _) => { if (SelectedNodes(_changeList).FirstOrDefault() is { } n) NodeActivated?.Invoke(n); };

        page.Controls.Add(_changeList);
        page.Controls.Add(Stack(Flow(label, _snapshotBox, _compare), _changeSummary, note));
        return page;
    }

    private async Task SaveSnapshotAndListAsync(Node root, bool complete)
    {
        string rootPath = root.FullPath;
        try
        {
            if (complete) await Task.Run(() => Snapshots.Save(root));
            var list = await Task.Run(() => Snapshots.List(rootPath));
            if (root != _scanRoot) return;
            // The newest one is the scan we just saved; offer the older ones.
            _currentSnapshot = complete ? list.FirstOrDefault()?.FilePath : null;
            _snapshotBox.Items.Clear();
            foreach (var s in list.Where(s => s.FilePath != _currentSnapshot)) _snapshotBox.Items.Add(s);
            if (_snapshotBox.Items.Count > 0)
            {
                _snapshotBox.SelectedIndex = 0;
                RunCompare();
            }
            else _changeSummary.Text = "No earlier snapshot of this folder yet — scan again later to see what changed.";
        }
        catch (Exception ex) { _changeSummary.Text = "Snapshot error: " + ex.Message; }
    }

    private async void RunCompare()
    {
        var root = _scanRoot;
        if (root == null || _snapshotBox.SelectedItem is not SnapshotInfo info) return;
        _changeSummary.Text = "Comparing…";
        try
        {
            long threshold = Math.Max(1L << 20, root.Size / 2000);
            var (before, changes) = await Task.Run(() =>
            {
                var snap = Snapshots.Load(info.FilePath);
                return (snap, Snapshots.Diff(root, snap, threshold));
            });
            if (root != _scanRoot) return;
            long delta = root.Size - before.Size;
            _changeSummary.Text = $"{(delta >= 0 ? "+" : "−")}{Fmt.Size(Math.Abs(delta))} since {info.TakenUtc.ToLocalTime():g}  " +
                                  $"({Fmt.Size(before.Size)} → {Fmt.Size(root.Size)})";
            _changeList.BeginUpdate();
            _changeList.Items.Clear();
            foreach (var c in changes.Take(500))
            {
                string sign = c.Delta >= 0 ? "+" : "−";
                _changeList.Items.Add(new ListViewItem([c.Kind.ToString(), sign + Fmt.Size(Math.Abs(c.Delta)), Fmt.Size(c.Before), Fmt.Size(c.After), c.Path])
                {
                    Tag = c.Current,
                    ForeColor = c.Delta >= 0 ? Color.Firebrick : Color.SeaGreen,
                });
            }
            _changeList.EndUpdate();
            if (changes.Count == 0) _changeSummary.Text += " — no folder changed by more than " + Fmt.Size(threshold);
        }
        catch (Exception ex) { _changeSummary.Text = "Compare failed: " + ex.Message; }
    }

    // ================================================================ helpers

    private void RequestDelete(ListView list)
    {
        var nodes = SelectedNodes(list);
        if (nodes.Count > 0) DeleteRequested?.Invoke(nodes);
    }

    private static List<Node> SelectedNodes(ListView list) =>
        list.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag switch { CleanupItem ci => ci.Node, Node n => n, _ => null })
            .Where(n => n != null).Cast<Node>().ToList();

    private static ListView NewList(params (string text, int width)[] columns)
    {
        var lv = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
            ShowItemToolTips = true, BorderStyle = BorderStyle.None,
        };
        foreach (var (text, width) in columns) lv.Columns.Add(text, width);
        typeof(ListView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(lv, true);
        return lv;
    }

    private static Label BoldLabel() => new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold), Margin = new Padding(3, 6, 3, 2) };
    private static Label GrayLabel() => new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 2), MaximumSize = new Size(560, 0) };

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Top, Margin = Padding.Empty };
        f.Controls.AddRange(controls);
        return f;
    }

    private static Control Stack(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Top, Padding = new Padding(4) };
        f.Controls.AddRange(controls);
        return f;
    }
}

internal static class Shell
{
    public static void Reveal(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
}
