using System.Diagnostics;
using SpaceMonger.Scanning;

namespace SpaceMonger;

public sealed class SettingsForm : Form
{
    private readonly RadioButton _win32 = new() { Text = "Win32 parallel scan (works everywhere, no admin needed)", AutoSize = true };
    private readonly RadioButton _mft = new() { Text = "NTFS MFT direct read (fastest; admin + NTFS only)", AutoSize = true };
    private readonly CheckBox _showFiles = new() { Text = "Show individual files in the map", AutoSize = true };
    private readonly CheckBox _live = new() { Text = "Update the map live while scanning", AutoSize = true };
    private readonly CheckBox _animations = new() { Text = "Animations (zoom, charts)", AutoSize = true };

    public const string Author = "marlon07021";
    public const string RepoUrl = "https://github.com/marlon07021/spacemonger";

    /// <summary>Set when the user asks to relaunch elevated.</summary>
    public bool RestartAsAdminRequested { get; private set; }

    public SettingsForm(AppSettings s)
    {
        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _win32.Checked = s.Engine == ScanEngine.Win32Parallel;
        _mft.Checked = s.Engine == ScanEngine.NtfsMft;
        _showFiles.Checked = s.ShowFiles;
        _live.Checked = s.LiveUpdate;
        _animations.Checked = s.Animations;

        var engineBox = new GroupBox { Text = "Scan engine", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Padding = new Padding(8) };
        var engineFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        engineFlow.Controls.Add(_win32);
        engineFlow.Controls.Add(_mft);

        bool elevated = MftScanner.IsElevated();
        var adminNote = new Label
        {
            AutoSize = true,
            ForeColor = elevated ? Color.SeaGreen : Color.DarkOrange,
            Text = elevated
                ? "Running as administrator: the MFT engine is available."
                : "Not running as administrator: the MFT engine will fall back to Win32.",
            Margin = new Padding(3, 8, 3, 3),
        };
        engineFlow.Controls.Add(adminNote);
        if (!elevated)
        {
            var restart = new Button { Text = "Restart as administrator", AutoSize = true };
            restart.Click += (_, _) => { RestartAsAdminRequested = true; Commit(s); DialogResult = DialogResult.OK; };
            engineFlow.Controls.Add(restart);
        }
        engineBox.Controls.Add(engineFlow);

        var viewBox = new GroupBox { Text = "Display", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Padding = new Padding(8) };
        var viewFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        viewFlow.Controls.Add(_showFiles);
        viewFlow.Controls.Add(_live);
        viewFlow.Controls.Add(_animations);
        viewBox.Controls.Add(viewFlow);

        var aboutBox = new GroupBox { Text = "About", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Padding = new Padding(8) };
        var aboutFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        var version = typeof(SettingsForm).Assembly.GetName().Version;
        aboutFlow.Controls.Add(new Label { Text = $"SpaceMonger {version?.ToString(3)}", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        aboutFlow.Controls.Add(new Label { Text = $"Vibe-coded with \u2665 by @{Author}", AutoSize = true });
        var link = new LinkLabel { Text = RepoUrl, AutoSize = true };
        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true });
        aboutFlow.Controls.Add(link);
        aboutFlow.Controls.Add(new Label { Text = "MIT licensed.", AutoSize = true, ForeColor = SystemColors.GrayText });
        aboutBox.Controls.Add(aboutFlow);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) => Commit(s);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(engineBox);
        layout.Controls.Add(viewBox);
        layout.Controls.Add(aboutBox);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
    }

    private void Commit(AppSettings s)
    {
        s.Engine = _mft.Checked ? ScanEngine.NtfsMft : ScanEngine.Win32Parallel;
        s.ShowFiles = _showFiles.Checked;
        s.LiveUpdate = _live.Checked;
        s.Animations = _animations.Checked;
        s.Save();
    }
}
