using System.IO.Compression;
using System.Text;
using Ferahla.Core.Findings;
using Ferahla.Windows;

namespace Ferahla.Core.Tests;

public sealed class VerifierTests : IDisposable
{
    private readonly TempTree _t = new();
    public void Dispose() => _t.Dispose();

    private static VerifyResult V(Finding f) => Verifier.Verify(f, TestContext.Current.CancellationToken);

    private static Finding Dup(string kept, params string[] copies) => new()
    {
        Id = "f-0000000001", Category = "duplicate", Title = "dup",
        Evidence = [$"Kept: {kept}", "Same size"],
        Items = copies.Select(c => new FindingItem { Path = c, Bytes = new FileInfo(c).Length }).ToList(),
    };

    [Fact]
    public void Identical_copies_verify_and_a_changed_byte_does_not()
    {
        var data = Encoding.UTF8.GetBytes(new string('x', 3_000_000));
        var kept = _t.P("a.bin"); File.WriteAllBytes(kept, data);
        var copy = _t.P("a (1).bin"); File.WriteAllBytes(copy, data);
        Assert.True(V(Dup(kept, copy)).Ok);

        data[2_500_000] = (byte)'y'; // the middle, where head/tail probes don't look
        File.WriteAllBytes(copy, data);
        var r = V(Dup(kept, copy));
        Assert.False(r.Ok);
        Assert.Contains(r.Checks, c => c.Contains("DIFFERENT") || c.Contains("FARKLI"));
    }

    [Fact]
    public void Missing_kept_copy_fails()
    {
        var copy = _t.File("b (1).bin", 10);
        Assert.False(V(Dup(_t.P("gone.bin"), copy)).Ok);
    }

    [Fact]
    public void Zip_and_folder_are_compared_by_crc()
    {
        var folder = _t.Dir("pack");
        File.WriteAllText(Path.Combine(folder, "one.txt"), "first file");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "two.txt"), "second file");
        var zip = _t.P("pack.zip");
        ZipFile.CreateFromDirectory(folder, zip);
        var f = new Finding { Id = "f-0000000002", Category = "archive", Title = "zip", Items = [new() { Path = zip, Bytes = new FileInfo(zip).Length }] };

        var ok = V(f);
        Assert.True(ok.Ok);
        Assert.Contains(ok.Checks, c => c.Contains("2/2") || c.Contains("2 tanesi"));

        File.WriteAllText(Path.Combine(folder, "one.txt"), "first filE"); // same length, different content
        Assert.False(V(f).Ok);
    }

    [Fact]
    public void Crc32_matches_the_standard_check_value() =>
        Assert.Equal(0xCBF43926u, Crc32.Append(0, Encoding.ASCII.GetBytes("123456789")));
}

public class SystemActionTests
{
    [Fact]
    public void Only_fixed_system_programs_and_never_ResetBase()
    {
        Assert.NotEmpty(SystemActions.All);
        foreach (var a in SystemActions.All)
        {
            Assert.StartsWith(Environment.SystemDirectory, a.Exe, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(a.Args, x => x.Contains("ResetBase", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(a.Args, x => x.Contains('&') || x.Contains('|') || x.Contains('"'));
        }
        Assert.Null(SystemActions.Get("powercfg -h off & del C:\\x"));
        Assert.Equal("hibernate-off", Assert.Single(SystemActions.ForRule("system.hiberfil")).Id);
    }

    [Fact]
    public void Rules_never_suggest_ResetBase()
    {
        var rules = Rules.RuleLoader.LoadBuiltIn(includePending: true);
        Assert.DoesNotContain(rules.Rules, r => (r.Command ?? "").Contains("ResetBase", StringComparison.OrdinalIgnoreCase));
    }
}

public class UserAreaTests
{
    private static readonly Windows.Scanning.FileClassifier C = new(new Dictionary<string, string>
    {
        ["UserProfile"] = @"C:\Users\user", ["LocalAppData"] = @"C:\Users\user\AppData\Local", ["AppData"] = @"C:\Users\user\AppData\Roaming",
        ["Windows"] = @"C:\Windows", ["ProgramFiles"] = @"C:\Program Files", ["ProgramData"] = @"C:\ProgramData",
    }, 50L << 20);

    [Theory]
    [InlineData(@"C:\Users\user\Downloads", true)]
    [InlineData(@"D:\Downloads\packs", true)]       // the user's files on another drive
    [InlineData(@"C:\Projects\app", true)]
    [InlineData(@"C:\Users\user\AppData\Local\x", false)]
    [InlineData(@"C:\Users\user\.cache", false)]
    [InlineData(@"C:\Users\public\Downloads", false)] // the shared profile, not the user's own
    [InlineData(@"C:\Windows\Temp", false)]
    [InlineData(@"C:\Program Files\App", false)]
    [InlineData(@"D:\$Recycle.Bin\S-1", false)]
    public void User_area(string path, bool expected) =>
        Assert.Equal(expected, (C.ContextFor(path, null) & Windows.Scanning.PathContext.UserArea) != 0);
}

public class UninstallStringTests
{
    private static readonly string Sys = Environment.SystemDirectory;

    [Fact]
    public void Quoted_path_with_arguments()
    {
        var exe = Path.Combine(Sys, "notepad.exe");
        var r = AppsService.ParseUninstall($"\"{exe}\" /S --uninstall");
        Assert.Equal((exe, "/S --uninstall"), r);
    }

    [Fact]
    public void Msiexec_without_a_path()
    {
        var r = AppsService.ParseUninstall("MsiExec.exe /X{12345678-1234-1234-1234-123456789012}");
        Assert.NotNull(r);
        Assert.Equal(Path.Combine(Sys, "msiexec.exe"), r!.Value.Exe, ignoreCase: true);
        Assert.StartsWith("/X{", r.Value.Args);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"C:\\nowhere\\uninstall.exe\" /S")]
    [InlineData("rundll32 something")]
    [InlineData("\"unterminated")]
    public void Unusable_strings_are_refused(string? s) => Assert.Null(AppsService.ParseUninstall(s));
}
