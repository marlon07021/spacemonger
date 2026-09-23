namespace SpaceMonger.Analysis;

public enum FileCategory
{
    Video, Audio, Image, Document, Archive, DiskImage, Code, Binary,
    BuildArtifact, Database, Log, Temp, Font, Other,
}

public static class FileCategories
{
    public const int Count = (int)FileCategory.Other + 1;

    private static readonly Dictionary<string, FileCategory> Map = Build();

    private static Dictionary<string, FileCategory> Build()
    {
        var m = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory c, string exts)
        {
            foreach (var e in exts.Split(' ', StringSplitOptions.RemoveEmptyEntries)) m[e] = c;
        }
        Add(FileCategory.Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg m2ts mts vob 3gp");
        Add(FileCategory.Audio, "mp3 flac wav aac ogg m4a wma opus aiff aif alac mid midi");
        Add(FileCategory.Image, "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 cr3 nef arw dng psd svg ico avif");
        Add(FileCategory.Document, "pdf doc docx xls xlsx ppt pptx odt ods odp txt rtf md csv epub mobi one");
        Add(FileCategory.Archive, "zip rar 7z tar gz tgz bz2 xz zst cab nupkg whl");
        Add(FileCategory.DiskImage, "iso img vhd vhdx vmdk vdi qcow2 wim esd dmg avhdx");
        Add(FileCategory.Code, "cs c cpp cc h hpp js mjs ts tsx jsx py java kt go rs rb php swift sql html htm css scss json xml yaml yml toml sh ps1 psm1 bat cmd lua dart vue sln csproj vcxproj ipynb");
        Add(FileCategory.Binary, "exe dll sys so dylib msi msix msixbundle appx bin ocx drv mui node jar winmd");
        Add(FileCategory.BuildArtifact, "obj o pdb lib a class pyc pyo ilk idb ipch pch tlog iobj ipdb exp");
        Add(FileCategory.Database, "db sqlite sqlite3 mdf ldf ndf accdb mdb edb ldb realm");
        Add(FileCategory.Log, "log etl evtx dmp mdmp hdmp trace blf");
        Add(FileCategory.Temp, "tmp temp bak old cache crdownload part partial swp chk download");
        Add(FileCategory.Font, "ttf otf woff woff2 fon ttc");
        return m;
    }

    /// <summary>Hand-authored or irreplaceable content: real source code, office documents/PDFs, media.</summary>
    private static readonly HashSet<string> PreciousExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs", "c", "cpp", "cc", "h", "hpp", "js", "ts", "tsx", "jsx", "py", "java", "kt", "go", "rs", "rb", "php", "swift",
        "vue", "dart", "lua", "sql", "ipynb",
        "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "md", "epub", "psd",
    };

    public static bool IsPrecious(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1) return false;
        string ext = fileName[(dot + 1)..];
        if (PreciousExt.Contains(ext)) return true;
        return Map.TryGetValue(ext, out var c) && c is FileCategory.Image or FileCategory.Video or FileCategory.Audio;
    }

    public static FileCategory Of(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1) return FileCategory.Other;
        return Map.TryGetValue(fileName[(dot + 1)..], out var c) ? c : FileCategory.Other;
    }

    public static string Extension(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot <= 0 || dot == fileName.Length - 1 ? "(none)" : fileName[dot..].ToLowerInvariant();
    }

    /// <summary>Display colour per category (used by the treemap "File type" mode and the legend).</summary>
    public static Color ColorOf(FileCategory c) => c switch
    {
        FileCategory.Video => Color.FromArgb(0xE0, 0x4F, 0x5F),
        FileCategory.Audio => Color.FromArgb(0xF2, 0x8C, 0x38),
        FileCategory.Image => Color.FromArgb(0xF2, 0xC9, 0x4C),
        FileCategory.Document => Color.FromArgb(0x6C, 0xC0, 0x6A),
        FileCategory.Archive => Color.FromArgb(0x9B, 0x6B, 0xD6),
        FileCategory.DiskImage => Color.FromArgb(0xC0, 0x5C, 0xC8),
        FileCategory.Code => Color.FromArgb(0x4A, 0xB8, 0xD8),
        FileCategory.Binary => Color.FromArgb(0x4C, 0x7C, 0xE0),
        FileCategory.BuildArtifact => Color.FromArgb(0xD4, 0x9A, 0x3C),
        FileCategory.Database => Color.FromArgb(0x2E, 0xA8, 0x8E),
        FileCategory.Log => Color.FromArgb(0x8C, 0x6A, 0x4A),
        FileCategory.Temp => Color.FromArgb(0xA0, 0x60, 0x50),
        FileCategory.Font => Color.FromArgb(0xD8, 0x7C, 0xA8),
        _ => Color.FromArgb(0x70, 0x70, 0x78),
    };
}
