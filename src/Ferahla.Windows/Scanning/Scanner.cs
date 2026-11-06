using System.Diagnostics;
using System.IO.Enumeration;
using Ferahla.Core;
using Ferahla.Core.Model;
using Ferahla.Core.Storage;

namespace Ferahla.Windows.Scanning;

public sealed class ScanOptions
{
    public required string Root { get; init; }
    /// <summary>Directories smaller than this are folded into their parent in the snapshot.</summary>
    public long MinDirBytes { get; init; } = 1L << 20;
    public long BigFileBytes { get; init; } = 50L << 20;
    /// <summary>Subdirectories are scanned in parallel down to this depth.</summary>
    public int ParallelDepth { get; init; } = 3;
    public int MaxDegreeOfParallelism { get; init; } = Math.Max(2, Environment.ProcessorCount);
    public IReadOnlyDictionary<string, string>? KnownFolders { get; init; }
    public IProgress<ScanProgress>? Progress { get; init; }
    public CancellationToken Cancellation { get; init; }
}

public readonly record struct ScanProgress(long Files, long Dirs, long Bytes, string? Current);

/// <summary>
/// Walks one volume with <see cref="FileSystemEnumerable{TResult}"/> (NtQueryDirectoryFile, large buffer):
/// size and attributes come with the listing, no per-file stat. Reparse-point directories are not entered.
/// Cloud-only placeholders count as 0 bytes on disk. Sparse/compressed files use their allocated size.
/// Known limitation: hardlinked files (e.g. WinSxS) are counted once per link.
/// </summary>
public sealed class Scanner
{
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes CloudMask = RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline;

    private readonly ScanOptions _o;
    private readonly string _root;
    private readonly FileClassifier _classifier;
    private long _files, _dirs, _bytes, _cloudFiles, _cloudBytes, _reparse, _denied;
    private long _lastReport;

    public Scanner(ScanOptions options)
    {
        _o = options;
        _root = PathUtil.Normalize(options.Root);
        _classifier = new FileClassifier(options.KnownFolders ?? new Dictionary<string, string>(), options.BigFileBytes);
    }

    public Snapshot Run()
    {
        var sw = Stopwatch.StartNew();
        var started = DateTimeOffset.Now;
        var root = new Node(null, _root, _classifier.ContextFor(_root, null));
        try { ScanDir(root, _root, 0); }
        catch (AggregateException ae) when (ae.Flatten().InnerExceptions.All(e => e is OperationCanceledException))
        {
            throw new OperationCanceledException(_o.Cancellation);
        }
        sw.Stop();

        var snap = new Snapshot
        {
            Id = SnapshotStore.NewId(started, _root),
            ToolVersion = typeof(Scanner).Assembly.GetName().Version?.ToString(3) ?? "",
            ScannedAt = started,
            ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
            Root = _root,
            MinDirBytes = _o.MinDirBytes,
            BigFileBytes = _o.BigFileBytes,
            Stats = new ScanStats
            {
                Files = _files, Dirs = _dirs, ScannedBytes = _bytes,
                CloudOnlyFiles = _cloudFiles, CloudOnlyBytes = _cloudBytes,
                ReparsePointsSkipped = _reparse, AccessDeniedDirs = _denied,
            },
        };
        if (_o.KnownFolders is not null)
            foreach (var (k, v) in _o.KnownFolders) snap.KnownFolders[k] = v;
        Export(root, snap);
        return snap;
    }

    private void ScanDir(Node node, string path, int depth)
    {
        _o.Cancellation.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _dirs);
        List<(string Name, FileAttributes Attr)>? subdirs = null;
        try
        {
            var e = new FileSystemEnumerable<Item>(path, Transform, Enumeration)
            {
                ShouldIncludePredicate = static (ref FileSystemEntry _) => true,
            };
            foreach (var it in e)
            {
                if (it.IsDir)
                {
                    if (it.Name is ".git") node.Markers |= DirMarkers.Git;
                    else if (it.Name is "Assets") node.Markers |= DirMarkers.UnityAssets;
                    else if (it.Name is "ProjectSettings") node.Markers |= DirMarkers.UnityProjectSettings;
                    if ((it.Attr & FileAttributes.ReparsePoint) != 0)
                    {
                        Interlocked.Increment(ref _reparse);
                        continue;
                    }
                    (subdirs ??= []).Add((it.Name!, it.Attr));
                    continue;
                }
                AddFile(node, path, it);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            node.Flags |= DirFlags.AccessDenied;
            Interlocked.Increment(ref _denied);
        }

        if (subdirs is not null)
        {
            var children = new Node[subdirs.Count];
            for (var i = 0; i < subdirs.Count; i++)
            {
                var (name, attr) = subdirs[i];
                var childPath = PathUtil.Join(path, name);
                children[i] = new Node(node, name, _classifier.ContextFor(childPath, node.Context))
                {
                    Flags = ((attr & FileAttributes.Hidden) != 0 ? DirFlags.Hidden : 0) | ((attr & FileAttributes.System) != 0 ? DirFlags.System : 0),
                };
            }
            if (depth < _o.ParallelDepth && children.Length > 1)
            {
                Parallel.For(0, children.Length, new ParallelOptions { MaxDegreeOfParallelism = _o.MaxDegreeOfParallelism },
                    i => ScanDir(children[i], PathUtil.Join(path, children[i].Name), depth + 1));
            }
            else
            {
                foreach (var c in children) ScanDir(c, PathUtil.Join(path, c.Name), depth + 1);
            }
            node.Children = children;
            foreach (var c in children)
            {
                node.Size += c.Size;
                node.FileCount += c.FileCount;
                if (c.MaxTicks > node.MaxTicks) node.MaxTicks = c.MaxTicks;
                if ((c.Flags & DirFlags.HasCloudOnly) != 0) node.Flags |= DirFlags.HasCloudOnly;
            }
        }
        node.Size += node.Own;
        node.FileCount += node.OwnCount;
        Report(path);
    }

    private void AddFile(Node node, string dirPath, in Item it)
    {
        var size = it.Length;
        var flags = FileFlags.None;
        if ((it.Attr & CloudMask) != 0 && (it.Attr & FileAttributes.Directory) == 0)
        {
            flags |= FileFlags.CloudOnly;
            Interlocked.Increment(ref _cloudFiles);
            Interlocked.Add(ref _cloudBytes, it.Length);
            node.Flags |= DirFlags.HasCloudOnly;
            size = 0;
        }
        else if ((it.Attr & (FileAttributes.SparseFile | FileAttributes.Compressed)) != 0 && it.Length >= (1 << 20))
        {
            flags |= (it.Attr & FileAttributes.SparseFile) != 0 ? FileFlags.Sparse : FileFlags.Compressed;
            var name = it.Name ?? "";
            if (name.Length > 0)
            {
                var alloc = Native.GetAllocatedSize(PathUtil.Join(dirPath, name));
                if (alloc >= 0) size = alloc;
            }
        }
        node.Own += size;
        node.OwnCount++;
        if (it.Ticks > node.MaxTicks) node.MaxTicks = it.Ticks;
        Interlocked.Increment(ref _files);
        Interlocked.Add(ref _bytes, size);

        node.Markers |= it.Marker;
        var notable = it.Notable;
        if (node.Parent is null) notable |= FileFlags.RootFile;
        notable |= _classifier.Contextual(it.Ext, node.Context, it.Length);
        if (notable != 0 && it.Name is not null)
        {
            if ((it.Attr & FileAttributes.Hidden) != 0) flags |= FileFlags.Hidden;
            if ((it.Attr & FileAttributes.System) != 0) flags |= FileFlags.System;
            if ((it.Attr & FileAttributes.ReadOnly) != 0) flags |= FileFlags.ReadOnly;
            (node.Files ??= []).Add(new FileEntry
            {
                Name = it.Name, Size = size, Length = it.Length, LastWriteTicks = it.Ticks, Flags = flags | notable,
            });
        }
    }

    private void Report(string path)
    {
        if (_o.Progress is null) return;
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastReport);
        if (now - last < 250 || Interlocked.CompareExchange(ref _lastReport, now, last) != last) return;
        _o.Progress.Report(new ScanProgress(Interlocked.Read(ref _files), Interlocked.Read(ref _dirs), Interlocked.Read(ref _bytes), path));
    }

    /// <summary>Pre-order flatten; small directories fold into their nearest kept ancestor.</summary>
    private void Export(Node root, Snapshot snap)
    {
        var stack = new Stack<(Node Node, int Parent, int KeptAncestor, string RelPrefix)>();
        stack.Push((root, -1, -1, ""));
        while (stack.Count > 0)
        {
            var (n, parent, keptAncestor, rel) = stack.Pop();
            var keep = n.Parent is null || n.Size >= _o.MinDirBytes;
            int myIndex;
            string myRel;
            if (keep)
            {
                myIndex = snap.Dirs.Count;
                snap.Dirs.Add(new DirEntry
                {
                    Parent = parent,
                    Name = n.Parent is null ? "" : rel + n.Name,
                    Size = n.Size, OwnSize = n.Own, FileCount = n.FileCount, LastWriteTicks = n.MaxTicks,
                    Markers = n.Markers, Flags = n.Flags,
                });
                myRel = "";
            }
            else
            {
                myIndex = keptAncestor;
                myRel = rel + n.Name + "\\";
            }
            if (n.Files is not null)
                foreach (var f in n.Files)
                {
                    f.Dir = myIndex;
                    if (!keep) f.Name = myRel + f.Name;
                    snap.Files.Add(f);
                }
            if (n.Children is null) continue;
            // Reverse push keeps listing order. A child is never larger than its parent, so everything
            // under a folded directory folds too and attaches to the same kept ancestor.
            for (var i = n.Children.Length - 1; i >= 0; i--)
                stack.Push(keep ? (n.Children[i], myIndex, myIndex, "") : (n.Children[i], keptAncestor, keptAncestor, myRel));
        }
    }

    // ---- enumeration ----

    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false, // we want to know about access-denied directories
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
        MatchType = MatchType.Simple,
    };

    private Item Transform(ref FileSystemEntry e)
    {
        var name = e.FileName;
        if (e.IsDirectory)
            return new Item { IsDir = true, Name = name.ToString(), Attr = e.Attributes };

        var ext = FileClassifier.Classify(name, out var marker);
        var notable = FileClassifier.Intrinsic(ext, e.Length, _o.BigFileBytes);
        var needName = notable != 0 || ext != FileExt.None
                       || (e.Attributes & (FileAttributes.SparseFile | FileAttributes.Compressed)) != 0;
        return new Item
        {
            IsDir = false,
            Name = needName ? name.ToString() : null,
            Attr = e.Attributes,
            Length = e.Length,
            Ticks = e.LastWriteTimeUtc.UtcTicks,
            Marker = marker,
            Ext = ext,
            Notable = notable,
        };
    }

    private struct Item
    {
        public bool IsDir;
        public string? Name;
        public FileAttributes Attr;
        public long Length;
        public long Ticks;
        public DirMarkers Marker;
        public FileExt Ext;
        public FileFlags Notable;
    }

    private sealed class Node(Node? parent, string name, PathContext context)
    {
        public Node? Parent { get; } = parent;
        public string Name { get; } = name;
        public PathContext Context { get; } = context;
        public long Own, Size, OwnCount, FileCount, MaxTicks;
        public DirMarkers Markers;
        public DirFlags Flags;
        public Node[]? Children;
        public List<FileEntry>? Files;
    }
}
