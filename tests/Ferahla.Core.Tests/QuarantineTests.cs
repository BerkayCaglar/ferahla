using System.Text.Json;
using Ferahla.Core.Findings;
using Ferahla.Core.Json;
using Ferahla.Core.Quarantine;
using Ferahla.Core.Rules;
using Ferahla.Windows.Quarantine;

namespace Ferahla.Core.Tests;

public class MoveGuardTests
{
    private static readonly Dictionary<string, string> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Windows"] = @"C:\Windows",
        ["ProgramFiles"] = @"C:\Program Files",
        ["UserProfile"] = @"C:\Users\user",
        ["Desktop"] = @"C:\Users\user\Desktop",
        ["LocalAppData"] = @"C:\Users\user\AppData\Local",
        ["Temp"] = @"C:\Users\user\AppData\Local\Temp",
    };

    private static readonly MoveGuard Guard = new(Folders, @"C:\Users\user\AppData\Local\Ferahla");

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\Temp\x")]
    [InlineData(@"C:\Program Files\App\cache")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\user")]
    [InlineData(@"C:\Users\user\Desktop")]
    [InlineData(@"C:\Users\user\AppData")]
    [InlineData(@"C:\Users\user\AppData\Local\Temp")]
    [InlineData(@"C:\Users\user\AppData\Local\Ferahla\quarantine\x")]
    [InlineData(@"D:\.ferahla\quarantine")]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"C:\System Volume Information\x")]
    [InlineData(@"\\server\share\x")]
    [InlineData(@"\\?\C:\Users\user\Desktop\a")]
    [InlineData(@"Desktop\a")]
    [InlineData(@"C:\Users\user\Desktop\..\..\..\Windows")]
    [InlineData(@"C:\Users\user\Desktop\a.")]
    [InlineData(@"C:\Users\user\Desktop\*")]
    [InlineData(@"C:\Users\user\Desktop\a:stream")]
    [InlineData("")]
    public void Blocks(string path) => Assert.NotNull(Guard.Check(path));

    [Theory]
    [InlineData(@"C:\Users\user\Desktop\setup-1.2.3.exe")]
    [InlineData(@"C:\Users\user\AppData\Local\Temp\old-folder")]
    [InlineData(@"C:\Users\user\.cache\pip")]
    [InlineData(@"D:\Projects\app\node_modules")]
    [InlineData(@"C:\Users\user\Desktop\çalışma ğüşİ\build")]
    public void Allows(string path) => Assert.Null(Guard.Check(path));
}

public class PlanValidatorTests
{
    private static readonly MoveGuard Guard = new(new Dictionary<string, string> { ["Windows"] = @"C:\Windows" }, @"C:\FerahlaHome");

    private static FindingsResult Current() => new()
    {
        ScanId = "scan-1",
        Findings =
        [
            new Finding { Id = "f-aaaaaaaaaa", RuleId = "npm-cache", Action = ActionKind.Quarantine, Items = [new() { Path = @"D:\work\cache", IsDir = true, Bytes = 100 }] },
            new Finding { Id = "f-bbbbbbbbbb", RuleId = "inner", Action = ActionKind.Quarantine, Items = [new() { Path = @"D:\work\cache\inner", IsDir = true, Bytes = 10 }] },
            new Finding { Id = "f-cccccccccc", RuleId = "hiberfil", Action = ActionKind.Command, Items = [new() { Path = @"D:\hiber", IsDir = false, Bytes = 5 }] },
        ],
    };

    private static CleanupPlan Plan(params (string Id, string Path)[] items) => new()
    {
        Schema = 1, Kind = "ferahla-plan", ScanId = "scan-1",
        Findings = items.GroupBy(i => i.Id).Select(g => new PlanFinding { Id = g.Key, Items = g.Select(i => new PlanItem { Path = i.Path }).ToList() }).ToList(),
    };

    [Fact]
    public void Accepts_only_what_is_suggested_now()
    {
        var rejected = new List<ItemOutcome>();
        var moves = PlanValidator.Validate(Plan(
            ("f-aaaaaaaaaa", @"D:\work\cache"),
            ("f-aaaaaaaaaa", @"D:\work\secret-plans"),  // edited into the plan
            ("f-dddddddddd", @"D:\anything"),           // no such finding
            ("f-cccccccccc", @"D:\hiber")),             // a command finding: never moved
            Current(), Guard, rejected);

        var m = Assert.Single(moves);
        Assert.Equal(@"D:\work\cache", m.Path);
        Assert.Equal(3, rejected.Count);
        Assert.All(rejected, r => Assert.Equal(OutcomeStatus.Skipped, r.Status));
    }

    [Fact]
    public void Item_inside_another_selected_folder_goes_with_it()
    {
        var rejected = new List<ItemOutcome>();
        var moves = PlanValidator.Validate(Plan(("f-bbbbbbbbbb", @"D:\work\cache\inner"), ("f-aaaaaaaaaa", @"D:\work\cache")), Current(), Guard, rejected);
        Assert.Equal(@"D:\work\cache", Assert.Single(moves).Path);
        Assert.Equal(@"D:\work\cache\inner", Assert.Single(rejected).Path);
    }

    [Fact]
    public void Rejects_foreign_files()
    {
        Assert.Throws<InvalidDataException>(() => PlanValidator.Validate(new CleanupPlan { Schema = 1, Kind = "other" }, Current(), Guard, []));
        Assert.Throws<InvalidDataException>(() => PlanValidator.Validate(new CleanupPlan { Schema = 99, Kind = "ferahla-plan", ScanId = "x" }, Current(), Guard, []));
        Assert.Throws<InvalidDataException>(() => PlanValidator.Validate(new CleanupPlan { Schema = 1, Kind = "ferahla-plan", ScanId = @"..\..\other.scan.json.gz" }, Current(), Guard, []));
    }

    [Fact]
    public void Reads_the_report_plan_format()
    {
        const string json = """
            { "schema": 1, "kind": "ferahla-plan", "scanId": "scan-1", "createdAt": "2026-01-02T03:04:05.000Z",
              "findings": [ { "id": "f-aaaaaaaaaa", "ruleId": "npm-cache", "action": "quarantine",
                              "items": [ { "path": "D:\\work\\cache", "bytes": 100 } ] } ] }
            """;
        var plan = JsonSerializer.Deserialize(json, FerahlaJson.Default.CleanupPlan)!;
        Assert.Single(PlanValidator.Validate(plan, Current(), Guard, []));
    }
}

public sealed class QuarantineEngineTests : IDisposable
{
    private readonly TempTree _t = new();
    private readonly string _home;
    private readonly QuarantineEngine _engine;

    public QuarantineEngineTests()
    {
        _home = _t.Dir("home");
        _engine = new QuarantineEngine(_home, new MoveGuard(new Dictionary<string, string> { ["Windows"] = @"C:\Windows" }, _home));
    }

    public void Dispose() => _t.Dispose();

    private static ApprovedMove Move(string path, bool isDir, int? keepRecent = null) => new(Core.PathUtil.Normalize(path), isDir, 0, "f-0000000000", "test-rule", keepRecent);

    [Fact]
    public void Dry_run_moves_nothing()
    {
        var dir = _t.Dir("data\\cache");
        _t.File("data\\cache\\a.bin", 1000);
        var r = _engine.Quarantine([Move(dir, true)], "scan-1", dryRun: true);
        Assert.Equal(OutcomeStatus.WouldMove, Assert.Single(r.Items).Status);
        Assert.Equal(1000, r.DoneBytes);
        Assert.Null(r.Batch);
        Assert.True(File.Exists(_t.P("data\\cache\\a.bin")));
        Assert.False(Directory.Exists(Path.Combine(_home, "quarantine")) && Directory.EnumerateFileSystemEntries(Path.Combine(_home, "quarantine")).Any());
    }

    [Fact]
    public void Moves_and_restores_turkish_long_and_read_only_items()
    {
        var turkish = _t.Dir("data\\Çalışma ğüşİöç");
        _t.File("data\\Çalışma ğüşİöç\\şarkı.bin", 300);
        var ro = _t.File("data\\salt-okunur.bin", 200);
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        var deep = _t.P("data\\" + string.Join("\\", Enumerable.Repeat("uzun-klasör-adı-0123456789", 12)));
        Assert.True(deep.Length > 300);
        var longFile = _t.File(Path.Combine(deep, "dosya.bin"), 100);
        var longDir = _t.P("data\\uzun-klasör-adı-0123456789");

        var r = _engine.Quarantine([Move(turkish, true), Move(ro, false), Move(longDir, true)], "scan-1", dryRun: false);

        Assert.All(r.Items, o => Assert.Equal(OutcomeStatus.Moved, o.Status));
        Assert.Equal(600, r.DoneBytes);
        Assert.NotNull(r.Batch);
        Assert.False(Directory.Exists(turkish));
        Assert.False(File.Exists(ro));
        Assert.False(File.Exists(longFile));
        var manifestFile = Path.Combine(_home, "quarantine", r.Batch!, QuarantineManifest.FileName);
        var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestFile), FerahlaJson.Default.QuarantineManifest)!;
        Assert.Equal(3, manifest.Items.Count);
        Assert.All(manifest.Items, e => Assert.Equal(EntryState.Quarantined, e.State));

        var listed = Assert.Single(_engine.List());
        Assert.Equal(3, listed.Items);
        Assert.Equal(600, listed.Bytes);

        var back = _engine.Restore(r.Batch!, dryRun: false);
        Assert.Equal(3, back.Done);
        Assert.True(File.Exists(Path.Combine(turkish, "şarkı.bin")));
        Assert.True(File.GetAttributes(ro).HasFlag(FileAttributes.ReadOnly));
        Assert.True(File.Exists(longFile));
        Assert.False(Directory.Exists(Path.GetDirectoryName(manifestFile))); // an emptied batch is removed
        Assert.Empty(_engine.List());
    }

    [Fact]
    public void Locked_items_fail_with_the_holder_and_the_rest_still_moves()
    {
        var locked = _t.File("data\\locked.bin", 10);
        var dirWithLock = _t.Dir("data\\busy");
        var inner = _t.File("data\\busy\\inner.bin", 10);
        var free = _t.File("data\\free.bin", 10);

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(inner, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var r = _engine.Quarantine([Move(locked, false), Move(dirWithLock, true), Move(free, false)], null, dryRun: false);

            var l = r.Items.Single(o => o.Path.EndsWith("locked.bin"));
            Assert.Equal(OutcomeStatus.Failed, l.Status);
            Assert.Contains(l.LockedBy!, h => h.Pid == Environment.ProcessId);
            Assert.Equal(OutcomeStatus.Failed, r.Items.Single(o => o.Path.EndsWith("busy")).Status);
            Assert.Equal(OutcomeStatus.Moved, r.Items.Single(o => o.Path.EndsWith("free.bin")).Status);
            Assert.True(File.Exists(locked));
            Assert.True(File.Exists(inner));

            // Failed items leave nothing behind in the manifest.
            var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(_home, "quarantine", r.Batch!, QuarantineManifest.FileName)), FerahlaJson.Default.QuarantineManifest)!;
            Assert.Single(manifest.Items);
        }
    }

    [Fact]
    public void Keep_recent_hours_leaves_the_folder_and_its_new_entries()
    {
        var temp = _t.Dir("tmp");
        var old = DateTime.UtcNow.AddDays(-3);
        var oldFile = _t.File("tmp\\old.log", 50, old);
        File.SetCreationTimeUtc(oldFile, old);
        var oldDir = _t.Dir("tmp\\old-dir");
        var oldInner = _t.File("tmp\\old-dir\\x.bin", 50, old);
        File.SetCreationTimeUtc(oldInner, old);
        Directory.SetCreationTimeUtc(oldDir, old);
        Directory.SetLastWriteTimeUtc(oldDir, old);
        _t.File("tmp\\new.log", 50);
        var mixed = _t.Dir("tmp\\mixed");
        _t.File("tmp\\mixed\\fresh.bin", 50); // a fresh file deep inside keeps its whole top-level folder
        Directory.SetLastWriteTimeUtc(mixed, old);

        var r = _engine.Quarantine([Move(temp, true, keepRecent: 24)], null, dryRun: false);

        Assert.True(Directory.Exists(temp));
        Assert.False(File.Exists(oldFile));
        Assert.False(Directory.Exists(oldDir));
        Assert.True(File.Exists(_t.P("tmp\\new.log")));
        Assert.True(File.Exists(_t.P("tmp\\mixed\\fresh.bin")));
        Assert.Equal(2, r.Done);
        Assert.Contains(r.Items, o => o.Status == OutcomeStatus.Skipped && o.Reason!.Contains('2'));
    }

    [Fact]
    public void Restore_does_not_overwrite_something_new_at_the_original_place()
    {
        var f = _t.File("data\\a.txt", 5);
        var r = _engine.Quarantine([Move(f, false)], null, dryRun: false);
        File.WriteAllText(f, "new");

        var back = _engine.Restore(r.Batch!, dryRun: false);
        Assert.Equal(OutcomeStatus.Skipped, Assert.Single(back.Items).Status);
        Assert.Equal("new", File.ReadAllText(f));
        Assert.Equal(1, Assert.Single(_engine.List()).Items);
    }

    [Fact]
    public void Restore_recreates_missing_parent_folders()
    {
        var f = _t.File("data\\gone\\deeper\\a.txt", 5);
        var r = _engine.Quarantine([Move(f, false)], null, dryRun: false);
        Directory.Delete(_t.P("data\\gone"), recursive: true);

        Assert.Equal(1, _engine.Restore(r.Batch!, dryRun: false).Done);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Missing_and_guarded_items_are_skipped()
    {
        var r = _engine.Quarantine([Move(_t.P("nope.bin"), false), Move(Path.Combine(_home, "x"), false), Move(@"C:\Windows\notepad.exe", false)], null, dryRun: false);
        Assert.All(r.Items, o => Assert.Equal(OutcomeStatus.Skipped, o.Status));
        Assert.True(File.Exists(@"C:\Windows\notepad.exe") || !File.Exists(@"C:\Windows\System32\notepad.exe"));
    }

    [Fact]
    public void Drive_root_quarantine_folder_is_private_and_hidden()
    {
        var dir = _t.P(".ferahla");
        QuarantineEngine.MakePrivate(dir);

        var sec = new DirectoryInfo(dir).GetAccessControl();
        Assert.True(sec.AreAccessRulesProtected);
        var sids = sec.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().Select(r => r.IdentityReference.Value).ToHashSet();
        Assert.Equal(3, sids.Count);
        Assert.Contains(System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, sids);
        Assert.Contains("S-1-5-18", sids);     // SYSTEM
        Assert.Contains("S-1-5-32-544", sids); // Administrators
        Assert.True(new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.Hidden));
        File.WriteAllText(Path.Combine(dir, "x.txt"), "still writable");
    }

    [Fact]
    public void Restore_rejects_bad_batch_ids()
    {
        Assert.Throws<ArgumentException>(() => _engine.Restore(@"..\..\x", dryRun: false));
        Assert.Throws<FileNotFoundException>(() => _engine.Restore("20260101T000000Z-abcd", dryRun: false));
    }
}
