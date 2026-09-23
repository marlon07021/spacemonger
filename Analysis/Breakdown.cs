namespace SpaceMonger.Analysis;

public sealed record ExtensionStat(string Extension, FileCategory Category, long Bytes, long Count);

public sealed record AgeBucket(string Label, long Bytes, long Count);

/// <summary>Space by file category, by extension and by last-modified age, for one subtree.</summary>
public sealed class Breakdown
{
    public required Node Root { get; init; }
    public long[] CategoryBytes { get; } = new long[FileCategories.Count];
    public long[] CategoryCounts { get; } = new long[FileCategories.Count];
    public List<ExtensionStat> Extensions { get; private set; } = [];
    public AgeBucket[] Ages { get; private set; } = [];
    public long TotalBytes { get; private set; }

    private static readonly (string label, double maxDays)[] AgeEdges =
    [
        ("Last 30 days", 30), ("1–6 months", 182), ("6–12 months", 365),
        ("1–2 years", 730), ("2–5 years", 1826), ("Over 5 years", double.MaxValue),
    ];

    /// <summary>One colour per age bucket (plus "unknown"), shared by the Age tab and the treemap.</summary>
    public static readonly Color[] AgeColors =
    [
        Color.FromArgb(0x4C, 0xB8, 0x4C), Color.FromArgb(0x9A, 0xC8, 0x3C), Color.FromArgb(0xD8, 0xC8, 0x3A),
        Color.FromArgb(0xE8, 0x9A, 0x30), Color.FromArgb(0xE0, 0x62, 0x3A), Color.FromArgb(0xC8, 0x3A, 0x3A), Color.Gray,
    ];

    /// <summary>Index into <see cref="AgeColors"/> for a last-write FILETIME (0 = unknown).</summary>
    public static int AgeBucketOf(long lastWrite, long now)
    {
        if (lastWrite <= 0) return AgeEdges.Length;
        double days = (now - lastWrite) / 864_000_000_000d;
        int bucket = 0;
        while (bucket < AgeEdges.Length - 1 && days > AgeEdges[bucket].maxDays) bucket++;
        return bucket;
    }

    public static Breakdown Compute(Node root, CancellationToken ct)
    {
        var b = new Breakdown { Root = root };
        var ext = new Dictionary<string, (long bytes, long count)>(StringComparer.Ordinal);
        var ageBytes = new long[AgeEdges.Length + 1];
        var ageCounts = new long[AgeEdges.Length + 1];
        long now = DateTime.UtcNow.ToFileTimeUtc();

        var stack = new Stack<Node>();
        stack.Push(root);
        int visited = 0;
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.Children == null) continue;
            foreach (var c in n.Children)
            {
                if (c.IsDirectory) { stack.Push(c); continue; }
                if ((++visited & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

                var cat = FileCategories.Of(c.Name);
                b.CategoryBytes[(int)cat] += c.Size;
                b.CategoryCounts[(int)cat]++;
                b.TotalBytes += c.Size;

                string e = FileCategories.Extension(c.Name);
                ext.TryGetValue(e, out var s);
                ext[e] = (s.bytes + c.Size, s.count + 1);

                int bucket = AgeBucketOf(c.LastWrite, now);
                ageBytes[bucket] += c.Size;
                ageCounts[bucket]++;
            }
        }

        b.Extensions = ext.Select(kv => new ExtensionStat(kv.Key, kv.Key == "(none)" ? FileCategory.Other : FileCategories.Of(kv.Key),
                kv.Value.bytes, kv.Value.count))
            .OrderByDescending(x => x.Bytes).ToList();
        var ages = new List<AgeBucket>();
        for (int i = 0; i < AgeEdges.Length; i++) ages.Add(new AgeBucket(AgeEdges[i].label, ageBytes[i], ageCounts[i]));
        if (ageCounts[^1] > 0) ages.Add(new AgeBucket("Unknown", ageBytes[^1], ageCounts[^1]));
        b.Ages = ages.ToArray();
        return b;
    }
}
