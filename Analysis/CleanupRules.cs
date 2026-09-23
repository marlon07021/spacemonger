namespace SpaceMonger.Analysis;

public enum FolderClass
{
    // Reclaimable
    Cache, Temp, BuildOutput, Dependencies, Logs, Installers, RecycleBin,
    // Keep
    Media, Documents, SourceCode, Application, System, UserKeep,
}

public enum Safety
{
    /// <summary>Disposable; nothing depends on it.</summary>
    Safe,
    /// <summary>Rebuilt automatically or by a build/restore command.</summary>
    Regenerable,
    /// <summary>Probably unneeded, but look before deleting.</summary>
    Review,
    /// <summary>Deleting can break repair/uninstall or similar.</summary>
    Caution,
}

public readonly record struct Verdict(FolderClass Class, Safety Safety, string Reason);

/// <summary>
/// Hand-written, high-precision rules. They produce the confident verdicts shown as "Rule" in the
/// cleanup list, and double as weak labels for training the <see cref="FolderClassifier"/>.
/// </summary>
public static class CleanupRules
{
    public static bool IsReclaimable(FolderClass c) => c <= FolderClass.RecycleBin;

    private static readonly HashSet<string> CacheNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cache", ".cache", "caches", "gpucache", "code cache", "shadercache", "dxcache", "glcache", "inetcache",
        "cache_data", "cachestorage", "cacheddata", "d3dscache", "grshadercache", "dawncache", "graphitedawncache",
        "cachedfiles", "webcache", "thumbnails", "crashpad", "npm-cache", ".parcel-cache", ".turbo",
    };

    private static readonly string[] ProjectMarkers = [".csproj", ".vbproj", ".fsproj", ".vcxproj", ".sln"];

    /// <summary>Areas where automatic (ML) suggestions are never made.</summary>
    public static bool IsProtected(string lowerPath)
    {
        string rel = lowerPath.Length > 2 && lowerPath[1] == ':' ? lowerPath[2..] : lowerPath;
        return rel is "" or @"\" ||
               rel.StartsWith(@"\windows") || rel.StartsWith(@"\program files") ||
               rel.StartsWith(@"\programdata\microsoft") || rel.StartsWith(@"\system volume information") ||
               rel.Contains(@"\appdata\roaming\microsoft\") ||
               (rel.StartsWith(@"\users\") && rel.Count(c => c == '\\') <= 2); // a user profile root
    }

    public static Verdict? Classify(Node dir, string p, DirStats s)
    {
        string n = dir.Name.ToLowerInvariant();
        string parent = dir.Parent?.FullPath.ToLowerInvariant() ?? "";
        long size = Math.Max(1, dir.Size);

        // ---- reclaimable
        if (n == "$recycle.bin") return new(FolderClass.RecycleBin, Safety.Safe, "Recycle Bin contents");
        if (n is "windows.old" or "$windows.~bt" or "$windows.~ws")
            return new(FolderClass.Installers, Safety.Review, "Previous Windows installation / upgrade files (remove with Disk Cleanup)");
        if (p.EndsWith(@"\softwaredistribution\download"))
            return new(FolderClass.Installers, Safety.Safe, "Windows Update download cache");
        if (n == "package cache" && parent.EndsWith(@"\programdata"))
            return new(FolderClass.Installers, Safety.Caution, "Installer cache — needed to repair/uninstall programs");
        if (n == "temp" && (parent.EndsWith(@"\appdata\local") || parent.EndsWith(@"\windows")))
            return new(FolderClass.Temp, Safety.Safe, "System/user temporary files");
        if ((n is "tmp" or "temp") && s.Fraction(size, FileCategory.Temp, FileCategory.Log) > 0.3)
            return new(FolderClass.Temp, Safety.Review, "Temporary folder");

        if (n == "node_modules") return new(FolderClass.Dependencies, Safety.Regenerable, "npm packages (restore with npm install)");
        if (p.EndsWith(@"\.nuget\packages")) return new(FolderClass.Dependencies, Safety.Regenerable, "NuGet package cache (restored on build)");
        if (p.EndsWith(@"\.m2\repository")) return new(FolderClass.Dependencies, Safety.Regenerable, "Maven repository cache");
        if (n == ".gradle" || p.EndsWith(@"\.gradle\caches")) return new(FolderClass.Dependencies, Safety.Regenerable, "Gradle cache");
        if ((n is ".venv" or "venv") && HasChild(dir, "pyvenv.cfg")) return new(FolderClass.Dependencies, Safety.Regenerable, "Python virtual environment");
        if (p.Contains(@"\appdata\local\pip\cache") || p.EndsWith(@"\yarn\cache") || p.EndsWith(@"\pnpm\store"))
            return new(FolderClass.Cache, Safety.Regenerable, "Package manager download cache");

        if ((n is "bin" or "obj") && SiblingHas(dir, name => ProjectMarkers.Any(name.EndsWith)))
            return new(FolderClass.BuildOutput, Safety.Regenerable, ".NET/C++ build output (rebuilt on build)");
        if (n == "target" && SiblingHas(dir, name => name is "cargo.toml" or "pom.xml"))
            return new(FolderClass.BuildOutput, Safety.Regenerable, "Rust/Maven build output");
        if (n.StartsWith("cmake-build-") || n == "cmakefiles")
            return new(FolderClass.BuildOutput, Safety.Regenerable, "CMake build tree");
        if (n == "build" && SiblingHas(dir, name => name is "build.gradle" or "build.gradle.kts" or "cmakelists.txt"))
            return new(FolderClass.BuildOutput, Safety.Regenerable, "Gradle/CMake build output");
        if (n is "dist" or ".next" or ".nuxt" or ".angular" or ".svelte-kit" && SiblingHas(dir, name => name == "package.json"))
            return new(FolderClass.BuildOutput, Safety.Regenerable, "JavaScript build output");
        if (n == ".vs" && SiblingHas(dir, name => name.EndsWith(".sln")))
            return new(FolderClass.Cache, Safety.Regenerable, "Visual Studio local cache (IntelliSense DB, recreated)");
        if (n is "__pycache__" or ".pytest_cache" or ".mypy_cache" or "deriveddata" or "ipch")
            return new(FolderClass.BuildOutput, Safety.Regenerable, "Compiler/tooling cache");

        if (n is "crashdumps" or "minidump" or "livekernelreports")
            return new(FolderClass.Logs, Safety.Safe, "Crash dumps");
        if ((n is "logs" or "log") && s.Fraction(FileCategory.Log, size) >= 0.5)
            return new(FolderClass.Logs, p.Contains(@"\windows\") ? Safety.Review : Safety.Safe, "Log files");

        if (CacheNames.Contains(n) && !p.Contains(@"\windows\"))
            return new(FolderClass.Cache, Safety.Regenerable, "Application cache (recreated automatically)");

        if (n == "downloads" && s.Fraction(size, FileCategory.Binary, FileCategory.Archive, FileCategory.DiskImage) >= 0.6)
            return new(FolderClass.Installers, Safety.Review, "Mostly installers/archives/disk images");

        // ---- keep (used as negative training examples)
        if (n is ".git" or ".svn" or ".hg")
            return new(FolderClass.SourceCode, Safety.Caution, "Version control history");
        if (n == "bin")
            return new(FolderClass.Application, Safety.Caution, "Program binaries (no project file next to it)");
        string rel = p.Length > 2 && p[1] == ':' ? p[2..] : p;
        if (rel.StartsWith(@"\windows") || rel.StartsWith(@"\system volume information"))
            return new(FolderClass.System, Safety.Caution, "Operating system");
        if (s.Files >= 5 && s.Fraction(size, FileCategory.Video, FileCategory.Audio, FileCategory.Image) >= 0.8)
            return new(FolderClass.Media, Safety.Caution, "Photos/music/video");
        if (s.Files >= 5 && s.Fraction(FileCategory.Document, size) >= 0.6)
            return new(FolderClass.Documents, Safety.Caution, "Documents");
        if (s.Files >= 5 && s.Fraction(FileCategory.Code, size) >= 0.5)
            return new(FolderClass.SourceCode, Safety.Caution, "Source code");
        if (rel.StartsWith(@"\program files") || s.Fraction(FileCategory.Binary, size) >= 0.6)
            return new(FolderClass.Application, Safety.Caution, "Installed application");
        return null;
    }

    private static bool HasChild(Node dir, string name) =>
        dir.Children?.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) == true;

    private static bool SiblingHas(Node dir, Func<string, bool> match) =>
        dir.Parent?.Children?.Any(c => !c.IsDirectory && match(c.Name.ToLowerInvariant())) == true;
}
