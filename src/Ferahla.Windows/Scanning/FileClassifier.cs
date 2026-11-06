using Ferahla.Core;
using Ferahla.Core.Model;

namespace Ferahla.Windows.Scanning;

public enum FileExt : byte
{
    None,
    Installer,      // .exe .msi .msix .msixbundle .appx .appxbundle
    Archive,        // .zip .rar .7z .tar .gz .tgz .xz .bz2 .iso .img .r00…
    Partial,        // .crdownload .part .partial .fdmdownload .download .opdownload
}

/// <summary>Where a directory sits, decided once per directory while descending.</summary>
[Flags]
public enum PathContext
{
    None = 0,
    /// <summary>Under the user profile but not in AppData or a dot-folder: the user's own files.</summary>
    UserArea = 1 << 0,
    /// <summary>Downloads or Desktop subtree.</summary>
    DownloadsOrDesktop = 1 << 1,
    UserProfile = 1 << 2,
}

/// <summary>Decides which files are worth keeping in the snapshot without allocating per file.</summary>
public sealed class FileClassifier(IReadOnlyDictionary<string, string> known, long bigFileBytes)
{
    private readonly string? _profile = Get(known, "UserProfile");
    private readonly string[] _dlDesk = new[] { Get(known, "Downloads"), Get(known, "Desktop") }.OfType<string>().ToArray();
    private readonly string[] _notUser = new[] { Get(known, "LocalAppData"), Get(known, "AppData") }.OfType<string>().ToArray();

    public long BigFileBytes { get; } = bigFileBytes;

    private static string? Get(IReadOnlyDictionary<string, string> d, string k) =>
        d.TryGetValue(k, out var v) && v.Length > 0 ? PathUtil.Normalize(v) : null;

    public PathContext ContextFor(string path, PathContext? parent)
    {
        var c = PathContext.None;
        if (_profile is not null && PathUtil.IsUnder(path, _profile))
        {
            c |= PathContext.UserProfile;
            var inAppData = _notUser.Any(a => PathUtil.IsUnder(path, a));
            var dotFolder = !inAppData && IsDotFolderBelowProfile(path);
            if (!inAppData && !dotFolder) c |= PathContext.UserArea;
        }
        // Outside the profile (other drives, D:\Downloads, D:\Projects) everything but system and program folders is the
        // user's own, as in DetectorContext.IsUserArea.
        else if (!_system.Any(s => PathUtil.IsUnder(path, s)) && !IsSystemRootFolder(path)) c |= PathContext.UserArea;
        if (_dlDesk.Any(d => PathUtil.IsUnder(path, d))) c |= PathContext.DownloadsOrDesktop;
        return c;
    }

    private readonly string[] _system = new[] { Get(known, "Windows"), Get(known, "ProgramFiles"), Get(known, "ProgramFilesX86"), Get(known, "ProgramData") }
        .OfType<string>().ToArray();

    private static bool IsSystemRootFolder(string path)
    {
        var n = PathUtil.Normalize(path);
        if (n.Length <= 3) return false;
        var i = n.IndexOf('\\', 3);
        var first = i < 0 ? n[3..] : n[3..i];
        return first.StartsWith('$') || first.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
               || first.Equals("Recovery", StringComparison.OrdinalIgnoreCase) || first.Equals("Users", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsDotFolderBelowProfile(string path)
    {
        if (path.Length <= _profile!.Length + 1) return false;
        var rest = path.AsSpan(_profile.Length + 1);
        var first = rest.IndexOf('\\') is var i and >= 0 ? rest[..i] : rest;
        return first.Length > 1 && first[0] == '.';
    }

    /// <summary>Flags that depend only on the file itself.</summary>
    public static FileFlags Intrinsic(FileExt ext, long length, long bigFileBytes)
    {
        var f = FileFlags.None;
        if (length >= bigFileBytes) f |= FileFlags.Big;
        if (ext == FileExt.Partial) f |= FileFlags.PartialDownload;
        return f;
    }

    /// <summary>Flags that depend on where the file is.</summary>
    public FileFlags Contextual(FileExt ext, PathContext ctx, long length) => ext switch
    {
        FileExt.Installer when (ctx & PathContext.DownloadsOrDesktop) != 0 && length >= 256 * 1024 => FileFlags.Installer,
        FileExt.Archive when (ctx & PathContext.UserArea) != 0 && length >= 1 << 20 => FileFlags.Archive,
        _ => FileFlags.None,
    };

    public static FileExt Classify(ReadOnlySpan<char> name, out DirMarkers marker)
    {
        marker = Marker(name);
        var dot = name.LastIndexOf('.');
        if (dot < 0) return FileExt.None;
        var ext = name[(dot + 1)..];
        if (ext.Length is 0 or > 11) return FileExt.None;
        Span<char> lower = stackalloc char[ext.Length];
        ext.ToLowerInvariant(lower);
        return lower switch
        {
            "exe" or "msi" or "msix" or "msixbundle" or "appx" or "appxbundle" => FileExt.Installer,
            "zip" or "rar" or "7z" or "tar" or "gz" or "tgz" or "xz" or "bz2" or "iso" or "img" or "cab" => FileExt.Archive,
            "crdownload" or "part" or "partial" or "fdmdownload" or "download" or "opdownload" => FileExt.Partial,
            _ when lower.Length == 3 && lower[0] == 'r' && char.IsAsciiDigit(lower[1]) && char.IsAsciiDigit(lower[2]) => FileExt.Archive, // .r00 split rar
            _ when lower.Length == 3 && char.IsAsciiDigit(lower[0]) && char.IsAsciiDigit(lower[1]) && char.IsAsciiDigit(lower[2]) && IsSplitArchive(name[..dot]) => FileExt.Archive, // x.7z.001
            _ => FileExt.None,
        };
    }

    private static bool IsSplitArchive(ReadOnlySpan<char> stem) =>
        stem.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) || stem.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || stem.EndsWith(".rar", StringComparison.OrdinalIgnoreCase);

    private static DirMarkers Marker(ReadOnlySpan<char> name)
    {
        if (name.Equals("Cargo.toml", StringComparison.Ordinal)) return DirMarkers.CargoToml;
        if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase)) return DirMarkers.PackageJson;
        if (name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase)) return DirMarkers.PyProject;
        if (name.Equals("go.mod", StringComparison.OrdinalIgnoreCase)) return DirMarkers.GoMod;
        if (name.Equals("build.gradle", StringComparison.OrdinalIgnoreCase) || name.Equals("build.gradle.kts", StringComparison.OrdinalIgnoreCase)) return DirMarkers.Gradle;
        if (name.Equals("pom.xml", StringComparison.OrdinalIgnoreCase)) return DirMarkers.PomXml;
        if (name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase)) return DirMarkers.CMakeLists;
        if (name.Equals("pubspec.yaml", StringComparison.OrdinalIgnoreCase)) return DirMarkers.Pubspec;
        if (name.Equals("composer.json", StringComparison.OrdinalIgnoreCase)) return DirMarkers.ComposerJson;
        if (name.Equals("mix.exs", StringComparison.OrdinalIgnoreCase)) return DirMarkers.MixExs;
        if (name.Equals("stack.yaml", StringComparison.OrdinalIgnoreCase)) return DirMarkers.StackYaml;
        if (name.Equals("build.zig", StringComparison.OrdinalIgnoreCase)) return DirMarkers.BuildZig;
        if (name.Equals("project.godot", StringComparison.OrdinalIgnoreCase)) return DirMarkers.GodotProject;
        if (name.Equals(".git", StringComparison.Ordinal)) return DirMarkers.Git; // worktree/submodule: .git is a file
        if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return DirMarkers.CsProj;
        if (name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)) return DirMarkers.FsProj;
        if (name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)) return DirMarkers.VbProj;
        if (name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)) return DirMarkers.Sln;
        if (name.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase)) return DirMarkers.UnrealProject;
        return DirMarkers.None;
    }
}
