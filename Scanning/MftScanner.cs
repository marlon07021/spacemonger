using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SpaceMonger.Scanning;

/// <summary>
/// Reads the NTFS Master File Table directly from the raw volume (the WizTree technique).
/// Requires administrator rights and an NTFS volume with a drive letter. Orders of magnitude
/// faster than walking directories because the whole MFT is read sequentially in big chunks
/// and parsed in parallel.
/// </summary>
public static unsafe class MftScanner
{
    private const int RootRecord = 5;
    private const int ChunkBytes = 8 * 1024 * 1024;

    private const byte FlagInUse = 1, FlagDir = 2, FlagDosName = 4, FlagHasName = 8;

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Returns null when the MFT engine can be used for this path, otherwise the reason it can't.</summary>
    public static string? WhyUnsupported(string path)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (root == null || root.Length < 2 || root[1] != ':') return "MFT scanning needs a local drive letter";
        try
        {
            if (!string.Equals(new DriveInfo(root).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                return "MFT scanning only works on NTFS volumes";
        }
        catch (Exception ex) { return ex.Message; }
        if (!IsElevated()) return "MFT scanning requires administrator rights";
        return null;
    }

    /// <summary>Scans the volume that contains <paramref name="path"/> and returns the node for <paramref name="path"/>.</summary>
    public static Node Scan(string path, ScanProgress progress, CancellationToken ct)
    {
        path = Path.GetFullPath(path);
        string driveRoot = Path.GetPathRoot(path)!;           // "C:\"
        string volume = @"\\.\" + driveRoot.TrimEnd('\\');     // "\\.\C:"

        using SafeFileHandle h = Native.CreateFile(volume, Native.GENERIC_READ,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | Native.FILE_SHARE_DELETE, 0, Native.OPEN_EXISTING, 0, 0);
        if (h.IsInvalid) throw new IOException($"Cannot open volume {volume} (error {Marshal.GetLastWin32Error()})");

        Native.NTFS_VOLUME_DATA_BUFFER vd;
        if (!Native.DeviceIoControl(h, Native.FSCTL_GET_NTFS_VOLUME_DATA, null, 0, &vd, sizeof(Native.NTFS_VOLUME_DATA_BUFFER), out _, 0))
            throw new IOException($"FSCTL_GET_NTFS_VOLUME_DATA failed (error {Marshal.GetLastWin32Error()})");

        int recSize = (int)vd.BytesPerFileRecordSegment;
        long cluster = vd.BytesPerCluster;
        long totalRecords = vd.MftValidDataLength / recSize;
        if (totalRecords > int.MaxValue) throw new IOException("MFT too large");
        int count = (int)totalRecords;

        byte* buf = (byte*)NativeMemory.AlignedAlloc(ChunkBytes, 4096);
        byte* buf2 = (byte*)NativeMemory.AlignedAlloc(ChunkBytes, 4096);
        Task parseTask = Task.CompletedTask;
        try
        {
            // 1. Read record 0 ($MFT itself) to learn where the MFT's extents are on disk.
            progress.Phase = "Reading MFT layout";
            int firstRead = (int)Math.Max(cluster, recSize);
            ReadAt(h, buf, firstRead, vd.MftStartLcn * cluster);
            if (!ApplyFixups(buf, recSize)) throw new IOException("MFT record 0 is corrupt");
            var extents = ParseMftExtents(buf);
            long extentBytes = 0;
            foreach (var e in extents) extentBytes += e.clusters * cluster;
            if (extentBytes < vd.MftValidDataLength)
                throw new IOException("MFT is too fragmented (attribute list); use the Win32 engine");

            // 2. Stream the MFT in big chunks, parsing each chunk in parallel while the next one is read.
            var names = new string?[count];
            var parents = new int[count];
            var sizes = new long[count];
            var flags = new byte[count];
            var times = new long[count];
            var extensionRecords = new ConcurrentBag<(int baseRecord, byte[] data)>();

            progress.Phase = "Reading MFT";
            long recordIndex = 0;
            foreach (var (lcn, clusters) in extents)
            {
                long offset = lcn * cluster;
                long remaining = clusters * cluster;
                while (remaining > 0 && recordIndex < count)
                {
                    ct.ThrowIfCancellationRequested();
                    int toRead = (int)Math.Min(ChunkBytes, remaining);
                    int records = (int)Math.Min(toRead / recSize, count - recordIndex);
                    ReadAt(h, buf, toRead, offset);

                    parseTask.Wait(ct);
                    nint chunk = (nint)buf;
                    int first = (int)recordIndex;
                    parseTask = Task.Run(() => ParseChunk(chunk, first, records, recSize, names, parents, sizes, flags, times, extensionRecords));
                    byte* tmp = buf; buf = buf2; buf2 = tmp;

                    offset += toRead;
                    remaining -= toRead;
                    recordIndex += records;
                    progress.Fraction = (double)recordIndex / count;
                    progress.Files = recordIndex;
                }
            }
            parseTask.Wait(ct);

            // Attributes that overflowed into extension records belong to their base record.
            foreach (var (baseRec, data) in extensionRecords)
                fixed (byte* p = data)
                    ParseAttributes(p, baseRec, names, parents, sizes, flags, times);

            // 3. Build the tree.
            progress.Phase = "Building tree";
            ct.ThrowIfCancellationRequested();
            var root = BuildTree(driveRoot, count, names, parents, sizes, flags, times, progress);
            return FindSubtree(root, path);
        }
        finally
        {
            // Never free a buffer that a parse task may still be reading.
            try { parseTask.Wait(); } catch { }
            NativeMemory.AlignedFree(buf);
            NativeMemory.AlignedFree(buf2);
        }
    }

    private static void ReadAt(SafeFileHandle h, byte* buffer, int length, long offset)
    {
        var ov = new NativeOverlapped { OffsetLow = (int)offset, OffsetHigh = (int)(offset >> 32) };
        if (!Native.ReadFile(h, buffer, length, out int read, &ov) || read != length)
            throw new IOException($"Volume read failed at {offset} (error {Marshal.GetLastWin32Error()})");
    }

    /// <summary>Replaces the update-sequence placeholders at the end of each sector with the real bytes.</summary>
    private static bool ApplyFixups(byte* rec, int recSize)
    {
        if (*(uint*)rec != 0x454C4946) return false; // "FILE"
        int usaOfs = *(ushort*)(rec + 4);
        int usaCount = *(ushort*)(rec + 6);
        if (usaCount < 2 || usaOfs + usaCount * 2 > recSize) return false;
        int stride = recSize / (usaCount - 1);
        ushort usn = *(ushort*)(rec + usaOfs);
        for (int i = 1; i < usaCount; i++)
        {
            ushort* end = (ushort*)(rec + i * stride - 2);
            if (*end != usn) return false;
            *end = *(ushort*)(rec + usaOfs + i * 2);
        }
        return true;
    }

    private static List<(long lcn, long clusters)> ParseMftExtents(byte* rec)
    {
        var result = new List<(long, long)>();
        byte* a = rec + *(ushort*)(rec + 0x14);
        while (*(uint*)a != 0xFFFFFFFF)
        {
            uint type = *(uint*)a;
            uint len = *(uint*)(a + 4);
            if (len == 0) break;
            if (type == 0x80 && a[8] != 0 && a[9] == 0)
            {
                byte* run = a + *(ushort*)(a + 0x20);
                long lcn = 0;
                while (*run != 0)
                {
                    int lenBytes = *run & 0xF, offBytes = *run >> 4;
                    run++;
                    long clusters = ReadLE(run, lenBytes, signed: false);
                    run += lenBytes;
                    if (offBytes == 0) continue; // sparse run; never happens for $MFT
                    lcn += ReadLE(run, offBytes, signed: true);
                    run += offBytes;
                    result.Add((lcn, clusters));
                }
                break;
            }
            a += len;
        }
        if (result.Count == 0) throw new IOException("Could not locate $MFT data runs");
        return result;
    }

    private static long ReadLE(byte* p, int n, bool signed)
    {
        long v = 0;
        for (int i = 0; i < n; i++) v |= (long)p[i] << (8 * i);
        if (signed && n > 0 && n < 8 && (p[n - 1] & 0x80) != 0) v |= -1L << (8 * n);
        return v;
    }

    private static void ParseChunk(nint chunk, int firstRecord, int records, int recSize,
        string?[] names, int[] parents, long[] sizes, byte[] flags, long[] times, ConcurrentBag<(int, byte[])> extensions)
    {
        Parallel.ForEach(Partitioner.Create(0, records, 2048), range =>
        {
            for (int i = range.Item1; i < range.Item2; i++)
            {
                byte* rec = (byte*)chunk + (long)i * recSize;
                if (!ApplyFixups(rec, recSize)) continue;
                ushort recFlags = *(ushort*)(rec + 0x16);
                if ((recFlags & 1) == 0) continue; // not in use
                long baseRef = *(long*)(rec + 0x20) & 0xFFFFFFFFFFFF;
                if (baseRef != 0)
                {
                    if (baseRef < names.Length)
                        extensions.Add(((int)baseRef, new ReadOnlySpan<byte>(rec, recSize).ToArray()));
                    continue;
                }
                int index = firstRecord + i;
                flags[index] = (byte)(FlagInUse | ((recFlags & 2) != 0 ? FlagDir : 0));
                ParseAttributes(rec, index, names, parents, sizes, flags, times);
            }
        });
    }

    private static void ParseAttributes(byte* rec, int index, string?[] names, int[] parents, long[] sizes, byte[] flags, long[] times)
    {
        int bytesInUse = *(int*)(rec + 0x18);
        byte* end = rec + bytesInUse;
        byte* a = rec + *(ushort*)(rec + 0x14);
        while (a + 8 <= end && *(uint*)a != 0xFFFFFFFF)
        {
            uint type = *(uint*)a;
            uint len = *(uint*)(a + 4);
            if (len == 0 || a + len > end) break;
            bool nonResident = a[8] != 0;
            int nameLen = a[9];

            if (type == 0x10 && !nonResident) // $STANDARD_INFORMATION
            {
                times[index] = *(long*)(a + *(ushort*)(a + 0x14) + 0x08); // last modification time
            }
            else if (type == 0x30 && !nonResident) // $FILE_NAME
            {
                byte* v = a + *(ushort*)(a + 0x14);
                byte ns = v[0x41];
                bool isDos = ns == 2;
                byte f = flags[index];
                bool haveName = (f & FlagHasName) != 0;
                // Prefer Win32/POSIX names over the 8.3 DOS alias.
                if (!haveName || ((f & FlagDosName) != 0 && !isDos))
                {
                    int chars = v[0x40];
                    names[index] = new string((char*)(v + 0x42), 0, chars);
                    parents[index] = (int)(*(long*)v & 0xFFFFFFFFFFFF);
                    flags[index] = (byte)((f & ~FlagDosName) | FlagHasName | (isDos ? FlagDosName : 0));
                }
            }
            else if (type == 0x80 && nameLen == 0) // unnamed $DATA stream
            {
                if (!nonResident)
                    sizes[index] = *(uint*)(a + 0x10);
                else if (*(long*)(a + 0x10) == 0) // lowest VCN 0 holds the real size
                    sizes[index] = *(long*)(a + 0x30);
            }
            a += len;
        }
    }

    private static Node BuildTree(string driveRoot, int count, string?[] names, int[] parents, long[] sizes, byte[] flags, long[] times, ScanProgress progress)
    {
        var nodes = new Node?[count];
        for (int i = 0; i < count; i++)
        {
            if ((flags[i] & FlagInUse) == 0) continue;
            if (i == RootRecord) nodes[i] = new Node(driveRoot, null, true);
            else if (names[i] != null) nodes[i] = new Node(names[i]!, null, (flags[i] & FlagDir) != 0);
        }
        var root = nodes[RootRecord] ?? throw new IOException("Root directory record missing");

        var childCount = new int[count];
        for (int i = 0; i < count; i++)
        {
            if (nodes[i] == null || i == RootRecord) continue;
            int p = parents[i];
            if ((uint)p < (uint)count && p != i && nodes[p] is { IsDirectory: true }) childCount[p]++;
            else nodes[i] = null; // orphan
        }
        for (int i = 0; i < count; i++)
            if (nodes[i] is { IsDirectory: true } d) d.Children = new Node[childCount[i]];
        Array.Clear(childCount);
        for (int i = 0; i < count; i++)
        {
            var n = nodes[i];
            if (n == null || i == RootRecord) continue;
            var parent = nodes[parents[i]]!;
            n.Parent = parent;
            parent.Children![childCount[parents[i]]++] = n;
            if (!n.IsDirectory) { n.Size = sizes[i]; n.LastWrite = times[i]; }
        }

        // Aggregate sizes bottom-up: collect directories in pre-order, then walk the list backwards.
        var order = new List<Node>();
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            order.Add(n);
            foreach (var c in n.Children!)
                if (c.IsDirectory) stack.Push(c);
        }
        for (int i = order.Count - 1; i >= 0; i--)
        {
            var d = order[i];
            long size = 0, fc = 0;
            foreach (var c in d.Children!)
            {
                size += c.Size;
                if (c.IsDirectory) fc += c.FileCount; else fc++;
            }
            d.Size = size;
            d.FileCount = fc;
        }
        progress.Files = root.FileCount;
        progress.Directories = order.Count;
        return root;
    }

    private static Node FindSubtree(Node root, string path)
    {
        string rel = path[Path.GetPathRoot(path)!.Length..].TrimEnd('\\');
        if (rel.Length == 0) return root;
        Node cur = root;
        foreach (var part in rel.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = cur.Children?.FirstOrDefault(c => c.IsDirectory && string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next == null) throw new DirectoryNotFoundException(path);
            cur = next;
        }
        cur.Parent = null;       // detach so the rest of the volume can be collected
        cur.Name = path.TrimEnd('\\');
        return cur;
    }
}
