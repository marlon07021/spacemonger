using System.Buffers;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpaceMonger.Analysis;

public sealed record DuplicateGroup(long Size, List<Node> Files)
{
    public long Wasted => Size * (Files.Count - 1);
}

/// <summary>
/// Finds identical files in three narrowing passes so most files are never read:
/// 1) group by exact size (free — from the scan), 2) hash the first+last 64 KB,
/// 3) full XxHash128 of the survivors. Hard links (same file ID) are not reported as duplicates.
/// </summary>
public static partial class DuplicateFinder
{
    private const int EdgeBytes = 64 * 1024;

    public static async Task<List<DuplicateGroup>> FindAsync(Node root, long minSize, IProgress<string> progress, CancellationToken ct)
    {
        // Pass 1: by size.
        progress.Report("Grouping by size…");
        var bySize = new Dictionary<long, List<Node>>();
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.Children == null) continue;
            foreach (var c in n.Children)
            {
                if (c.IsDirectory) stack.Push(c);
                else if (c.Size >= minSize)
                {
                    if (!bySize.TryGetValue(c.Size, out var l)) bySize[c.Size] = l = new List<Node>(2);
                    l.Add(c);
                }
            }
        }
        var groups = bySize.Where(kv => kv.Value.Count > 1).Select(kv => kv.Value).ToList();
        ct.ThrowIfCancellationRequested();

        // Pass 2: edges. Pass 3: full content (only needed when the file is bigger than both edges).
        int total = groups.Sum(g => g.Count), done = 0;
        var partial = await RefineAsync(groups, full: false, () => progress.Report($"Quick-hashing {Interlocked.Increment(ref done):N0} / {total:N0} candidates…"), ct);
        total = partial.Where(g => g[0].Size > 2 * EdgeBytes).Sum(g => g.Count);
        done = 0;
        var small = partial.Where(g => g[0].Size <= 2 * EdgeBytes);
        var fullHashed = await RefineAsync(partial.Where(g => g[0].Size > 2 * EdgeBytes).ToList(), full: true,
            () => progress.Report($"Verifying {Interlocked.Increment(ref done):N0} / {total:N0} files…"), ct);

        return small.Concat(fullHashed)
            .Select(g => new DuplicateGroup(g[0].Size, g))
            .OrderByDescending(g => g.Wasted)
            .ToList();
    }

    private static async Task<List<List<Node>>> RefineAsync(List<List<Node>> groups, bool full, Action tick, CancellationToken ct)
    {
        var result = new List<List<Node>>();
        var gate = new object();
        await Parallel.ForEachAsync(groups, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, (group, token) =>
        {
            var byHash = new Dictionary<UInt128, List<Node>>();
            var seenIds = new HashSet<(uint, ulong)>();
            foreach (var file in group)
            {
                token.ThrowIfCancellationRequested();
                tick();
                if (TryHash(file, full, out var hash, out var id) && seenIds.Add(id))
                {
                    if (!byHash.TryGetValue(hash, out var l)) byHash[hash] = l = new List<Node>(2);
                    l.Add(file);
                }
            }
            lock (gate)
                foreach (var l in byHash.Values)
                    if (l.Count > 1) result.Add(l);
            return ValueTask.CompletedTask;
        });
        return result;
    }

    private static bool TryHash(Node file, bool full, out UInt128 hash, out (uint volume, ulong index) id)
    {
        hash = default;
        id = default;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            using var h = File.OpenHandle(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                full ? FileOptions.SequentialScan : FileOptions.RandomAccess);
            if (GetFileInformationByHandle(h, out var info))
                id = (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
            else
                id = (0, (ulong)file.GetHashCode());

            var hasher = new XxHash128();
            long length = RandomAccess.GetLength(h);
            if (!full || length <= 2 * EdgeBytes)
            {
                int n = RandomAccess.Read(h, buffer.AsSpan(0, (int)Math.Min(EdgeBytes, length)), 0);
                hasher.Append(buffer.AsSpan(0, n));
                if (length > EdgeBytes)
                {
                    long tail = Math.Max(EdgeBytes, length - EdgeBytes);
                    n = RandomAccess.Read(h, buffer.AsSpan(0, (int)(length - tail)), tail);
                    hasher.Append(buffer.AsSpan(0, n));
                }
            }
            else
            {
                long offset = 0;
                int n;
                while ((n = RandomAccess.Read(h, buffer, offset)) > 0)
                {
                    hasher.Append(buffer.AsSpan(0, n));
                    offset += n;
                }
            }
            hash = hasher.GetCurrentHashAsUInt128();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // locked or inaccessible: can't prove it's a duplicate
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)] // FILETIMEs are DWORD-aligned
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);
}
