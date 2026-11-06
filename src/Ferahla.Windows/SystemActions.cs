using System.ComponentModel;
using System.Diagnostics;

namespace Ferahla.Windows;

/// <param name="Id">What the UI asks for; nothing else can be run.</param>
/// <param name="RuleId">The finding this action belongs to.</param>
public sealed record SystemAction(string Id, string RuleId, string Exe, string[] Args)
{
    public string CommandLine => Path.GetFileName(Exe) + " " + string.Join(' ', Args);
}

/// <summary>
/// The few system commands Ferahla can start, each in its own elevated console (UAC for that step only; Ferahla
/// itself never runs elevated). Fixed programs and arguments: no text from a rule, a request or a scan reaches the
/// command line. <c>DISM /ResetBase</c> is deliberately absent: it makes installed updates impossible to remove.
/// </summary>
public static class SystemActions
{
    private static string Sys(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    public static readonly IReadOnlyList<SystemAction> All =
    [
        new("hibernate-off", "system.hiberfil", Sys("powercfg.exe"), ["/hibernate", "off"]),
        new("component-analyze", "system.component-store", Sys("Dism.exe"), ["/Online", "/Cleanup-Image", "/AnalyzeComponentStore"]),
        new("component-cleanup", "system.component-store", Sys("Dism.exe"), ["/Online", "/Cleanup-Image", "/StartComponentCleanup"]),
        new("shadow-storage", "system.unaccounted", Sys("vssadmin.exe"), ["list", "shadowstorage"]),
    ];

    public static SystemAction? Get(string id) => All.FirstOrDefault(a => a.Id == id);

    public static IEnumerable<SystemAction> ForRule(string ruleId) => All.Where(a => a.RuleId == ruleId);

    /// <summary>Starts the action in an elevated console that stays open to show the output.</summary>
    /// <returns>False if the user declined the UAC prompt.</returns>
    public static bool Run(SystemAction a)
    {
        // cmd /k keeps the window open so the user sees the result; the arguments are constants (see All).
        var psi = new ProcessStartInfo(Sys("cmd.exe"))
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = "/k \"\"" + a.Exe + "\" " + string.Join(' ', a.Args) + "\"",
        };
        try { Process.Start(psi)?.Dispose(); return true; }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; } // ERROR_CANCELLED
    }
}
