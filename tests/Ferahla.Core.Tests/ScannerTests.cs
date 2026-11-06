using System.Diagnostics;
using Ferahla.Core.Model;
using Ferahla.Core.Storage;
using Ferahla.Windows.Scanning;

namespace Ferahla.Core.Tests;

public class ScannerTests
{
    private const long MB = 1 << 20;

    private static (Snapshot Snap, SnapshotIndex Ix) Scan(TempTree t, long minDir = MB, long big = 50 * MB)
    {
        var known = new Dictionary<string, string>
        {
            ["UserProfile"] = t.Root,
            ["Downloads"] = t.P("Downloads"),
            ["Desktop"] = t.P("Desktop"),
            ["LocalAppData"] = t.P(@"AppData\Local"),
            ["AppData"] = t.P(@"AppData\Roaming"),
        };
        var snap = new Scanner(new ScanOptions { Root = t.Root, MinDirBytes = minDir, BigFileBytes = big, KnownFolders = known }).Run();
        return (snap, new SnapshotIndex(snap));
    }

    [Fact]
    public void Sizes_add_up_and_small_dirs_fold_into_parent()
    {
        using var t = new TempTree();
        t.File(@"a\big.bin", 3 * MB);
        t.File(@"a\small\x.txt", 10_000);
        t.File(@"a\small\deeper\y.txt", 20_000);
        t.File("root.txt", 5);

        var (snap, ix) = Scan(t);

        Assert.Equal(3 * MB + 30_005, snap.Dirs[0].Size);
        Assert.Equal(4, snap.Stats.Files);
        var a = ix.Find(t.P("a"));
        Assert.True(a >= 0);
        Assert.Equal(3 * MB + 30_000, ix.Dir(a).Size);
        Assert.Equal(4, snap.Dirs[0].FileCount);
        // "small" and "deeper" are under 1 MB, so they are not separate entries
        Assert.Equal(-1, ix.Find(t.P(@"a\small")));
        Assert.Equal(a, ix.FindNearest(t.P(@"a\small\deeper")));
    }

    [Fact]
    public void Records_project_markers()
    {
        using var t = new TempTree();
        t.File(@"rs\Cargo.toml", 100);
        t.File(@"rs\target\debug\app.exe", 2 * MB);
        t.Dir(@"unity\Assets");
        t.Dir(@"unity\ProjectSettings");
        t.File(@"unity\Library\cache.bin", 2 * MB);
        t.File(@"cs\App.csproj", 100);
        t.File(@"cs\bin\x.dll", 2 * MB);

        var (_, ix) = Scan(t);

        Assert.Equal(DirMarkers.CargoToml, ix.Dir(ix.Find(t.P("rs"))).Markers);
        Assert.Equal(DirMarkers.UnityAssets | DirMarkers.UnityProjectSettings, ix.Dir(ix.Find(t.P("unity"))).Markers);
        Assert.Equal(DirMarkers.CsProj, ix.Dir(ix.Find(t.P("cs"))).Markers);
    }

    [Fact]
    public void Keeps_notable_files_by_kind_and_place()
    {
        using var t = new TempTree();
        t.File(@"Downloads\setup_1.0.exe", 2 * MB);
        t.File(@"Downloads\tiny.exe", 1000);                // too small for an installer
        t.File(@"Documents\tool.exe", 2 * MB);              // not in Downloads/Desktop
        t.File(@"Desktop\stems.zip", 2 * MB);
        t.File(@"AppData\Local\pkg\cache.zip", 2 * MB);     // archives in AppData are not "user files"
        t.File(@"Downloads\movie.mkv.crdownload", 10);      // partial: any size
        t.File(@"Videos\huge.mkv", 60 * MB);                // big
        t.File(@"Downloads\sub\folder\small-setup.msi", 300 * 1024); // inside a folded dir

        var (snap, ix) = Scan(t);
        var byName = snap.Files.ToDictionary(f => Path.GetFileName(f.Name), f => f);

        Assert.True(byName["setup_1.0.exe"].Flags.HasFlag(FileFlags.Installer));
        Assert.False(byName.ContainsKey("tiny.exe"));
        Assert.False(byName.ContainsKey("tool.exe"));
        Assert.True(byName["stems.zip"].Flags.HasFlag(FileFlags.Archive));
        Assert.False(byName.ContainsKey("cache.zip"));
        Assert.True(byName["movie.mkv.crdownload"].Flags.HasFlag(FileFlags.PartialDownload));
        Assert.True(byName["huge.mkv"].Flags.HasFlag(FileFlags.Big));
        var msi = byName["small-setup.msi"];
        Assert.True(msi.Flags.HasFlag(FileFlags.Installer));
        Assert.Equal(t.P(@"Downloads\sub\folder\small-setup.msi"), ix.PathOf(msi));
    }

    [Fact]
    public void Tracks_latest_write_time_of_subtree()
    {
        using var t = new TempTree();
        var old = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var recent = new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        t.File(@"p\a.bin", 2 * MB, old);
        t.File(@"p\deep\b.txt", 10, recent);

        var (_, ix) = Scan(t);

        Assert.Equal(recent, ix.Dir(ix.Find(t.P("p"))).LastWriteUtc);
    }

    [Fact]
    public void Does_not_enter_junctions()
    {
        using var t = new TempTree();
        t.File(@"real\data.bin", 2 * MB);
        var link = t.P("link");
        var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{t.P("real")}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        mk.WaitForExit();
        Assert.Equal(0, mk.ExitCode);

        var (snap, _) = Scan(t);

        Assert.Equal(2 * MB, snap.Dirs[0].Size);
        Assert.Equal(1, snap.Stats.ReparsePointsSkipped);
    }

    [Fact]
    public void Handles_long_and_turkish_paths()
    {
        using var t = new TempTree();
        var deep = string.Join('\\', Enumerable.Repeat("very-long-folder-name-ğüşıöçİ", 12));
        t.File(Path.Combine(deep, "dosya.bin"), 2 * MB);
        Assert.True(t.P(deep).Length > 260);

        var (snap, ix) = Scan(t);

        Assert.Equal(2 * MB, snap.Dirs[0].Size);
        Assert.True(ix.Find(t.P(deep)) >= 0);
    }

    [Fact]
    public void Snapshot_round_trips_through_store()
    {
        using var t = new TempTree();
        t.File(@"Downloads\setup.exe", 2 * MB);
        var (snap, _) = Scan(t);
        var store = new SnapshotStore(t.P(".home"));

        var path = store.Save(snap);
        var back = store.Load(snap.Id);

        Assert.True(File.Exists(path));
        Assert.Equal(snap.Id, store.Latest());
        Assert.Equal(snap.Dirs.Count, back.Dirs.Count);
        Assert.Equal(snap.Dirs[0].Size, back.Dirs[0].Size);
        Assert.Equal(snap.Files.Single().Name, back.Files.Single().Name);
        Assert.Equal(snap.Files.Single().Flags, back.Files.Single().Flags);
    }
}
