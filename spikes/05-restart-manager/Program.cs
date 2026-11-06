// Spike 05: can Restart Manager find file locks, and locks inside a folder?
// Results: docs/spikes.md
using System.Diagnostics;
using System.Runtime.InteropServices;

var root = Path.Combine(Path.GetTempPath(), $"ferahla-rm-{Environment.ProcessId}");
var folder = Path.Combine(root, "locked folder ğüş");
Directory.CreateDirectory(Path.Combine(folder, "alt"));
var locked = Path.Combine(folder, "sub", "locked file.bin");
File.WriteAllBytes(locked, new byte[1024]);
for (var i = 0; i < 5000; i++) File.WriteAllBytes(Path.Combine(folder, $"f{i:D4}.txt"), [1]);

// 1) Another process holds the file with FileShare.None
var holder = StartPs($"$f=[IO.File]::Open('{locked}','Open','Read','None'); Start-Sleep 60");
// 2) Another process has its working directory inside the folder (like a terminal that cd'd there)
var cwdHolder = StartPs("Start-Sleep 60", workDir: folder);
Thread.Sleep(2500);
Console.WriteLine($"file held by PID {holder.Id}, cwd held by PID {cwdHolder.Id}");

try
{
    Report("A) the locked file itself", [locked]);
    Report("B) the folder path (as one resource)", [folder]);
    var all = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToArray();
    Report($"C) every file in the folder ({all.Length})", all);

    var target = folder + "-tasindi";
    try { Directory.Move(folder, target); Console.WriteLine("D) move folder: SUCCEEDED (unexpected)"); Directory.Move(target, folder); }
    catch (IOException e) { Console.WriteLine($"D) move folder: {e.GetType().Name} HResult=0x{e.HResult:X8} — {e.Message}"); }
    try { File.Move(locked, locked + ".x"); Console.WriteLine("E) move locked file: SUCCEEDED (unexpected)"); }
    catch (IOException e) { Console.WriteLine($"E) move locked file: {e.GetType().Name} HResult=0x{e.HResult:X8} — {e.Message}"); }

    cwdHolder.Kill(); cwdHolder.WaitForExit(); Thread.Sleep(300);
    try { Directory.Move(folder, folder + "-2"); Console.WriteLine("F) move folder after the cwd holder exits, file still locked: SUCCEEDED"); Directory.Move(folder + "-2", folder); }
    catch (IOException e) { Console.WriteLine($"F) move folder after the cwd holder exits, file still locked: {e.GetType().Name} HResult=0x{e.HResult:X8}"); }
}
finally
{
    foreach (var p in new[] { holder, cwdHolder }) { try { p.Kill(); p.WaitForExit(); } catch { } }
    Thread.Sleep(300);
    Directory.Delete(root, true);
}

static Process StartPs(string script, string? workDir = null)
{
    var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir ?? "" };
    psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-Command"); psi.ArgumentList.Add(script);
    return Process.Start(psi)!;
}

static void Report(string label, string[] files)
{
    var sw = Stopwatch.StartNew();
    var (err, procs) = Rm.WhoIsLocking(files);
    Console.WriteLine($"{label}: {sw.ElapsedMilliseconds} ms, error={err}, processes={procs.Count}");
    foreach (var p in procs) Console.WriteLine($"   PID {p.Pid}  {p.AppName}  type={p.Type}  service='{p.Service}'");
}

record LockingProcess(int Pid, string AppName, string Service, int Type);

static unsafe partial class Rm
{
    const int CCH_RM_SESSION_KEY = 32;
    const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    // FILETIME is two DWORDs (4-byte aligned); using long pads the struct to 16 bytes and shifts the fields
    struct RM_UNIQUE_PROCESS { public int dwProcessId; public uint StartLow; public uint StartHigh; }

    [StructLayout(LayoutKind.Sequential)]
    struct RM_PROCESS_INFO
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

    public static (int Error, List<LockingProcess> Procs) WhoIsLocking(string[] paths)
    {
        var key = stackalloc char[CCH_RM_SESSION_KEY + 1];
        var rc = RmStartSession(out var h, 0, key);
        if (rc != 0) return (rc, []);
        var ptrs = new IntPtr[paths.Length];
        try
        {
            for (var i = 0; i < paths.Length; i++) ptrs[i] = Marshal.StringToHGlobalUni(paths[i]);
            fixed (IntPtr* pp = ptrs)
            {
                rc = RmRegisterResources(h, (uint)paths.Length, (char**)pp, 0, null, 0, null);
                if (rc != 0) return (rc, []);
            }
            uint count = 0;
            rc = RmGetList(h, out var needed, ref count, null, out _);
            if (rc == 0) return (0, []);
            if (rc != ERROR_MORE_DATA) return (rc, []);
            var info = new RM_PROCESS_INFO[needed];
            count = needed;
            fixed (RM_PROCESS_INFO* pi = info) rc = RmGetList(h, out needed, ref count, pi, out _);
            var list = new List<LockingProcess>();
            for (var i = 0; i < count; i++)
            {
                fixed (char* n = info[i].strAppName) fixed (char* s = info[i].strServiceShortName)
                    list.Add(new(info[i].Process.dwProcessId, new string(n), new string(s), info[i].ApplicationType));
            }
            return (rc, list);
        }
        finally
        {
            foreach (var p in ptrs) if (p != 0) Marshal.FreeHGlobal(p);
            RmEndSession(h);
        }
    }
}
