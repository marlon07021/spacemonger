namespace SpaceMonger;

/// <summary>
/// A file or directory in the scanned tree. Kept deliberately small because a full
/// drive scan can produce millions of these.
/// </summary>
public sealed class Node
{
    public string Name;
    public Node? Parent;

    /// <summary>Null for files, and for directories that have not been enumerated yet.</summary>
    public Node[]? Children;

    /// <summary>Total bytes (whole subtree for directories). Updated with Interlocked during live scans.</summary>
    public long Size;

    /// <summary>Number of files in the subtree (directories only).</summary>
    public long FileCount;

    /// <summary>Last write time as a UTC FILETIME. For directories: newest file in the subtree (set by <see cref="FinalizeTree"/>).</summary>
    public long LastWrite;

    public readonly bool IsDirectory;

    public Node(string name, Node? parent, bool isDirectory)
    {
        Name = name;
        Parent = parent;
        IsDirectory = isDirectory;
    }

    public int Depth
    {
        get
        {
            int d = 0;
            for (var p = Parent; p != null; p = p.Parent) d++;
            return d;
        }
    }

    /// <summary>The root node's Name holds the absolute scan path; descendants hold plain names.</summary>
    public string FullPath
    {
        get
        {
            if (Parent == null) return Name;
            var parts = new List<string>();
            Node n = this;
            for (; n.Parent != null; n = n.Parent) parts.Add(n.Name);
            var sb = new System.Text.StringBuilder(n.Name);
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                if (sb.Length == 0 || sb[^1] != '\\') sb.Append('\\');
                sb.Append(parts[i]);
            }
            return sb.ToString();
        }
    }

    public bool IsAncestorOf(Node other)
    {
        for (var p = other.Parent; p != null; p = p.Parent)
            if (p == this) return true;
        return false;
    }

    /// <summary>Detaches this node from its parent and subtracts its totals from all ancestors.</summary>
    public void Remove()
    {
        var parent = Parent;
        if (parent?.Children == null) return;
        parent.Children = Array.FindAll(parent.Children, c => c != this);
        long files = IsDirectory ? FileCount : 1;
        for (var p = parent; p != null; p = p.Parent)
        {
            p.Size -= Size;
            p.FileCount -= files;
        }
        Parent = null;
    }

    /// <summary>
    /// Post-scan pass: sorts every directory's children by size (largest first, so layout needn't
    /// re-sort) and rolls the newest file timestamp up into each directory.
    /// </summary>
    public static void FinalizeTree(Node root)
    {
        var dirs = Directories(root);
        var cmp = Comparer<Node>.Create((a, b) => b.Size.CompareTo(a.Size));
        for (int i = dirs.Count - 1; i >= 0; i--)
        {
            var d = dirs[i];
            if (d.Children == null) continue;
            Array.Sort(d.Children, cmp);
            long newest = 0;
            foreach (var c in d.Children)
                if (c.LastWrite > newest) newest = c.LastWrite;
            d.LastWrite = newest;
        }
    }

    /// <summary>All directories of the subtree in pre-order (parents before children).</summary>
    public static List<Node> Directories(Node root)
    {
        var result = new List<Node>();
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            result.Add(n);
            if (n.Children == null) continue;
            foreach (var c in n.Children)
                if (c.IsDirectory) stack.Push(c);
        }
        return result;
    }

    public DateTime LastWriteUtc => LastWrite > 0 ? DateTime.FromFileTimeUtc(LastWrite) : DateTime.MinValue;
}

public static class Fmt
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Size(long bytes)
    {
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : v >= 100 ? $"{v:0} {Units[u]}" : v >= 10 ? $"{v:0.0} {Units[u]}" : $"{v:0.00} {Units[u]}";
    }
}
