namespace SpaceMonger.Analysis;

/// <summary>Aggregated content statistics for one directory's whole subtree.</summary>
public sealed class DirStats
{
    public readonly long[] CategoryBytes = new long[FileCategories.Count];
    public readonly long[] CategoryCounts = new long[FileCategories.Count];
    public long Files;
    public long PreciousFiles;
    public int Subdirs;
    public long Oldest = long.MaxValue;
    public long Newest;

    public double Fraction(FileCategory c, long total) => total > 0 ? (double)CategoryBytes[(int)c] / total : 0;

    /// <summary>Share of the files (by count) that are irreplaceable content: source, documents, media.</summary>
    public double PreciousFileShare => Files > 0 ? (double)PreciousFiles / Files : 0;

    public double Fraction(long total, params FileCategory[] cats)
    {
        long sum = 0;
        foreach (var c in cats) sum += CategoryBytes[(int)c];
        return total > 0 ? (double)sum / total : 0;
    }
}

public static class FolderFeatures
{
    /// <summary>Extra numeric features on top of the per-category byte fractions.</summary>
    public const int NumericCount = FileCategories.Count + 13;

    /// <summary>
    /// One bottom-up pass computing <see cref="DirStats"/> for every directory of at least
    /// <paramref name="minSize"/> bytes (smaller ones are merged into their parent, then dropped).
    /// </summary>
    public static Dictionary<Node, DirStats> Compute(Node root, long minSize, CancellationToken ct)
    {
        var dirs = Node.Directories(root);
        var stats = new Dictionary<Node, DirStats>(dirs.Count / 4);
        for (int i = dirs.Count - 1; i >= 0; i--)
        {
            if ((i & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
            var d = dirs[i];
            var s = new DirStats();
            if (d.Children != null)
            {
                foreach (var c in d.Children)
                {
                    if (c.IsDirectory)
                    {
                        if (!stats.TryGetValue(c, out var cs)) continue;
                        for (int k = 0; k < cs.CategoryBytes.Length; k++)
                        {
                            s.CategoryBytes[k] += cs.CategoryBytes[k];
                            s.CategoryCounts[k] += cs.CategoryCounts[k];
                        }
                        s.Files += cs.Files;
                        s.PreciousFiles += cs.PreciousFiles;
                        s.Subdirs += 1 + cs.Subdirs;
                        if (cs.Oldest < s.Oldest) s.Oldest = cs.Oldest;
                        if (cs.Newest > s.Newest) s.Newest = cs.Newest;
                        if (c.Size < minSize) stats.Remove(c);
                    }
                    else
                    {
                        int cat = (int)FileCategories.Of(c.Name);
                        s.CategoryBytes[cat] += c.Size;
                        s.CategoryCounts[cat]++;
                        s.Files++;
                        if (FileCategories.IsPrecious(c.Name)) s.PreciousFiles++;
                        if (c.LastWrite > 0)
                        {
                            if (c.LastWrite < s.Oldest) s.Oldest = c.LastWrite;
                            if (c.LastWrite > s.Newest) s.Newest = c.LastWrite;
                        }
                    }
                }
            }
            stats[d] = s;
        }
        return stats;
    }

    /// <summary>Structural context the name alone can't reveal (a "bin" next to a .csproj vs. a JRE's "bin").</summary>
    public readonly record struct Context(bool ParentHasProjectFile, bool InsideRepo, bool IsRepoRoot);

    private static readonly string[] ProjectFiles =
        [".csproj", ".vbproj", ".fsproj", ".vcxproj", ".sln", "package.json", "cargo.toml", "pom.xml", "build.gradle", "build.gradle.kts", "cmakelists.txt", "pyproject.toml", "go.mod"];

    public static bool IsProjectFile(string name)
    {
        foreach (var p in ProjectFiles)
            if (name.EndsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Pre-order pass computing <see cref="Context"/> for every directory.</summary>
    public static Dictionary<Node, Context> Contexts(List<Node> preorder)
    {
        var result = new Dictionary<Node, Context>(preorder.Count);
        var inRepo = new Dictionary<Node, bool>(preorder.Count);
        foreach (var d in preorder)
        {
            bool isRepoRoot = d.Children?.Any(c => c.IsDirectory && c.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) == true;
            bool parentInRepo = d.Parent != null && inRepo.TryGetValue(d.Parent, out var r) && r;
            inRepo[d] = parentInRepo || isRepoRoot;
            bool parentProject = d.Parent?.Children?.Any(c => !c.IsDirectory && IsProjectFile(c.Name)) == true;
            result[d] = new Context(parentProject, parentInRepo, isRepoRoot);
        }
        return result;
    }

    public static float[] Vector(Node dir, DirStats s, string lowerPath, Context ctx)
    {
        var v = new float[NumericCount];
        long total = Math.Max(1, dir.Size);
        for (int k = 0; k < FileCategories.Count; k++) v[k] = (float)((double)s.CategoryBytes[k] / total);

        long now = DateTime.UtcNow.ToFileTimeUtc();
        const double TicksPerDay = 864_000_000_000d;
        double newestDays = s.Newest > 0 ? Math.Max(0, (now - s.Newest) / TicksPerDay) : 0;
        double oldestDays = s.Oldest != long.MaxValue ? Math.Max(0, (now - s.Oldest) / TicksPerDay) : 0;
        int depth = 0;
        foreach (char ch in lowerPath) if (ch == '\\') depth++;

        int i = FileCategories.Count;
        v[i++] = (float)(Math.Log10(dir.Size + 1) / 12);
        v[i++] = (float)(Math.Log10(s.Files + 1) / 7);
        v[i++] = (float)(Math.Log10(dir.Size / Math.Max(1.0, s.Files) + 1) / 10);
        v[i++] = (float)(Math.Log10(s.Subdirs + 1) / 6);
        v[i++] = (float)(Math.Log10(newestDays + 1) / 4);
        v[i++] = (float)(Math.Log10(oldestDays + 1) / 4);
        v[i++] = depth / 20f;
        v[i++] = dir.Name.StartsWith('.') ? 1 : 0;
        v[i++] = lowerPath.Contains(@"\appdata\") ? 1 : 0;
        v[i++] = lowerPath.Contains(@"\program files") || lowerPath.Contains(@"\programdata\") ? 1 : 0;
        v[i++] = ctx.ParentHasProjectFile ? 1 : 0;
        v[i++] = ctx.InsideRepo ? 1 : 0;
        v[i++] = ctx.IsRepoRoot ? 1 : 0;
        return v;
    }

    /// <summary>
    /// Name tokens for the text featurizer: "GPUCache" → "gpucache gpu cache", parent tokens get a "p_" prefix.
    /// </summary>
    public static string Text(Node dir)
    {
        var sb = new System.Text.StringBuilder();
        AppendTokens(sb, dir.Name, "");
        if (dir.Parent != null) AppendTokens(sb, dir.Parent.Name, "p_");
        return sb.ToString();
    }

    private static void AppendTokens(System.Text.StringBuilder sb, string name, string prefix)
    {
        var whole = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (whole.Length > 0) sb.Append(prefix).Append(whole).Append(' ');
        int start = -1;
        for (int i = 0; i <= name.Length; i++)
        {
            bool boundary = i == name.Length || !char.IsLetterOrDigit(name[i]) ||
                            (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1])) ||
                            (i > 0 && char.IsDigit(name[i]) != char.IsDigit(name[i - 1]));
            if (boundary && start >= 0)
            {
                var tok = name[start..i].ToLowerInvariant();
                if (tok != whole) sb.Append(prefix).Append(tok).Append(' ');
                start = -1;
            }
            if (i < name.Length && char.IsLetterOrDigit(name[i]) && start < 0) start = i;
        }
    }
}
