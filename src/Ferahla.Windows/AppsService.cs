using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ferahla.Core;
using Ferahla.Core.Model;

namespace Ferahla.Windows;

public sealed class AppEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public string? Location { get; set; }
    public long Bytes { get; set; }
    /// <summary><c>scan</c> (measured folder) or <c>estimate</c> (the installer's own number).</summary>
    public string? BytesSource { get; set; }
    /// <summary><c>steam</c>, <c>epic</c>, <c>ea</c>, <c>riot</c>, or null for an ordinary app.</summary>
    public string? Launcher { get; set; }
    public bool CanUninstall { get; set; }
    public bool IsGame { get; set; }
}

/// <summary>
/// Installed apps and games, and how to remove them the right way: through the app's own uninstaller (registry
/// UninstallString) or its launcher (Steam). Ferahla never deletes Program Files contents itself.
/// </summary>
public static partial class AppsService
{
    public static List<AppEntry> List(Snapshot? scan)
    {
        var ix = scan is null ? null : new SnapshotIndex(scan);
        var list = new List<AppEntry>();
        foreach (var a in SystemInfo.InstalledApps())
        {
            var e = new AppEntry
            {
                Id = IdOf(a), Name = a.Name, Version = a.Version, Publisher = a.Publisher, Location = a.InstallLocation,
                Bytes = a.EstimatedBytes, BytesSource = a.EstimatedBytes > 0 ? "estimate" : null,
                CanUninstall = ParseUninstall(a.UninstallString) is not null,
                Launcher = LauncherOf(a), IsGame = LauncherOf(a) is not null,
            };
            if (ix is not null && a.InstallLocation is { Length: > 3 } loc && PathUtil.IsUnder(loc, scan!.Root) && ix.Find(loc) is var d and >= 0)
            {
                e.Bytes = ix.Dir(d).Size;
                e.BytesSource = "scan";
            }
            list.Add(e);
        }
        foreach (var g in SteamGames()) if (!list.Any(x => x.Location is not null && PathUtil.IsUnder(x.Location, g.Location!))) list.Add(g.WithSize(ix, scan));
        return list.OrderByDescending(x => x.Bytes).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static AppEntry WithSize(this AppEntry e, SnapshotIndex? ix, Snapshot? scan)
    {
        if (ix is not null && e.Location is not null && PathUtil.IsUnder(e.Location, scan!.Root) && ix.Find(e.Location) is var d and >= 0)
        {
            e.Bytes = ix.Dir(d).Size;
            e.BytesSource = "scan";
        }
        return e;
    }

    public static string IdOf(InstalledApp a) =>
        "a-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{a.Name}|{a.Version}|{a.Hive}|{a.UninstallString}")).AsSpan(0, 5));

    private static string? LauncherOf(InstalledApp a) =>
        a.Publisher?.Contains("Riot Games", StringComparison.OrdinalIgnoreCase) == true && !a.Name.Contains("Riot Client", StringComparison.OrdinalIgnoreCase) ? "riot"
        : a.InstallLocation?.Contains(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase) == true ? "steam"
        : a.InstallLocation?.Contains(@"\Epic Games\", StringComparison.OrdinalIgnoreCase) == true ? "epic"
        : a.InstallLocation?.Contains(@"\EA Games\", StringComparison.OrdinalIgnoreCase) == true ? "ea"
        : null;

    // ---- Steam ----

    [GeneratedRegex("\"(appid|name|installdir)\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex AcfRx();

    /// <summary>Games from Steam's app manifests (appmanifest_&lt;id&gt;.acf) in every library folder.</summary>
    public static List<AppEntry> SteamGames()
    {
        var games = new List<AppEntry>();
        foreach (var lib in SteamLibraries())
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    var fields = AcfRx().Matches(File.ReadAllText(acf)).GroupBy(m => m.Groups[1].Value.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
                    if (!fields.TryGetValue("appid", out var id) || !id.All(char.IsAsciiDigit) || !fields.TryGetValue("installdir", out var dir)) continue;
                    if (dir.Contains("..") || dir.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                    games.Add(new AppEntry
                    {
                        Id = "steam-" + id, Name = fields.GetValueOrDefault("name", dir), Location = PathUtil.Normalize(Path.Combine(apps, "common", dir)),
                        Launcher = "steam", IsGame = true, CanUninstall = true, Publisher = "Steam",
                    });
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return games;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string>();
        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            if (k?.GetValue("SteamPath") is string sp) roots.Add(PathUtil.Normalize(sp.Replace('/', '\\')));
        foreach (var root in roots.ToList())
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                roots.Add(PathUtil.Normalize(m.Groups[1].Value.Replace(@"\\", @"\")));
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    // ---- uninstall ----

    /// <summary>Splits an UninstallString into program and arguments. Null if it doesn't name an existing program.</summary>
    public static (string Exe, string Args)? ParseUninstall(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = Environment.ExpandEnvironmentVariables(s.Trim());
        string exe, args;
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end < 0) return null;
            exe = s[1..end];
            args = s[(end + 1)..].Trim();
        }
        else
        {
            var i = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            exe = s[..(i + 4)];
            args = s[(i + 4)..].Trim();
        }
        if (exe.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("MsiExec", StringComparison.OrdinalIgnoreCase))
            exe = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        if (!Path.IsPathFullyQualified(exe)) exe = Path.Combine(Environment.SystemDirectory, exe);
        return File.Exists(exe) ? (exe, args) : null;
    }

    /// <summary>Starts the app's own uninstaller, or Steam's uninstall dialog. Returns false if it can't.</summary>
    public static bool Uninstall(string id)
    {
        if (id.StartsWith("steam-", StringComparison.Ordinal))
        {
            var appId = id[6..];
            if (appId.Length == 0 || !appId.All(char.IsAsciiDigit)) return false;
            Process.Start(new ProcessStartInfo("steam://uninstall/" + appId) { UseShellExecute = true })?.Dispose();
            return true;
        }
        var app = SystemInfo.InstalledApps().FirstOrDefault(a => IdOf(a) == id);
        if (app is null || ParseUninstall(app.UninstallString) is not { } u) return false;
        // The uninstaller asks for elevation itself if it needs it (its manifest); shell execute lets it.
        Process.Start(new ProcessStartInfo(u.Exe, u.Args) { UseShellExecute = true })?.Dispose();
        return true;
    }

    public static void OpenAppsSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true })?.Dispose();
}
