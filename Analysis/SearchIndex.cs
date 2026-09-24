using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace SpaceMonger.Analysis;

/// <summary>
/// Flat pre-order index of every node in a scan. A directory's subtree is the contiguous range
/// [index, end), so searching "everything" or "only the current view" is a single parallel pass
/// over an array — no path strings are built until results are displayed.
/// </summary>
public sealed class SearchIndex
{
    private readonly Node[] _nodes;
    private readonly int[] _subtreeEnd;                 // exclusive end of each node's subtree
    private readonly Dictionary<Node, int> _dirIndex;   // directory → its position

    public Node Root { get; }
    public int Count => _nodes.Length;
    public TimeSpan BuildTime { get; }

    private SearchIndex(Node root, Node[] nodes, int[] ends, Dictionary<Node, int> dirs, TimeSpan buildTime)
    {
        Root = root; _nodes = nodes; _subtreeEnd = ends; _dirIndex = dirs; BuildTime = buildTime;
    }

    public static SearchIndex Build(Node root, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var nodes = new List<Node>(1 << 16);
        var ends = new List<int>(1 << 16);
        var dirs = new Dictionary<Node, int>();

        // Iterative pre-order; each directory's end index is patched when its subtree is done.
        var stack = new Stack<(Node node, int index, bool exit)>();
        stack.Push((root, -1, false));
        while (stack.Count > 0)
        {
            var (n, idx, exit) = stack.Pop();
            if (exit) { ends[idx] = nodes.Count; continue; }
            int my = nodes.Count;
            nodes.Add(n);
            ends.Add(my + 1);
            if (!n.IsDirectory) continue;
            dirs[n] = my;
            if ((my & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            stack.Push((n, my, true));
            var children = n.Children;
            if (children != null)
                for (int i = children.Length - 1; i >= 0; i--) stack.Push((children[i], -1, false));
        }
        return new SearchIndex(root, nodes.ToArray(), ends.ToArray(), dirs, sw.Elapsed);
    }

    public SearchResult Search(SearchQuery q, Node? scope, int maxResults, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int start = 0, end = _nodes.Length;
        if (scope != null && scope != Root && _dirIndex.TryGetValue(scope, out int si))
        {
            start = si + 1;           // the scope folder itself isn't a result
            end = _subtreeEnd[si];
        }
        else if (scope == Root) start = 1;

        // Each partition keeps only its own top-K by size in a min-heap, so a query matching
        // millions of items never materializes or sorts them all.
        var partials = new ConcurrentBag<PriorityQueue<Node, long>>();
        long fileBytes = 0, count = 0;
        Parallel.ForEach(Partitioner.Create(start, end, 1 << 15), new ParallelOptions { CancellationToken = ct }, range =>
        {
            var heap = new PriorityQueue<Node, long>();
            long bytes = 0, hits = 0;
            for (int i = range.Item1; i < range.Item2; i++)
            {
                var n = _nodes[i];
                if (!q.Matches(n)) continue;
                hits++;
                if (!n.IsDirectory) bytes += n.Size;
                if (heap.Count < maxResults) heap.Enqueue(n, n.Size);
                else if (n.Size > heap.Peek().Size) heap.EnqueueDequeue(n, n.Size);
            }
            if (heap.Count > 0) partials.Add(heap);
            Interlocked.Add(ref fileBytes, bytes);
            Interlocked.Add(ref count, hits);
            ct.ThrowIfCancellationRequested();
        });

        // Biggest first — the useful order for a disk-space tool.
        var top = partials.SelectMany(h => h.UnorderedItems.Select(x => x.Element))
            .OrderByDescending(n => n.Size).Take(maxResults).ToList();
        return new SearchResult(top, count, fileBytes, sw.Elapsed);
    }
}

public sealed record SearchResult(List<Node> Top, long Count, long FileBytes, TimeSpan Elapsed);

/// <summary>
/// Query syntax (all parts optional, combined with AND, case-insensitive):
///   report 2024          name contains "report" and "2024"
///   *.mp4  IMG_????      wildcards (* and ?) match the whole name
///   ext:mp4,mkv          file extension(s)
///   type:video           file category (video, audio, image, document, archive, code, …)
///   size:>1gb  size:&lt;10k   size bounds (b, k/kb, m/mb, g/gb, t/tb)
///   age:>1y  age:&lt;7d      older / newer than (d, w, m = months, y)
///   is:file  is:folder   kind
///   "exact phrase"       quoted text is one term
/// </summary>
public sealed class SearchQuery
{
    private readonly List<string> _terms = [];
    private readonly List<string> _wildcards = [];
    private HashSet<string>? _exts;
    private HashSet<FileCategory>? _types;
    private long _minSize = -1, _maxSize = long.MaxValue;
    private long _olderThan = long.MaxValue, _newerThan = long.MinValue; // FILETIME bounds
    private bool? _folders; // null = both

    public string? Error { get; private set; }
    public bool IsEmpty { get; private set; } = true;

    public static SearchQuery Parse(string text)
    {
        var q = new SearchQuery();
        long now = DateTime.UtcNow.ToFileTimeUtc();
        foreach (var raw in Tokenize(text))
        {
            q.IsEmpty = false;
            int colon = raw.IndexOf(':');
            string key = colon > 0 ? raw[..colon].ToLowerInvariant() : "";
            string val = colon > 0 ? raw[(colon + 1)..] : raw;
            switch (key)
            {
                case "ext":
                    q._exts ??= new(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in val.Split(',', StringSplitOptions.RemoveEmptyEntries)) q._exts.Add(e.TrimStart('.', '*'));
                    q._folders = false;
                    break;
                case "type":
                    q._types ??= [];
                    foreach (var t in val.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var match = Enum.GetValues<FileCategory>().FirstOrDefault(c => c.ToString().StartsWith(t, StringComparison.OrdinalIgnoreCase), (FileCategory)(-1));
                        if ((int)match < 0) { q.Error = $"Unknown type “{t}”. Try: {string.Join(", ", Enum.GetNames<FileCategory>()).ToLowerInvariant()}"; return q; }
                        q._types.Add(match);
                    }
                    q._folders = false;
                    break;
                case "size":
                    if (!ParseBound(val, ParseSize, out bool greater, out long bytes)) { q.Error = $"Bad size “{val}” (e.g. size:>100mb)"; return q; }
                    if (greater) q._minSize = Math.Max(q._minSize, bytes + 1); else q._maxSize = Math.Min(q._maxSize, bytes - 1);
                    break;
                case "age":
                case "modified":
                    if (!ParseBound(val, ParseAgeTicks, out bool older, out long ticks)) { q.Error = $"Bad age “{val}” (e.g. age:>1y, age:<7d)"; return q; }
                    if (older) q._olderThan = Math.Min(q._olderThan, now - ticks); else q._newerThan = Math.Max(q._newerThan, now - ticks);
                    break;
                case "is":
                    q._folders = val.ToLowerInvariant() switch { "folder" or "dir" or "directory" => true, "file" => false, _ => q._folders };
                    break;
                default:
                    if (raw.Contains('*') || raw.Contains('?')) q._wildcards.Add(raw);
                    else q._terms.Add(raw);
                    break;
            }
        }
        return q;
    }

    public bool Matches(Node n)
    {
        if (_folders is { } f && n.IsDirectory != f) return false;
        long size = n.Size;
        if (size < _minSize || size > _maxSize) return false;
        if (_olderThan != long.MaxValue && (n.LastWrite <= 0 || n.LastWrite > _olderThan)) return false;
        if (_newerThan != long.MinValue && n.LastWrite < _newerThan) return false;

        string name = n.Name;
        if (_exts != null)
        {
            int dot = name.LastIndexOf('.');
            if (dot < 0 || !_exts.Contains(name[(dot + 1)..])) return false;
        }
        if (_types != null && !_types.Contains(FileCategories.Of(name))) return false;
        foreach (var t in _terms)
            if (!name.Contains(t, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var w in _wildcards)
            if (!FileSystemName.MatchesSimpleExpression(w, name, ignoreCase: true)) return false;
        return true;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) yield break;
            var sb = new System.Text.StringBuilder();
            bool quoted = false;
            for (; i < text.Length && (quoted || !char.IsWhiteSpace(text[i])); i++)
            {
                if (text[i] == '"') { quoted = !quoted; continue; }
                sb.Append(text[i]);
            }
            if (sb.Length > 0) yield return sb.ToString();
        }
    }

    private static bool ParseBound(string val, Func<string, long?> parse, out bool greater, out long value)
    {
        greater = true;
        value = 0;
        val = val.Trim();
        if (val.StartsWith('>')) { val = val[1..].TrimStart('='); }
        else if (val.StartsWith('<')) { greater = false; val = val[1..].TrimStart('='); }
        if (parse(val) is not { } v) return false;
        value = v;
        return true;
    }

    private static long? ParseSize(string s)
    {
        s = s.Trim().ToLowerInvariant();
        int i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        if (i == 0 || !double.TryParse(s[..i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n)) return null;
        long mul = s[i..] switch
        {
            "" or "b" => 1,
            "k" or "kb" => 1L << 10,
            "m" or "mb" => 1L << 20,
            "g" or "gb" => 1L << 30,
            "t" or "tb" => 1L << 40,
            _ => -1,
        };
        return mul < 0 ? null : (long)(n * mul);
    }

    private static long? ParseAgeTicks(string s)
    {
        s = s.Trim().ToLowerInvariant();
        int i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        if (i == 0 || !double.TryParse(s[..i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n)) return null;
        double days = s[i..] switch
        {
            "d" or "day" or "days" => n,
            "w" or "week" or "weeks" => n * 7,
            "m" or "mo" or "month" or "months" => n * 30.44,
            "y" or "year" or "years" => n * 365.25,
            _ => double.NaN,
        };
        return double.IsNaN(days) ? null : (long)(days * 864_000_000_000d);
    }
}
