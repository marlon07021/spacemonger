namespace SpaceMonger.Scanning;

public enum ScanEngine
{
    Win32Parallel,
    NtfsMft,
}

/// <summary>Counters shared between a scanner thread and the UI. Read without locking; values are advisory.</summary>
public sealed class ScanProgress
{
    public long Files;
    public long Directories;
    public long Errors;

    /// <summary>For the MFT engine: records processed / total, as a 0..1 fraction. -1 when not applicable.</summary>
    public double Fraction = -1;

    public string Phase = "";
}
