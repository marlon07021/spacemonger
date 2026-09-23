using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace SpaceMonger.Analysis;

/// <summary>A saved directory tree (sizes only, no files) used to see what changed between scans.</summary>
public sealed class SnapshotNode
{
    public required string Name;
    public long Size;
    public long FileCount;
    public SnapshotNode[] Children = [];

    public SnapshotNode? Find(string name)
    {
        // Children are sorted by name (ordinal, case-insensitive) when loaded.
        int lo = 0, hi = Children.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int c = string.Compare(Children[mid].Name, name, StringComparison.OrdinalIgnoreCase);
            if (c == 0) return Children[mid];
            if (c < 0) lo = mid + 1; else hi = mid - 1;
        }
        return null;
    }
}

public sealed record SnapshotInfo(string FilePath, DateTime TakenUtc, long TotalSize)
{
    public override string ToString() => $"{TakenUtc.ToLocalTime():yyyy-MM-dd HH:mm}  —  {Fmt.Size(TotalSize)}";
}

public enum ChangeKind { Grew, Shrank, New, Deleted }

public sealed record ChangeItem(string Path, Node? Current, ChangeKind Kind, long Before, long After)
{
    public long Delta => After - Before;
}

public static class Snapshots
{
    private const uint Magic = 0x4E534D53; // "SMSN"
    private const int Version = 1;
    private const int KeepPerRoot = 30;
    /// <summary>Folders smaller than this aren't stored (they can't be a meaningful change anyway).</summary>
    private const long MinStoredSize = 256 * 1024;

    private static string DirFor(string rootPath)
    {
        var key = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(rootPath.TrimEnd('\\').ToLowerInvariant())).ToString("x16");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceMonger", "snapshots", key);
    }

    /// <summary>Writes the directory tree (Brotli-compressed, pre-order) and prunes old snapshots.</summary>
    public static void Save(Node root)
    {
        string dir = DirFor(root.FullPath);
        Directory.CreateDirectory(dir);
        var taken = DateTime.UtcNow;
        string file = Path.Combine(dir, taken.ToString("yyyyMMdd-HHmmss") + ".snap");

        using (var fs = File.Create(file))
        using (var br = new BrotliStream(fs, CompressionLevel.Fastest))
        using (var w = new BinaryWriter(br, Encoding.UTF8))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(root.FullPath);
            w.Write(taken.Ticks);
            w.Write(root.Size);
            var stack = new Stack<Node>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                var subdirs = n.Children?.Where(c => c.IsDirectory && c.Size >= MinStoredSize).ToArray() ?? [];
                w.Write(n.Name);
                w.Write(n.Size);
                w.Write(n.FileCount);
                w.Write(subdirs.Length);
                for (int i = subdirs.Length - 1; i >= 0; i--) stack.Push(subdirs[i]);
            }
        }

        foreach (var old in Directory.GetFiles(dir, "*.snap").OrderByDescending(f => f).Skip(KeepPerRoot))
            File.Delete(old);
    }

    public static List<SnapshotInfo> List(string rootPath)
    {
        var result = new List<SnapshotInfo>();
        string dir = DirFor(rootPath);
        if (!Directory.Exists(dir)) return result;
        foreach (var f in Directory.GetFiles(dir, "*.snap").OrderByDescending(f => f))
        {
            try
            {
                using var fs = File.OpenRead(f);
                using var r = new BinaryReader(new BrotliStream(fs, CompressionMode.Decompress), Encoding.UTF8);
                if (r.ReadUInt32() != Magic || r.ReadInt32() != Version) continue;
                r.ReadString();
                var taken = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
                result.Add(new SnapshotInfo(f, taken, r.ReadInt64()));
            }
            catch { /* unreadable snapshot: skip */ }
        }
        return result;
    }

    public static SnapshotNode Load(string file)
    {
        using var fs = File.OpenRead(file);
        using var r = new BinaryReader(new BrotliStream(fs, CompressionMode.Decompress), Encoding.UTF8);
        if (r.ReadUInt32() != Magic || r.ReadInt32() != Version) throw new InvalidDataException("Not a SpaceMonger snapshot");
        r.ReadString(); r.ReadInt64(); r.ReadInt64();
        return ReadNode(r);
    }

    private static SnapshotNode ReadNode(BinaryReader r)
    {
        // Iterative pre-order read (deep trees would overflow a recursive reader).
        var root = new SnapshotNode { Name = r.ReadString(), Size = r.ReadInt64(), FileCount = r.ReadInt64() };
        var stack = new Stack<(SnapshotNode node, int remaining, List<SnapshotNode> kids)>();
        int count = r.ReadInt32();
        stack.Push((root, count, new List<SnapshotNode>(count)));
        while (stack.Count > 0)
        {
            var (node, remaining, kids) = stack.Pop();
            if (remaining == 0)
            {
                kids.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                node.Children = kids.ToArray();
                continue;
            }
            stack.Push((node, remaining - 1, kids));
            var child = new SnapshotNode { Name = r.ReadString(), Size = r.ReadInt64(), FileCount = r.ReadInt64() };
            kids.Add(child);
            int n = r.ReadInt32();
            stack.Push((child, n, new List<SnapshotNode>(n)));
        }
        return root;
    }

    /// <summary>
    /// Finds change "hotspots": the deepest folders that explain a change of at least
    /// <paramref name="threshold"/> bytes. A folder is reported only if its changed subfolders
    /// don't already account for most (70%) of its own change.
    /// </summary>
    public static List<ChangeItem> Diff(Node current, SnapshotNode before, long threshold)
    {
        var result = new List<ChangeItem>();
        Walk(current, before, current.FullPath, threshold, result);
        result.Sort((a, b) => Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta)));
        return result;
    }

    /// <summary>Returns the change it reported (itself or via descendants).</summary>
    private static long Walk(Node cur, SnapshotNode old, string path, long threshold, List<ChangeItem> result)
    {
        long delta = cur.Size - old.Size;
        if (Math.Abs(delta) < threshold) return 0;

        long explained = 0;
        var matched = new HashSet<SnapshotNode>();
        foreach (var c in cur.Children ?? [])
        {
            if (!c.IsDirectory) continue;
            string childPath = Path.Join(path, c.Name);
            var oc = old.Find(c.Name);
            if (oc == null)
            {
                if (c.Size >= threshold)
                {
                    result.Add(new ChangeItem(childPath, c, ChangeKind.New, 0, c.Size));
                    explained += c.Size;
                }
                continue;
            }
            matched.Add(oc);
            explained += Walk(c, oc, childPath, threshold, result);
        }
        foreach (var oc in old.Children)
        {
            if (matched.Contains(oc) || oc.Size < threshold) continue;
            result.Add(new ChangeItem(Path.Join(path, oc.Name), null, ChangeKind.Deleted, oc.Size, 0));
            explained -= oc.Size;
        }

        if (Math.Abs(explained) >= 0.7 * Math.Abs(delta) && Math.Sign(explained) == Math.Sign(delta)) return explained;
        result.Add(new ChangeItem(path, cur, delta > 0 ? ChangeKind.Grew : ChangeKind.Shrank, old.Size, cur.Size));
        return delta;
    }
}
