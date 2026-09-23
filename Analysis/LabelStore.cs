using System.Text.Json;

namespace SpaceMonger.Analysis;

/// <summary>Folder labels given by the user ("this is junk: Cache", "keep this"). Persisted; used as training data.</summary>
public sealed class LabelStore
{
    private readonly Dictionary<string, FolderClass> _labels;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceMonger", "labels.json");

    private LabelStore(Dictionary<string, FolderClass> labels) => _labels = labels;

    public static LabelStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? [];
                var d = new Dictionary<string, FolderClass>(StringComparer.OrdinalIgnoreCase);
                foreach (var (k, v) in raw)
                    if (Enum.TryParse<FolderClass>(v, out var c)) d[k] = c;
                return new LabelStore(d);
            }
        }
        catch { /* ignore a corrupt file */ }
        return new LabelStore(new(StringComparer.OrdinalIgnoreCase));
    }

    public FolderClass? Get(string path) => _labels.TryGetValue(path, out var c) ? c : null;

    public int Count => _labels.Count;

    public void Set(string path, FolderClass? label)
    {
        if (label is { } l) _labels[path] = l; else _labels.Remove(path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_labels.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}
