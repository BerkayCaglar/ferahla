using System.IO.Enumeration;
using System.Runtime.InteropServices;
using Ferahla.Core.Quarantine;

namespace Ferahla.Windows.Quarantine;

/// <summary>
/// Who holds a file open, via Restart Manager. Only called after a move failed (it costs ~1 s per 5000 files).
/// RM accepts files, not folders, so for a folder its files are registered. A process whose working directory
/// is inside a folder (a terminal, Explorer) is invisible to RM (see docs/spikes.md, spike 5).
/// </summary>
internal static unsafe partial class LockInspector
{
    private const int MaxFiles = 20000;
    private const int CCH_RM_SESSION_KEY = 32;
    private const int ERROR_MORE_DATA = 234;

    public static List<LockHolder> WhoIsLocking(string path, bool isDir)
    {
        string[] files;
        if (!isDir) files = [path];
        else
        {
            var list = new List<string>();
            try
            {
                var e = new FileSystemEnumerable<string>(path, (ref FileSystemEntry x) => x.ToFullPath(),
                    new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
                {
                    ShouldIncludePredicate = (ref FileSystemEntry x) => !x.IsDirectory,
                    ShouldRecursePredicate = (ref FileSystemEntry x) => (x.Attributes & FileAttributes.ReparsePoint) == 0,
                };
                foreach (var f in e) { list.Add(f); if (list.Count >= MaxFiles) break; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            files = [.. list];
        }
        if (files.Length == 0) return [];

        var key = stackalloc char[CCH_RM_SESSION_KEY + 1];
        if (RmStartSession(out var h, 0, key) != 0) return [];
        var ptrs = new nint[files.Length];
        try
        {
            for (var i = 0; i < files.Length; i++) ptrs[i] = Marshal.StringToHGlobalUni(files[i]);
            fixed (nint* pp = ptrs)
                if (RmRegisterResources(h, (uint)files.Length, (char**)pp, 0, null, 0, null) != 0) return [];
            uint count = 0;
            var rc = RmGetList(h, out var needed, ref count, null, out _);
            if (rc != ERROR_MORE_DATA || needed == 0) return [];
            var info = new RM_PROCESS_INFO[needed + 4];
            count = (uint)info.Length;
            fixed (RM_PROCESS_INFO* pi = info) rc = RmGetList(h, out needed, ref count, pi, out _);
            if (rc != 0) return [];
            var result = new List<LockHolder>();
            for (var i = 0; i < count; i++)
                fixed (char* n = info[i].strAppName)
                    result.Add(new LockHolder { Pid = info[i].Process.dwProcessId, App = new string(n) });
            return result.DistinctBy(x => x.Pid).ToList();
        }
        finally
        {
            foreach (var p in ptrs) if (p != 0) Marshal.FreeHGlobal(p);
            _ = RmEndSession(h);
        }
    }

    // FILETIME is two DWORDs (4-byte aligned); a long would pad the struct and shift the fields.
    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS { public int dwProcessId; public uint StartLow; public uint StartHigh; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        public fixed char strAppName[256];
        public fixed char strServiceShortName[64];
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        public int bRestartable;
    }

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmStartSession(out uint handle, uint flags, char* sessionKey);
    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmEndSession(uint handle);
    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmRegisterResources(uint handle, uint nFiles, char** files, uint nApps, void* apps, uint nServices, char** services);
    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmGetList(uint handle, out uint needed, ref uint count, RM_PROCESS_INFO* info, out uint rebootReasons);
}
