using Ferahla.Core.Diff;

namespace Ferahla.Core.Tests;

public class DiffTests
{
    private const long MB = 1 << 20;

    [Fact]
    public void Growth_shows_once_at_the_folder_that_explains_it()
    {
        using var t = new TempTree();
        t.File(@"home\code\app\src\a", 5 * MB);
        t.File(@"home\code\app\target\debug\a", 10 * MB);
        var before = FindingsEngineTests.Scan(t);
        t.File(@"home\code\app\target\debug\b", 30 * MB);   // grows
        t.File(@"home\Downloads\new\x", 20 * MB);           // new
        Directory.Delete(t.P(@"home\code\app\src"), true);   // gone (5 MB, below threshold)
        var after = FindingsEngineTests.Scan(t);

        var d = SnapshotDiff.Compare(before, after, threshold: 8 * MB);

        var grow = Assert.Single(d.Entries, e => e.Delta == 30 * MB);
        Assert.Equal(t.P(@"home\code\app\target\debug"), grow.Path);
        Assert.DoesNotContain(d.Entries, e => e.Path == t.P(@"home\code\app\target") || e.Path == t.P(@"home\code") || e.Path == t.Root);
        var added = Assert.Single(d.Entries, e => e.IsNew);
        Assert.Equal(t.P(@"home\Downloads\new"), added.Path);
        Assert.DoesNotContain(d.Entries, e => e.IsGone); // below threshold
        Assert.Equal(45 * MB, d.ScannedBytesDelta);
    }

    [Fact]
    public void Parent_is_listed_when_growth_is_spread_over_children()
    {
        using var t = new TempTree();
        t.File(@"cache\a\x", 2 * MB);
        t.File(@"cache\b\x", 2 * MB);
        var before = FindingsEngineTests.Scan(t);
        t.File(@"cache\a\y", 6 * MB);
        t.File(@"cache\b\y", 6 * MB);
        var after = FindingsEngineTests.Scan(t);

        var d = SnapshotDiff.Compare(before, after, threshold: 10 * MB);

        var e = Assert.Single(d.Entries);
        Assert.Equal(t.P("cache"), e.Path);
        Assert.Equal(12 * MB, e.Delta);
    }
}
