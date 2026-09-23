using System.Diagnostics;
using Microsoft.VisualBasic.FileIO;
using SpaceMonger.Analysis;
using SpaceMonger.Scanning;
using SpaceMonger.UI;

namespace SpaceMonger;

public partial class Form1 : Form
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly string? _startupPath;

    private readonly ToolStripComboBox _pathBox = new() { AutoSize = false, Width = 320, FlatStyle = FlatStyle.System };
    private readonly ToolStripButton _scanButton = new("Scan") { ToolTipText = "Scan (F5)" };
    private readonly ToolStripButton _stopButton = new("Stop") { ToolTipText = "Stop scanning (Esc)", Enabled = false };
    private readonly ToolStripButton _upButton = new("\u2191 Up") { ToolTipText = "Zoom out one level (Backspace)" };
    private readonly ToolStripButton _topButton = new("\u21C8 Top") { ToolTipText = "Zoom out to the scan root" };
    private readonly ToolStrip _crumbs = new() { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
    private readonly TreemapView _map = new() { Dock = DockStyle.Fill };
    private readonly ToolStripStatusLabel _hoverLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripProgressBar _progressBar = new() { Visible = false, Width = 160 };
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 250 };
    private readonly ToolStripComboBox _colorBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 90, ToolTipText = "Map colouring" };
    private readonly ToolStripButton _insightsButton = new("Insights") { CheckOnClick = true, ToolTipText = "Show/hide the insights panel" };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
    private readonly InsightsPanel _insights = new(LabelStore.Load()) { Dock = DockStyle.Fill };

    private Node? _scanRoot;
    private CancellationTokenSource? _cts;
    private ScanProgress? _progress;
    private Stopwatch _stopwatch = new();
    private bool _liveScan;
    private TreemapHit? _menuHit;
    private bool _shown;

    public Form1(string? startupPath = null)
    {
        _startupPath = startupPath;
        InitializeComponent();
        BuildUi();
    }

    private void BuildUi()
    {
        Text = "SpaceMonger" + (MftScanner.IsElevated() ? " (Administrator)" : "");
        ClientSize = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        foreach (var d in DriveInfo.GetDrives())
            if (d.IsReady) _pathBox.Items.Add(d.Name);
        _pathBox.Text = _startupPath ?? _settings.LastPath ?? (_pathBox.Items.Count > 0 ? (string)_pathBox.Items[0]! : @"C:\");
        _pathBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; StartScan(); } };

        var browse = new ToolStripButton("Browse\u2026");
        browse.Click += (_, _) => Browse();
        _scanButton.Click += (_, _) => StartScan();
        _stopButton.Click += (_, _) => _cts?.Cancel();
        _upButton.Click += (_, _) => ZoomOut();
        _topButton.Click += (_, _) => { if (_scanRoot != null) ZoomTo(_scanRoot); };
        var settings = new ToolStripButton("Settings\u2026") { Alignment = ToolStripItemAlignment.Right };
        settings.Click += (_, _) => ShowSettings();

        _colorBox.Items.AddRange(["Depth", "File type", "Age"]);
        _colorBox.SelectedIndex = (int)_settings.ColorMode;
        _colorBox.SelectedIndexChanged += (_, _) =>
        {
            _map.ColorMode = _settings.ColorMode = (ColorMode)_colorBox.SelectedIndex;
            _settings.Save();
        };
        _insightsButton.Checked = _settings.ShowInsights;
        _insightsButton.Alignment = ToolStripItemAlignment.Right;
        _insightsButton.CheckedChanged += (_, _) =>
        {
            _split.Panel2Collapsed = !(_settings.ShowInsights = _insightsButton.Checked);
            _settings.Save();
        };

        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2) };
        toolbar.Items.AddRange([_pathBox, browse, _scanButton, _stopButton, new ToolStripSeparator(), _upButton, _topButton,
            new ToolStripSeparator(), new ToolStripLabel("Colour:"), _colorBox, settings, _insightsButton]);

        var status = new StatusStrip();
        status.Items.AddRange([_hoverLabel, _statusLabel, _progressBar]);

        _map.ShowFiles = _settings.ShowFiles;
        _map.ColorMode = _settings.ColorMode;
        _map.HoverChanged += (_, hit) => _hoverLabel.Text = Describe(hit);
        _map.ItemActivated += (_, hit) => { if (hit.Node is { IsDirectory: true } d) ZoomTo(d); };
        _map.WheelZoom += (_, a) => { if (a.delta > 0) ZoomToward(a.hit); else ZoomOut(); };
        _map.ContextMenuStrip = _menu;
        _menu.Opening += MenuOpening;

        _uiTimer.Tick += (_, _) => OnUiTick();

        _insights.NodeSelected += n => { EnsureVisible(n); _map.Selected = n; };
        _insights.NodeActivated += n => { ZoomTo(n.IsDirectory ? n : n.Parent ?? n); _map.Selected = n; };
        _insights.DeleteRequested += DeleteNodes;

        _split.Panel1.Controls.Add(_map);
        _split.Panel2.Controls.Add(_insights);
        _split.Panel2Collapsed = !_settings.ShowInsights;
        _split.SplitterMoved += (_, _) => { if (_shown) _settings.InsightsWidth = _split.Panel2.Width; };
        FormClosed += (_, _) => _settings.Save();

        // Dock order: last added docks first.
        Controls.Add(_split);
        Controls.Add(_crumbs);
        Controls.Add(toolbar);
        Controls.Add(status);

        UpdateButtons();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _split.Panel2MinSize = 340;
        _split.SplitterDistance = Math.Max(300, _split.Width - Math.Max(420, _settings.InsightsWidth));
        _shown = true;
        if (_startupPath != null) StartScan();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_pathBox.Focused)
        {
            switch (keyData)
            {
                case Keys.Back: ZoomOut(); return true;
                case Keys.Escape: _cts?.Cancel(); return true;
            }
        }
        if (keyData == Keys.F5) { StartScan(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------------------------------------------------------------- scanning

    private bool IsScanning => _cts != null;

    private async void StartScan()
    {
        if (IsScanning) return;
        string path = _pathBox.Text.Trim();
        if (path.Length == 2 && path[1] == ':') path += "\\";
        if (!Directory.Exists(path))
        {
            MessageBox.Show(this, $"Folder not found:\n{path}", "SpaceMonger", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _settings.LastPath = path;
        _settings.Save();

        var cts = _cts = new CancellationTokenSource();
        var progress = _progress = new ScanProgress();
        string? note = null;
        var engine = _settings.Engine;
        if (engine == ScanEngine.NtfsMft && MftScanner.WhyUnsupported(path) is { } why)
        {
            note = why + " \u2014 used Win32";
            engine = ScanEngine.Win32Parallel;
        }

        _insights.Reset();
        _map.Selected = null;
        _stopwatch = Stopwatch.StartNew();
        _progressBar.Visible = true;
        _progressBar.Style = engine == ScanEngine.NtfsMft ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
        _progressBar.Value = 0;
        UpdateButtons();
        _uiTimer.Start();

        Node? root = null;
        try
        {
            if (engine == ScanEngine.NtfsMft)
            {
                try
                {
                    root = await Task.Run(() => MftScanner.Scan(path, progress, cts.Token));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    note = $"MFT scan failed ({ex.Message}) \u2014 used Win32";
                    engine = ScanEngine.Win32Parallel;
                    progress = _progress = new ScanProgress();
                    _progressBar.Style = ProgressBarStyle.Marquee;
                }
            }

            if (engine == ScanEngine.Win32Parallel)
            {
                root = Win32Scanner.CreateRoot(path);
                _liveScan = _settings.LiveUpdate;
                SetScanRoot(root, sorted: false);
                await Task.Run(() => Win32Scanner.Run(root, progress, cts.Token));
            }

            _liveScan = false;
            progress.Phase = "Sorting";
            await Task.Run(() => Node.FinalizeTree(root!));
            SetScanRoot(root!, sorted: true);
            _insights.SetScan(root!, complete: !cts.IsCancellationRequested);

            _stopwatch.Stop();
            string engineName = engine == ScanEngine.NtfsMft ? "MFT" : "Win32";
            string result = $"{root!.FileCount:N0} files, {progress.Directories:N0} folders, {Fmt.Size(root.Size)} " +
                            $"in {_stopwatch.Elapsed.TotalSeconds:0.00}s ({engineName})";
            if (cts.IsCancellationRequested) result = "Cancelled \u2014 partial: " + result;
            if (progress.Errors > 0) result += $", {progress.Errors:N0} inaccessible";
            if (note != null) result += "  |  " + note;
            _statusLabel.Text = result;
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "Scan cancelled";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Scan failed: " + ex.Message;
        }
        finally
        {
            _liveScan = false;
            _uiTimer.Stop();
            _progressBar.Visible = false;
            _cts = null;
            cts.Dispose();
            UpdateButtons();
        }
    }

    private void OnUiTick()
    {
        var p = _progress;
        if (p == null) return;
        if (p.Fraction >= 0) _progressBar.Value = (int)(Math.Clamp(p.Fraction, 0, 1) * 100);
        string what = p.Fraction >= 0 ? $"{p.Files:N0} records" : $"{p.Files:N0} files, {p.Directories:N0} folders";
        _statusLabel.Text = $"{p.Phase}\u2026 {what}  {_stopwatch.Elapsed.TotalSeconds:0.0}s";
        if (_liveScan)
        {
            _map.Rebuild();
            UpdateCrumbs();
        }
    }

    private void SetScanRoot(Node root, bool sorted)
    {
        _scanRoot = root;
        _map.ChildrenSorted = sorted;
        _map.ViewRoot = root;
        UpdateCrumbs();
        UpdateButtons();
    }

    private void Browse()
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = _pathBox.Text, ShowNewFolderButton = false };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _pathBox.Text = dlg.SelectedPath;
            StartScan();
        }
    }

    private void ShowSettings()
    {
        using var dlg = new SettingsForm(_settings);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _map.ShowFiles = _settings.ShowFiles;
        _map.ColorMode = _settings.ColorMode;
        if (dlg.RestartAsAdminRequested) RestartElevated();
    }

    private void RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"\"{_pathBox.Text.TrimEnd('\\')}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            Close();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC prompt was declined.
        }
    }

    // ---------------------------------------------------------------- navigation

    private void ZoomTo(Node node)
    {
        _map.ViewRoot = node;
        UpdateCrumbs();
        UpdateButtons();
        if (!IsScanning) _insights.SetView(node);
    }

    /// <summary>Zooms out to the folder containing <paramref name="n"/> if it's outside the current view.</summary>
    private void EnsureVisible(Node n)
    {
        var view = _map.ViewRoot;
        if (view == null || view == n || view.IsAncestorOf(n)) return;
        ZoomTo(n.Parent ?? n);
    }

    private void ZoomOut()
    {
        if (_map.ViewRoot?.Parent is { } parent) ZoomTo(parent);
    }

    /// <summary>Zooms one level from the current view toward the hovered item.</summary>
    private void ZoomToward(TreemapHit? hit)
    {
        var view = _map.ViewRoot;
        if (hit == null || view == null) return;
        Node? n = hit.Value.Node ?? hit.Value.Owner;
        while (n != null && n.Parent != view) n = n.Parent;
        if (n is { IsDirectory: true }) ZoomTo(n);
    }

    private void UpdateCrumbs()
    {
        _crumbs.SuspendLayout();
        _crumbs.Items.Clear();
        var chain = new List<Node>();
        for (var n = _map.ViewRoot; n != null; n = n.Parent) chain.Add(n);
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var node = chain[i];
            var b = new ToolStripButton(node.Name) { Font = i == 0 ? new Font(_crumbs.Font, FontStyle.Bold) : _crumbs.Font };
            b.Click += (_, _) => ZoomTo(node);
            _crumbs.Items.Add(b);
            if (i > 0) _crumbs.Items.Add(new ToolStripLabel("\u203A"));
        }
        if (_map.ViewRoot is { } v)
        {
            string share = _scanRoot is { Size: > 0 } r ? $"  ({100.0 * v.Size / r.Size:0.#}% of scan)" : "";
            _crumbs.Items.Add(new ToolStripLabel($"   {Fmt.Size(v.Size)} \u2022 {v.FileCount:N0} files{share}") { ForeColor = SystemColors.GrayText });
        }
        _crumbs.ResumeLayout();
    }

    private void UpdateButtons()
    {
        _scanButton.Enabled = !IsScanning;
        _stopButton.Enabled = IsScanning;
        _pathBox.Enabled = !IsScanning;
        _upButton.Enabled = _map.ViewRoot?.Parent != null;
        _topButton.Enabled = _map.ViewRoot != null && _map.ViewRoot != _scanRoot;
    }

    private string Describe(TreemapHit? hit)
    {
        if (hit is not { } h) return "";
        long parentSize = h.Node?.Parent?.Size ?? h.Owner.Size;
        string pct = parentSize > 0 ? $"{100.0 * h.Size / parentSize:0.#}% of parent" : "";
        if (h.IsFileGroup)
            return $"{h.FileGroupCount:N0} files in {h.Owner.FullPath}   {Fmt.Size(h.Size)}   {pct}";
        var n = h.Node!;
        string files = n.IsDirectory ? $"   {n.FileCount:N0} files" : "";
        return $"{n.FullPath}   {Fmt.Size(h.Size)}   {pct}{files}";
    }

    // ---------------------------------------------------------------- context menu

    private void MenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _menu.Items.Clear();
        _menuHit = _map.HitTest(_map.PointToClient(Cursor.Position));
        if (_menuHit is not { } hit) { e.Cancel = true; return; }

        Node target = hit.Node ?? hit.Owner;
        string path = target.FullPath;
        bool isDir = target.IsDirectory;

        var zoomIn = new ToolStripMenuItem("Zoom into folder", null, (_, _) => ZoomTo(target)) { Enabled = isDir && target != _map.ViewRoot };
        var zoomOut = new ToolStripMenuItem("Zoom out", null, (_, _) => ZoomOut()) { Enabled = _map.ViewRoot?.Parent != null };
        var open = new ToolStripMenuItem(isDir ? "Open folder" : "Open file", null, (_, _) => Shell(path));
        var reveal = new ToolStripMenuItem("Show in Explorer", null, (_, _) => Reveal(path));
        var copy = new ToolStripMenuItem("Copy path", null, (_, _) => Clipboard.SetText(path));
        var delete = new ToolStripMenuItem("Delete to Recycle Bin\u2026", null, (_, _) => DeleteNodes([target]))
        {
            Enabled = !IsScanning && hit.Node != null && target.Parent != null,
            ForeColor = Color.Firebrick,
        };
        _menu.Items.AddRange([zoomIn, zoomOut, new ToolStripSeparator(), open, reveal, copy, new ToolStripSeparator(), delete]);
    }

    private void Shell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "SpaceMonger", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private static void Reveal(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });

    /// <summary>Moves nodes to the Recycle Bin after one confirmation, then updates the tree, map and insights.</summary>
    private void DeleteNodes(IReadOnlyList<Node> requested)
    {
        if (IsScanning) return;
        // Skip the scan root and anything already covered by a selected ancestor.
        var nodes = requested.Where(n => n.Parent != null).Distinct()
            .Where(n => !requested.Any(o => o != n && o.IsAncestorOf(n))).ToList();
        if (nodes.Count == 0) return;

        long total = nodes.Sum(n => n.Size);
        string what = nodes.Count == 1
            ? $"{(nodes[0].IsDirectory ? "the folder" : "the file")}\n\n{nodes[0].FullPath}\n\n({Fmt.Size(total)})"
            : $"{nodes.Count} items ({Fmt.Size(total)}):\n\n" + string.Join("\n", nodes.Take(12).Select(n => n.FullPath)) +
              (nodes.Count > 12 ? $"\n\u2026and {nodes.Count - 12} more" : "");
        if (MessageBox.Show(this, $"Move {what}\n\nto the Recycle Bin?", "Delete", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        int deleted = 0;
        foreach (var node in nodes)
        {
            string path = node.FullPath;
            try
            {
                if (node.IsDirectory)
                    FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                else
                    FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (MessageBox.Show(this, $"{path}\n\n{ex.Message}", "Delete failed", MessageBoxButtons.OKCancel, MessageBoxIcon.Error) == DialogResult.Cancel) break;
                continue;
            }
            if (Directory.Exists(path) || File.Exists(path)) continue; // partially deleted

            var view = _map.ViewRoot;
            var parent = node.Parent!;
            node.Remove();
            if (view == node || (view != null && node.IsAncestorOf(view))) ZoomTo(parent);
            if (_map.Selected is { } sel && (sel == node || node.IsAncestorOf(sel))) _map.Selected = null;
            _insights.OnNodeRemoved(node);
            deleted++;
        }
        _map.Rebuild();
        UpdateCrumbs();
        _statusLabel.Text = $"Moved {deleted} item(s) to the Recycle Bin";
    }
}
