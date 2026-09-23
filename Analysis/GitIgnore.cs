using System.IO.Enumeration;

namespace SpaceMonger.Analysis;

/// <summary>
/// Lightweight .gitignore check: is a folder (or one of its ancestors inside the repo) matched by a
/// pattern in any .gitignore between it and the repository root? Handles the common forms
/// ("build/", "/dist", "cmake-build-*", "out/bin"); negations and global excludes are ignored,
/// which errs on the side of "not ignored" — i.e. fewer suggestions, never more.
/// </summary>
public sealed class GitIgnore
{
    private readonly Dictionary<Node, string[]> _patterns = new();

    public bool IsIgnored(Node dir)
    {
        for (var a = dir.Parent; a != null; a = a.Parent)
        {
            foreach (var pattern in PatternsOf(a))
            {
                // Match every path component between a and dir, and the relative path for "a/b" patterns.
                for (var c = dir; c != null && c != a; c = c.Parent)
                {
                    if (!pattern.Contains('/') && FileSystemName.MatchesSimpleExpression(pattern, c.Name)) return true;
                }
                if (pattern.Contains('/') && FileSystemName.MatchesSimpleExpression(pattern, RelativePath(a, dir))) return true;
            }
            if (a.Children?.Any(c => c.IsDirectory && c.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) == true)
                break; // reached the repository root
        }
        return false;
    }

    private static string RelativePath(Node ancestor, Node dir)
    {
        var parts = new List<string>();
        for (var c = dir; c != null && c != ancestor; c = c.Parent) parts.Add(c.Name);
        parts.Reverse();
        return string.Join('/', parts);
    }

    private string[] PatternsOf(Node dir)
    {
        if (_patterns.TryGetValue(dir, out var cached)) return cached;
        string[] result = [];
        if (dir.Children?.Any(c => !c.IsDirectory && c.Name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)) == true)
        {
            try
            {
                result = File.ReadLines(Path.Join(dir.FullPath, ".gitignore"))
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && l[0] != '#' && l[0] != '!')
                    .Select(l => l.Trim('/').Replace("**/", ""))
                    .Where(l => l.Length > 0)
                    .ToArray();
            }
            catch { /* unreadable: treat as empty */ }
        }
        return _patterns[dir] = result;
    }
}
