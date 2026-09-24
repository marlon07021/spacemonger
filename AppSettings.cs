using System.Text.Json;
using SpaceMonger.Scanning;

namespace SpaceMonger;

public sealed class AppSettings
{
    public ScanEngine Engine { get; set; } = ScanEngine.Win32Parallel;
    public bool ShowFiles { get; set; } = true;
    public bool LiveUpdate { get; set; } = true;
    public string? LastPath { get; set; }
    public ColorMode ColorMode { get; set; } = ColorMode.Depth;
    public bool ShowInsights { get; set; } = true;
    public bool Animations { get; set; } = true;
    public bool ShowTree { get; set; } = true;
    public int TreeWidth { get; set; } = 340;
    public int InsightsWidth { get; set; } = 520;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceMonger", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* corrupt settings: fall back to defaults */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are best-effort */ }
    }
}
