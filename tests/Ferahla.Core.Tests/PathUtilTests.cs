namespace Ferahla.Core.Tests;

public class PathUtilTests
{
    [Theory]
    [InlineData(@"c:", @"C:\")]
    [InlineData(@"c:\", @"C:\")]
    [InlineData(@"c:\Users\", @"C:\Users")]
    [InlineData(@"C:/Users/user", @"C:\Users\user")]
    [InlineData(@"\\?\C:\Users\user", @"C:\Users\user")]
    [InlineData(@"C:\\Riot Games\x", @"C:\Riot Games\x")]
    [InlineData(@"\\server\share\\x", @"\\server\share\x")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, PathUtil.Normalize(input));

    [Theory]
    [InlineData(@"C:\a\b", @"C:\a", true)]
    [InlineData(@"C:\a", @"C:\a", true)]
    [InlineData(@"C:\ab", @"C:\a", false)]
    [InlineData(@"c:\A\b", @"C:\a", true)]
    [InlineData(@"C:\a", @"C:\", true)]
    [InlineData(@"D:\a", @"C:\", false)]
    public void IsUnder(string path, string ancestor, bool expected) => Assert.Equal(expected, PathUtil.IsUnder(path, ancestor));

    [Fact]
    public void Parent_and_name()
    {
        Assert.Equal(@"C:\a", PathUtil.Parent(@"C:\a\b"));
        Assert.Equal(@"C:\", PathUtil.Parent(@"C:\a"));
        Assert.Null(PathUtil.Parent(@"C:\"));
        Assert.Equal("b", PathUtil.Name(@"C:\a\b"));
    }

    [Fact]
    public void Expand_known_tokens_only()
    {
        var vars = new Dictionary<string, string> { ["LocalAppData"] = @"C:\Users\user\AppData\Local" };
        Assert.Equal(@"C:\Users\user\AppData\Local\pip\cache", PathUtil.Expand(@"%LocalAppData%\pip\cache", vars));
        Assert.Null(PathUtil.Expand(@"%Nope%\x", vars));
    }
}
