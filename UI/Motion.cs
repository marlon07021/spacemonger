using System.Diagnostics;

namespace SpaceMonger.UI;

/// <summary>Shared animation settings and helpers.</summary>
public static class Motion
{
    /// <summary>Global switch (Settings → "Animations").</summary>
    public static bool Enabled { get; set; } = true;

    public static double EaseOutCubic(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double u = 1 - t;
        return 1 - u * u * u;
    }

    public static Rectangle Lerp(Rectangle a, Rectangle b, double t) => Rectangle.FromLTRB(
        (int)Math.Round(a.Left + (b.Left - a.Left) * t),
        (int)Math.Round(a.Top + (b.Top - a.Top) * t),
        (int)Math.Round(a.Right + (b.Right - a.Right) * t),
        (int)Math.Round(a.Bottom + (b.Bottom - a.Bottom) * t));
}

/// <summary>
/// Drives a 0→1 eased progress value on the UI thread for a fixed duration, invoking a callback
/// every frame (~60 fps). Restarting mid-flight simply begins a new run.
/// </summary>
public sealed class Tween : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly Stopwatch _clock = new();
    private readonly int _durationMs;
    private readonly Action<double> _onFrame;

    public Tween(int durationMs, Action<double> onFrame)
    {
        _durationMs = durationMs;
        _onFrame = onFrame;
        _timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, _clock.Elapsed.TotalMilliseconds / _durationMs);
            if (t >= 1) _timer.Stop();
            _onFrame(Motion.EaseOutCubic(t));
        };
    }

    public bool Running => _timer.Enabled;

    public void Start()
    {
        if (!Motion.Enabled) { _onFrame(1); return; }
        _clock.Restart();
        _timer.Start();
        _onFrame(0);
    }

    public void Stop() => _timer.Stop();

    public void Dispose() => _timer.Dispose();
}
