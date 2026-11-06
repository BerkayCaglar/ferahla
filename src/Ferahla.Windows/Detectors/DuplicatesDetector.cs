using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Ferahla.Core;
using Ferahla.Core.Findings;
using Ferahla.Core.Model;
using Ferahla.Core.Rules;
using Ferahla.Core.Text;
using Microsoft.Win32.SafeHandles;

namespace Ferahla.Windows.Detectors;

/// <summary>
/// Likely duplicates among the large loose files kept in the snapshot (user area, outside projects): same size, not
/// hardlinks of each other, and the same content at 18 places (first and last 1 MB plus 16 samples spread through the
/// file). Head and tail alone are not enough: stems of one song share length and often silent ends. Full-content
/// verification is a separate step (ferahla verify). One finding per set; the copy in the most settled place is kept
/// (outside Desktop/Downloads first, then the oldest).
/// </summary>
public sealed partial class DuplicatesDetector : IDetector
{
    private const int Edge = 1 << 20;
    private const int Samples = 16;
    private const int Sample = 64 * 1024;

    public string Name => "duplicates";

    public IEnumerable<Finding> Detect(DetectorContext ctx, Rule rule)
    {
        if (!ctx.Live) yield break;
        var ix = ctx.Index;
        var candidates = DetectorUtil.FilesWith(ctx.Snapshot, FileFlags.Big)
            .Where(f => (f.Flags & FileFlags.CloudOnly) == 0 && !ctx.InProject(f.Dir))
            .Select(f => (File: f, Path: ix.PathOf(f)))
            .Where(x => ctx.IsUserArea(x.Path))
            .GroupBy(x => x.File.Length)
            .Where(g => g.Count() > 1);

        var transient = new[] { ctx.Var("Desktop"), ctx.Var("Downloads") };
        foreach (var sizeGroup in candidates)
        {
            var probed = new List<(FileEntry File, string Path, string Hash, (uint, ulong) Id)>();
            foreach (var (file, path) in sizeGroup)
            {
                if (Identity(path) is not { } id || ProbeHash(path, file.Length) is not { } h) continue;
                probed.Add((file, path, h, id));
            }
            foreach (var same in probed.GroupBy(p => p.Hash).Where(g => g.Count() > 1))
            {
                // Hardlinks are the same file: not duplicates.
                var distinct = same.GroupBy(p => p.Id).Select(g => g.First()).ToList();
                if (distinct.Count < 2) continue;
                // Keep the copy that something probably depends on: outside Desktop/Downloads, without a "- Copy"
                // style suffix, deeper in a folder structure (a project's Samples\ rather than a loose file on the
                // Desktop), then the oldest.
                var keep = distinct
                    .OrderBy(p => DetectorUtil.IsUnderAny(p.Path, transient) ? 1 : 0)
                    .ThenBy(p => CopySuffixRx().IsMatch(Path.GetFileNameWithoutExtension(p.Path)) ? 1 : 0)
                    .ThenByDescending(p => p.Path.Count(ch => ch == '\\'))
                    .ThenBy(p => p.File.LastWriteTicks)
                    .First();
                var dups = distinct.Where(p => p.Path != keep.Path).ToList();
                var f = FindingsEngine.NewFinding(rule, keep.Path, new()
                {
                    ["name"] = Path.GetFileName(keep.Path),
                    ["count"] = dups.Count.ToString(Lang.Culture),
                });
                foreach (var d in dups) f.Items.Add(FindingsEngine.FileItem(ix, d.File));
                f.Evidence.Add(Lang.T($"Kept: {keep.Path}", $"Kalan: {keep.Path}"));
                f.Evidence.Add(Lang.T($"Same size; content matches at 18 places (start, end, 16 samples). Full check: ferahla verify {f.Id}",
                                      $"Boyut aynı; içerik 18 noktada eşleşiyor (baş, son, 16 örnek). Tam kontrol: ferahla verify {f.Id}"));
                yield return f;
            }
        }
    }

    // "kopya"/"kopyası": Turkish Windows names copies "x - Kopya".
    [System.Text.RegularExpressions.GeneratedRegex(@"([ _-]+(copy|kopya|kopyası)( \(?\d+\)?)?|[ _-]*\(\d+\))$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CopySuffixRx();

    internal static string? ProbeHash(string path, long length)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[Edge];
            void Read(long offset, int count)
            {
                fs.Position = offset;
                var n = fs.ReadAtLeast(buf.AsSpan(0, count), count, throwOnEndOfStream: false);
                sha.AppendData(buf, 0, n);
            }
            if (length <= 2L * Edge + Samples * (long)Sample)
            {
                Read(0, (int)Math.Min(length, buf.Length));
                for (var pos = (long)buf.Length; pos < length; pos += buf.Length) Read(pos, (int)Math.Min(buf.Length, length - pos));
            }
            else
            {
                Read(0, Edge);
                var span = length - 2L * Edge - Sample;
                for (var i = 0; i < Samples; i++) Read(Edge + span * i / (Samples - 1), Sample);
                Read(length - Edge, Edge);
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>(volume serial, file index): equal for hardlinks of the same file.</summary>
    internal static (uint, ulong)? Identity(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(h, out var info)
                ? (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow)
                : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // FILETIME is two DWORDs (4-byte aligned). Declaring it as long would add padding after FileAttributes and
    // shift every following field (see docs/spikes.md, spike 5). Size must be 52 bytes.
    [StructLayout(LayoutKind.Sequential)]
    internal struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);
}
