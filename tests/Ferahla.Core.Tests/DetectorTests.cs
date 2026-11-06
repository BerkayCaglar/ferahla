using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Ferahla.Core.Findings;
using Ferahla.Core.Model;
using Ferahla.Core.Rules;
using Ferahla.Windows.Detectors;

namespace Ferahla.Core.Tests;

public class DetectorTests
{
    private const long MB = 1 << 20;

    private static FindingsResult Run(Snapshot s) =>
        new FindingsEngine(RuleLoader.LoadBuiltIn(), BuiltInDetectors.All()).Run(s, live: true);

    private static Snapshot Scan(TempTree t, params InstalledApp[] apps)
    {
        var s = FindingsEngineTests.Scan(t);
        s.Apps.AddRange(apps);
        return s;
    }

    [Fact]
    public void Installers_of_installed_app_are_safe_and_a_newer_one_stays()
    {
        using var t = new TempTree();
        t.File(@"home\Downloads\tunebox_win64_3.1.0.exe", 2 * MB);
        t.File(@"home\Downloads\tunebox_win64_3.4.2.exe", 2 * MB);
        t.File(@"home\Downloads\tunebox_win64_4.0.1.exe", 2 * MB); // newer than installed: an update waiting
        var s = Scan(t, new InstalledApp { Name = "Tunebox Studio 3", Version = "3.4.2" });

        var r = Run(s);

        var f = Assert.Single(r.Findings, f => f.RuleId == "installer.downloads");
        Assert.Equal(Risk.Safe, f.Risk);
        Assert.Equal(2, f.Items.Count);
        Assert.DoesNotContain(f.Items, i => i.Path.Contains("4.0.1"));
        Assert.Contains("Tunebox Studio 3", f.Title);
    }

    [Fact]
    public void Stale_registry_version_does_not_hide_old_installers()
    {
        using var t = new TempTree();
        // Apps that update in place can keep an old version (here "1.4.2") in the registry while 4.x is what runs.
        foreach (var v in new[] { "3.1.0.1200", "3.4.2.1350", "4.0.1.1500" })
            t.File($@"home\Downloads\tunebox_win64_{v}.exe", 2 * MB);
        var s = Scan(t, new InstalledApp { Name = "Tunebox Studio", Version = "1.4.2.900" });

        var f = Assert.Single(Run(s).Findings, f => f.RuleId == "installer.downloads");

        Assert.Equal(2, f.Items.Count);
        Assert.DoesNotContain(f.Items, i => i.Path.Contains("4.0.1"));
    }

    [Fact]
    public void Installer_and_its_zip_twin_go_together()
    {
        using var t = new TempTree();
        t.File(@"home\Desktop\Install_mixdeck_x64_2_4_8.exe", 2 * MB);
        t.File(@"home\Desktop\Install_mixdeck_x64_2_4_8.zip", 2 * MB);
        var s = Scan(t, new InstalledApp { Name = "mixdeck 2.4.8", Version = "2.4.8" });

        var f = Assert.Single(Run(s).Findings, f => f.RuleId == "installer.downloads");

        Assert.Equal(2, f.Items.Count);
        Assert.DoesNotContain(Run(s).Findings, x => x.RuleId == "archive.extracted");
    }

    [Fact]
    public void Several_versions_of_an_unknown_product_keep_the_newest_at_review()
    {
        using var t = new TempTree();
        t.File(@"home\Downloads\sometool-setup-1.2.exe", 2 * MB);
        t.File(@"home\Downloads\sometool-setup-1.10.exe", 2 * MB);

        var f = Assert.Single(Run(Scan(t)).Findings, f => f.RuleId == "installer.downloads");

        Assert.Equal(Risk.Review, f.Risk);
        Assert.EndsWith("sometool-setup-1.2.exe", f.Items.Single().Path);
    }

    [Fact]
    public void Zip_with_complete_extracted_twin_is_safe()
    {
        using var t = new TempTree();
        var folder = t.Dir(@"home\Desktop\Stems");
        File.WriteAllBytes(Path.Combine(folder, "a.wav"), new byte[30 * MB]);
        File.WriteAllBytes(Path.Combine(folder, "b.wav"), new byte[30 * MB]);
        ZipFile.CreateFromDirectory(folder, t.P(@"home\Desktop\Stems.zip"), CompressionLevel.NoCompression, includeBaseDirectory: false);

        var r = Run(Scan(t));

        var f = Assert.Single(r.Findings, f => f.RuleId == "archive.extracted");
        Assert.Equal(Risk.Safe, f.Risk);
        Assert.Contains(f.Evidence, e => e.Contains("2/2"));
    }

    [Fact]
    public void Zip_with_partial_twin_stays_review()
    {
        using var t = new TempTree();
        var src = t.Dir("src");
        File.WriteAllBytes(Path.Combine(src, "a.bin"), new byte[30 * MB]);
        File.WriteAllBytes(Path.Combine(src, "b.bin"), new byte[30 * MB]);
        t.Dir(@"home\Desktop");
        ZipFile.CreateFromDirectory(src, t.P(@"home\Desktop\pack.zip"), CompressionLevel.NoCompression, false);
        t.File(@"home\Desktop\pack\a.bin", 30 * MB);

        var f = Assert.Single(Run(Scan(t)).Findings, f => f.RuleId == "archive.extracted");

        Assert.Equal(Risk.Review, f.Risk);
        Assert.Contains(f.Evidence, e => e.Contains("1/2"));
    }

    [Fact]
    public void Old_unextracted_multipart_rar_is_one_review_finding()
    {
        using var t = new TempTree();
        var old = DateTime.UtcNow.AddDays(-60);
        for (var i = 1; i <= 3; i++) t.File($@"home\Downloads\Game\Game.part0{i}.rar", 100 * MB, old);

        var f = Assert.Single(Run(Scan(t)).Findings, f => f.RuleId == "archive.extracted");

        Assert.Equal(3, f.Items.Count);
        Assert.Equal(Risk.Review, f.Risk);
    }

    [Fact]
    public void Partial_download_with_finished_twin_is_safe()
    {
        using var t = new TempTree();
        t.File(@"home\Downloads\movie.mkv.fdmdownload", 5 * MB);
        t.File(@"home\Downloads\movie.mkv", 5 * MB);

        var f = Assert.Single(Run(Scan(t)).Findings, f => f.RuleId == "download.partial");

        Assert.Equal(Risk.Safe, f.Risk);
    }

    [Fact]
    public void Fresh_partial_download_is_left_alone()
    {
        using var t = new TempTree();
        t.File(@"home\Downloads\big.iso.crdownload", 5 * MB);

        Assert.DoesNotContain(Run(Scan(t)).Findings, f => f.RuleId == "download.partial");
    }

    [Fact]
    public void Repo_clone_with_same_remote_is_found()
    {
        using var t = new TempTree();
        foreach (var name in new[] { "game", "game_clone_0" })
        {
            t.File($@"home\code\{name}\.git\config", 0);
            File.WriteAllText(t.P($@"home\code\{name}\.git\config"), "[core]\n\tbare = false\n[remote \"origin\"]\n\turl = https://example.com/team/game.git\n");
            t.File($@"home\code\{name}\Assets\big.bin", 120 * MB);
        }

        var f = Assert.Single(Run(Scan(t)).Findings, f => f.RuleId == "repo.clone");

        Assert.EndsWith("game_clone_0", f.Items.Single().Path);
        Assert.Contains(f.Evidence, e => e.Contains("example.com/team/game"));
    }

    [Fact]
    public void ParrelSync_clone_is_one_finding_covering_its_library()
    {
        using var t = new TempTree();
        t.Dir(@"home\code\game\Assets");
        t.Dir(@"home\code\game\ProjectSettings");
        t.File(@"home\code\game\Library\a", 150 * MB);
        t.Dir(@"home\code\game_clone_0\ProjectSettings");
        var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{t.P(@"home\code\game_clone_0\Assets")}\" \"{t.P(@"home\code\game\Assets")}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        mk.WaitForExit();
        t.File(@"home\code\game_clone_0\Library\a", 120 * MB);

        var r = Run(Scan(t));

        var clone = Assert.Single(r.Findings, f => f.RuleId == "repo.clone");
        Assert.EndsWith("game_clone_0", clone.Items.Single().Path);
        Assert.Contains(clone.Evidence, e => e.Contains("ParrelSync"));
        // The clone's Library is inside the clone finding, so it is not a separate finding.
        Assert.DoesNotContain(r.Findings, f => f.RuleId == "build.unity" && f.Items.Any(i => i.Path.Contains("_clone_0")));
    }

    [Fact]
    public void Duplicates_are_found_but_hardlinks_are_not()
    {
        using var t = new TempTree();
        var a = t.P(@"home\Videos\rec.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(a)!);
        var data = new byte[60 * MB];
        new Random(1).NextBytes(data);
        File.WriteAllBytes(a, data);
        Directory.CreateDirectory(t.P(@"home\Desktop"));
        File.Copy(a, t.P(@"home\Desktop\rec.mp4"));
        var link = t.P(@"home\Videos\rec-link.mp4");
        var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{a}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        mk.WaitForExit();
        Assert.Equal(0, mk.ExitCode);

        var dups = Run(Scan(t)).Findings.Where(f => f.RuleId == "duplicate.file").ToList();

        // Videos\rec.mp4 and its hardlink are one file; the Desktop copy is the duplicate to suggest.
        var f = Assert.Single(dups);
        Assert.Equal(t.P(@"home\Desktop\rec.mp4"), f.Items.Single().Path);
    }

    [Fact]
    public void Same_length_files_with_equal_ends_but_different_middles_are_not_duplicates()
    {
        using var t = new TempTree();
        // Like two stems of one song: same length, silent start and end, different content in between.
        var a = new byte[60 * MB];
        var b = new byte[60 * MB];
        new Random(1).NextBytes(a.AsSpan(4 * (int)MB, 50 * (int)MB));
        new Random(2).NextBytes(b.AsSpan(4 * (int)MB, 50 * (int)MB));
        Directory.CreateDirectory(t.P(@"home\Music"));
        File.WriteAllBytes(t.P(@"home\Music\drums.wav"), a);
        File.WriteAllBytes(t.P(@"home\Music\bass.wav"), b);

        Assert.DoesNotContain(Run(Scan(t)).Findings, f => f.RuleId == "duplicate.file");
    }

    [Fact]
    public void Duplicate_keeps_the_organised_copy_not_the_loose_one()
    {
        using var t = new TempTree();
        var data = new byte[60 * MB];
        new Random(5).NextBytes(data);
        Directory.CreateDirectory(t.P(@"home\Desktop\Music Projects\song\Samples"));
        File.WriteAllBytes(t.P(@"home\Desktop\Music Projects\song\Samples\drums.wav"), data);
        File.WriteAllBytes(t.P(@"home\Desktop\drums.wav"), data);
        File.WriteAllBytes(t.P(@"home\Desktop\Music Projects\song\Samples\drums - Copy.wav"), data);

        var f = Assert.Single(Run(Scan(t)).Findings, f => f.RuleId == "duplicate.file");

        Assert.Contains(f.Evidence, e => e.EndsWith(@"Samples\drums.wav"));
        Assert.Equal(2, f.Items.Count);
    }

    [Fact]
    public void Files_inside_projects_are_not_duplicate_candidates()
    {
        using var t = new TempTree();
        var data = new byte[60 * MB];
        new Random(3).NextBytes(data);
        foreach (var p in new[] { "a", "b" })
        {
            t.Dir($@"home\code\{p}\.git");
            Directory.CreateDirectory(t.P($@"home\code\{p}\Plugins"));
            File.WriteAllBytes(t.P($@"home\code\{p}\Plugins\lib.bundle"), data);
        }

        Assert.DoesNotContain(Run(Scan(t)).Findings, f => f.RuleId == "duplicate.file");
    }

    [Fact]
    public void Stray_marker_in_profile_does_not_hide_loose_files()
    {
        using var t = new TempTree();
        t.File(@"home\package.json", 10); // npm install once run in the home folder
        var data = new byte[60 * MB];
        new Random(4).NextBytes(data);
        Directory.CreateDirectory(t.P(@"home\Music"));
        Directory.CreateDirectory(t.P(@"home\Desktop"));
        File.WriteAllBytes(t.P(@"home\Music\song.wav"), data);
        File.WriteAllBytes(t.P(@"home\Desktop\song - Copy.wav"), data);

        var f = Assert.Single(Run(Scan(t)).Findings, f => f.RuleId == "duplicate.file");
        Assert.EndsWith("song - Copy.wav", f.Items.Single().Path);
    }

    [Fact]
    public void Unity_editor_not_used_by_any_project_is_found()
    {
        using var t = new TempTree();
        t.File(@"Program Files\Unity\Hub\Editor\2019.4.1f1\Editor\Unity.exe", 150 * MB);
        t.File(@"Program Files\Unity\Hub\Editor\2019.4.9f1\Editor\Unity.exe", 150 * MB);
        t.Dir(@"home\proj\Assets");
        t.File(@"home\proj\ProjectSettings\ProjectVersion.txt", 0);
        File.WriteAllText(t.P(@"home\proj\ProjectSettings\ProjectVersion.txt"), "m_EditorVersion: 2019.4.9f1\n");
        t.File(@"home\proj\Library\x", 2 * MB);

        var f = Assert.Single(Run(Scan(t)).Findings, f => f.RuleId == "old.unity-editor");

        Assert.EndsWith("2019.4.1f1", f.Items.Single().Path);
        Assert.Equal(ActionKind.Launcher, f.Action);
    }

    [Fact]
    public void Steam_games_are_launcher_findings_and_shared_runtimes_are_skipped()
    {
        using var t = new TempTree();
        var known = FindingsEngineTests.Known(t);
        known["ProgramFilesX86"] = t.P("Program Files (x86)");
        t.File(@"Program Files (x86)\Steam\steamapps\common\SomeGame\game.pak", 1100 * MB);
        t.File(@"Program Files (x86)\Steam\steamapps\common\Steamworks Shared\redist.bin", 1100 * MB);
        var s = new Windows.Scanning.Scanner(new Windows.Scanning.ScanOptions { Root = t.Root, KnownFolders = known }).Run();

        var games = Run(s).Findings.Where(f => f.RuleId == "game.steam").ToList();

        var g = Assert.Single(games);
        Assert.Contains("SomeGame", g.Title);
        Assert.Equal(ActionKind.Launcher, g.Action);
    }

    [Fact]
    public void Native_struct_sizes_match_win32()
    {
        Assert.Equal(52, Marshal.SizeOf<DuplicatesDetector.BY_HANDLE_FILE_INFORMATION>());
    }

    [Theory]
    [InlineData("tunebox_win64_3.1.0.1200.exe", "tunebox")]
    [InlineData("Install_mixdeck_x64_2_4_8.exe", "mixdeck")]
    [InlineData("modeler-9.8.1-windows-x64.msi", "modeler")]
    [InlineData("node-v1.2.3-x64.msi", "node")]
    [InlineData("python-9.8.7-amd64.exe", "python")]
    public void Product_keys(string file, string key) => Assert.Equal(key, DetectorUtil.ProductKey(file));

    [Theory]
    [InlineData("tunebox_win64_3.1.0.1200.exe", "3.1.0.1200")]
    [InlineData("Install_mixdeck_x64_2_4_8.exe", "2.4.8")]
    [InlineData("node-v1.2.3-x64.msi", "1.2.3")]
    [InlineData("python-9.8.7-amd64.exe", "9.8.7")]
    public void Versions_in_names(string file, string version) => Assert.Equal(Version.Parse(version), DetectorUtil.VersionIn(file));

    [Theory]
    [InlineData("https://github.com/Team/Repo.git", "github.com/team/repo")]
    [InlineData("git@github.com:team/repo.git", "github.com/team/repo")]
    public void Remote_urls_normalize(string url, string expected) => Assert.Equal(expected, RepoClonesDetector.NormalizeRemote(url));
}
