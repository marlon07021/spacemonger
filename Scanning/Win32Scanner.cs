namespace SpaceMonger.Scanning;

/// <summary>
/// Parallel directory walker built on FindFirstFileEx (basic info + large fetch).
/// The tree is published incrementally so the UI can render it while the scan runs:
/// a directory's Children array is assigned once fully built, and sizes are pushed up
/// the ancestor chain with Interlocked.
/// </summary>
public static unsafe class Win32Scanner
{
    /// <summary>Creates the root node immediately; call <see cref="Run"/> to populate it.</summary>
    public static Node CreateRoot(string path) => new(NormalizeRoot(path), null, true);

    public static void Run(Node root, ScanProgress progress, CancellationToken ct)
    {
        progress.Phase = "Scanning";
        var work = new Stack<(Node node, string path)>();
        var gate = new object();
        int pending = 1;
        int idle = 0;
        work.Push((root, root.Name));

        int workers = Math.Clamp(Environment.ProcessorCount, 2, 32);
        var threads = new Thread[workers];
        for (int t = 0; t < workers; t++)
        {
            threads[t] = new Thread(() =>
            {
                var buffer = new List<Node>(256);
                var subdirs = new List<(Node, string)>(64);
                while (true)
                {
                    (Node node, string path) item;
                    lock (gate)
                    {
                        while (work.Count == 0)
                        {
                            if (pending == 0 || ct.IsCancellationRequested) { Monitor.PulseAll(gate); return; }
                            idle++;
                            Monitor.Wait(gate);
                            idle--;
                        }
                        item = work.Pop();
                    }

                    if (!ct.IsCancellationRequested)
                        ScanDirectory(item.node, item.path, buffer, subdirs, progress);

                    lock (gate)
                    {
                        if (!ct.IsCancellationRequested)
                            foreach (var s in subdirs) work.Push(s);
                        pending += (ct.IsCancellationRequested ? 0 : subdirs.Count) - 1;
                        if (idle > 0) Monitor.PulseAll(gate);
                    }
                    subdirs.Clear();
                }
            }, 256 * 1024) { IsBackground = true, Name = "scan-" + t };
            threads[t].Start();
        }
        foreach (var th in threads) th.Join();
    }

    private static void ScanDirectory(Node dir, string path, List<Node> buffer, List<(Node, string)> subdirs, ScanProgress progress)
    {
        buffer.Clear();
        long bytes = 0, files = 0;
        Native.WIN32_FIND_DATAW data;
        nint h = Native.FindFirstFileEx(ToSearchPattern(path), Native.FindExInfoBasic, &data,
            Native.FindExSearchNameMatch, 0, Native.FIND_FIRST_EX_LARGE_FETCH);
        if (h == -1)
        {
            Interlocked.Increment(ref progress.Errors);
            dir.Children = [];
            return;
        }

        string prefix = path.EndsWith('\\') ? path : path + "\\";
        try
        {
            do
            {
                char* name = data.cFileName;
                if (name[0] == '.' && (name[1] == 0 || (name[1] == '.' && name[2] == 0))) continue;

                bool isDir = (data.dwFileAttributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
                var n = new Node(new string(name), dir, isDir);
                if (isDir)
                {
                    // Junctions and directory symlinks point elsewhere: show them, but don't follow (avoids cycles and double counting).
                    if ((data.dwFileAttributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                        n.Children = [];
                    else
                        subdirs.Add((n, prefix + n.Name));
                }
                else
                {
                    n.Size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                    n.LastWrite = data.ftLastWriteTime;
                    bytes += n.Size;
                    files++;
                }
                buffer.Add(n);
            } while (Native.FindNextFile(h, &data));
        }
        finally
        {
            Native.FindClose(h);
        }

        Volatile.Write(ref dir.Children, buffer.ToArray());
        for (var p = dir; p != null; p = p.Parent)
        {
            Interlocked.Add(ref p.Size, bytes);
            Interlocked.Add(ref p.FileCount, files);
        }
        Interlocked.Add(ref progress.Files, files);
        Interlocked.Increment(ref progress.Directories);
    }

    private static string NormalizeRoot(string path)
    {
        path = Path.GetFullPath(path);
        // Keep "C:\" but strip trailing separators from anything longer.
        return path.Length > 3 ? path.TrimEnd('\\') : path;
    }

    /// <summary>Uses the \\?\ prefix so paths longer than MAX_PATH still work.</summary>
    private static string ToSearchPattern(string path)
    {
        path = path.TrimEnd('\\');
        if (path.StartsWith(@"\\?\")) return path + @"\*";
        if (path.StartsWith(@"\\")) return @"\\?\UNC\" + path[2..] + @"\*";
        return @"\\?\" + path + @"\*";
    }
}
