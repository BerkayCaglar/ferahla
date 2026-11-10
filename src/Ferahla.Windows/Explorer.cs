using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ferahla.Core;

namespace Ferahla.Windows;

/// <summary>
/// Opens Explorer with an item selected, in front. Ferahla's server runs in the background, so Windows' foreground
/// lock would leave the new window behind the browser; once it appears, it is brought forward by briefly attaching to
/// the input of the window that is in front (the browser the user just clicked in).
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class Explorer
{
    /// <param name="path">An existing full local path, already checked by the caller.</param>
    public static void Reveal(string path)
    {
        var before = Windows();
        // Full path: a bare "explorer.exe" could be found next to ferahla.exe first (e.g. in Downloads).
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(new ProcessStartInfo(exe, $"/select,\"{PathUtil.Normalize(path)}\"") { UseShellExecute = false })?.Dispose();
        _ = Task.Run(async () =>
        {
            // The window usually appears within a few hundred ms; give a cold Explorer up to 4 s.
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(100);
                var fresh = Windows().FirstOrDefault(h => !before.Contains(h));
                if (fresh != 0) { BringToFront(fresh); return; }
            }
        });
    }

    private static unsafe HashSet<nint> Windows()
    {
        var set = new HashSet<nint>();
        var handle = GCHandle.Alloc(set);
        try { EnumWindows(&Collect, GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }
        return set;
    }

    [UnmanagedCallersOnly]
    private static unsafe int Collect(nint hwnd, nint state)
    {
        var buf = stackalloc char[64];
        if (IsWindowVisible(hwnd) && GetClassNameW(hwnd, buf, 64) is var n && n > 0 && new string(buf, 0, n) == "CabinetWClass")
            ((HashSet<nint>)GCHandle.FromIntPtr(state).Target!).Add(hwnd);
        return 1; // keep enumerating
    }

    private static void BringToFront(nint hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        var front = GetForegroundWindow();
        var frontThread = front == 0 ? 0u : GetWindowThreadProcessId(front, 0);
        var me = GetCurrentThreadId();
        var attached = frontThread != 0 && frontThread != me && AttachThreadInput(me, frontThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(me, frontThread, false);
        }
    }

    private const int SW_RESTORE = 9;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumWindows(delegate* unmanaged<nint, nint, int> proc, nint lParam);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetClassNameW(nint hwnd, char* name, int max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, nint processId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint to, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);
}
