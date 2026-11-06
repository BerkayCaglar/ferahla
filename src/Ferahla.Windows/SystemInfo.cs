using System.Runtime.Versioning;
using Ferahla.Core;
using Ferahla.Core.Model;
using Microsoft.Win32;

namespace Ferahla.Windows;

[SupportedOSPlatform("windows")]
public static class SystemInfo
{
    private static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>Known folders used by rules (expanded as %Name%). Values are this machine's paths; they stay local.</summary>
    public static Dictionary<string, string> KnownFolders()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string? value) { if (!string.IsNullOrEmpty(value)) d[key] = PathUtil.Normalize(value); }
        Add("UserProfile", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add("LocalAppData", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Add("AppData", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Add("ProgramData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        Add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        Add("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Add("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add("PublicDocuments", Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments));
        Add("ProgramFiles", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add("ProgramFilesX86", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add("Windows", Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add("Temp", Path.GetTempPath());
        Add("Downloads", Native.KnownFolder(Downloads));
        if (d.TryGetValue("Windows", out var win)) Add("SystemDrive", win[..3]);
        return d;
    }

    public static VolumeInfo Volume(string root)
    {
        var di = new DriveInfo(root);
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new VolumeInfo
        {
            TotalBytes = di.TotalSize,
            FreeBytes = di.TotalFreeSpace,
            Format = di.DriveFormat,
            DriveType = di.DriveType.ToString(),
            IsSystem = win.Length >= 3 && string.Equals(win[..3], PathUtil.Normalize(root), StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>Uninstall entries from HKLM (64/32-bit) and HKCU. System components and updates are skipped.</summary>
    public static List<InstalledApp> InstalledApps()
    {
        var list = new List<InstalledApp>();
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM");
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32");
        Read(RegistryHive.CurrentUser, RegistryView.Default, "HKCU");
        return list
            .GroupBy(a => (a.Name, a.Version, a.InstallLocation))
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        void Read(RegistryHive hive, RegistryView view, string tag)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key is null) return;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var k = key.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is not string name || name.Length == 0) continue;
                if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                if (k.GetValue("ParentKeyName") is string) continue;
                list.Add(new InstalledApp
                {
                    Name = name.Trim(),
                    Version = k.GetValue("DisplayVersion") as string,
                    Publisher = k.GetValue("Publisher") as string,
                    InstallLocation = k.GetValue("InstallLocation") is string loc && loc.Length > 0 ? PathUtil.Normalize(loc.Trim().Trim('"')) : null,
                    EstimatedBytes = k.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0,
                    UninstallString = k.GetValue("UninstallString") as string,
                    Hive = tag,
                });
            }
        }
    }
}
