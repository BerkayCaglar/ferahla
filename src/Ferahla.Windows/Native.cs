using System.Runtime.InteropServices;

namespace Ferahla.Windows;

internal static unsafe partial class Native
{
    private const uint INVALID_FILE_SIZE = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetCompressedFileSize(string fileName, out uint high);

    /// <summary>Allocated bytes for sparse/compressed files; -1 on failure.</summary>
    public static long GetAllocatedSize(string path)
    {
        var low = GetCompressedFileSize(LongPath(path), out var high);
        if (low == INVALID_FILE_SIZE && Marshal.GetLastPInvokeError() != 0) return -1;
        return ((long)high << 32) | low;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out char* path);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(void* p);

    public static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, 0, out var p) != 0) return null;
        try { return new string(p); }
        finally { CoTaskMemFree(p); }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existing, string target, uint flags);

    /// <summary>
    /// Renames a file or folder within one volume. No copy fallback (unlike <see cref="File.Move(string, string)"/>),
    /// never replaces an existing target. Returns 0 or the Win32 error code.
    /// </summary>
    public static int Rename(string from, string to) =>
        MoveFileEx(ExtendedPath(from), ExtendedPath(to), 0) ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>Always <c>\\?\</c>-prefixed: no MAX_PATH limit, no reinterpretation of the (already full) path.</summary>
    public static string ExtendedPath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    /// <summary>Win32 APIs that don't go through .NET need the \\?\ prefix for paths over MAX_PATH.</summary>
    public static string LongPath(string path) =>
        path.Length < 248 || path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
}
