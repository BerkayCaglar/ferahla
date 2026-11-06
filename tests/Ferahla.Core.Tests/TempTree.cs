namespace Ferahla.Core.Tests;

/// <summary>A throwaway directory tree under %TEMP% for scanner tests.</summary>
public sealed class TempTree : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ferahla-test-" + Guid.NewGuid().ToString("N")[..8]);

    public TempTree() => Directory.CreateDirectory(Root);

    public string P(string rel) => Path.Combine(Root, rel);

    public string File(string rel, long bytes, DateTime? lastWriteUtc = null)
    {
        var path = P(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = System.IO.File.Create(path)) fs.SetLength(bytes);
        if (lastWriteUtc is { } t) System.IO.File.SetLastWriteTimeUtc(path, t);
        return path;
    }

    public string Dir(string rel) => Directory.CreateDirectory(P(rel)).FullName;

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
