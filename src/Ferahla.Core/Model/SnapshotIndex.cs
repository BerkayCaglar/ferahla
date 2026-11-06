namespace Ferahla.Core.Model;

/// <summary>Read-side helper over a <see cref="Snapshot"/>: full paths, children, path lookup.</summary>
public sealed class SnapshotIndex
{
    private readonly string[] _paths;
    private readonly List<int>[] _children;
    private readonly List<int>[] _files;
    private readonly Dictionary<string, int> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public Snapshot Snapshot { get; }

    public SnapshotIndex(Snapshot snapshot)
    {
        Snapshot = snapshot;
        var dirs = snapshot.Dirs;
        _paths = new string[dirs.Count];
        _children = new List<int>[dirs.Count];
        _files = new List<int>[dirs.Count];
        for (var i = 0; i < dirs.Count; i++)
        {
            var d = dirs[i];
            // Parents are always written before children, so the parent path is ready.
            _paths[i] = d.Parent < 0 ? NormalizeRoot(snapshot.Root) : PathUtil.Join(_paths[d.Parent], d.Name);
            _byPath[_paths[i]] = i;
            if (d.Parent >= 0) (_children[d.Parent] ??= []).Add(i);
        }
        for (var i = 0; i < snapshot.Files.Count; i++)
        {
            var dir = snapshot.Files[i].Dir;
            (_files[dir] ??= []).Add(i);
        }
    }

    public string PathOf(int dir) => _paths[dir];
    public string PathOf(FileEntry f) => PathUtil.Join(_paths[f.Dir], f.Name);
    public DirEntry Dir(int i) => Snapshot.Dirs[i];
    public IReadOnlyList<int> Children(int dir) => (IReadOnlyList<int>?)_children[dir] ?? [];
    public IReadOnlyList<int> FilesIn(int dir) => (IReadOnlyList<int>?)_files[dir] ?? [];
    public int Count => _paths.Length;

    /// <summary>Index of the directory at <paramref name="path"/>, or -1 if it was not kept in the snapshot.</summary>
    public int Find(string path) => _byPath.TryGetValue(PathUtil.Normalize(path), out var i) ? i : -1;

    /// <summary>Deepest kept directory that contains <paramref name="path"/> (or is it).</summary>
    public int FindNearest(string path)
    {
        var p = PathUtil.Normalize(path);
        while (true)
        {
            if (_byPath.TryGetValue(p, out var i)) return i;
            var parent = PathUtil.Parent(p);
            if (parent is null) return -1;
            p = parent;
        }
    }

    public IEnumerable<int> Descendants(int dir)
    {
        var stack = new Stack<int>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            yield return d;
            foreach (var c in Children(d)) stack.Push(c);
        }
    }

    public int Depth(int dir)
    {
        var depth = 0;
        for (var p = Snapshot.Dirs[dir].Parent; p >= 0; p = Snapshot.Dirs[p].Parent) depth++;
        return depth;
    }

    private static string NormalizeRoot(string root) => PathUtil.Normalize(root);
}
