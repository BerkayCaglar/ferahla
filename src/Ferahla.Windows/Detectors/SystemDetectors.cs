using Ferahla.Core;
using Ferahla.Core.Findings;
using Ferahla.Core.Model;
using Ferahla.Core.Rules;
using Ferahla.Core.Text;

namespace Ferahla.Windows.Detectors;

/// <summary>hiberfil.sys at the system drive root: turn hibernation off instead of deleting the file.</summary>
public sealed class HiberfilDetector : IDetector
{
    public string Name => "hiberfil";

    public IEnumerable<Finding> Detect(DetectorContext ctx, Rule rule)
    {
        if (!ctx.Snapshot.Volume.IsSystem && ctx.Snapshot.Root != ctx.Var("SystemDrive")) yield break;
        var ix = ctx.Index;
        foreach (var fi in ix.FilesIn(0))
        {
            var file = ix.Snapshot.Files[fi];
            if (!file.Name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase) || file.Size < rule.MinBytes) continue;
            var f = FindingsEngine.NewFinding(rule, ix.PathOf(file));
            f.Items.Add(FindingsEngine.FileItem(ix, file));
            yield return f;
        }
    }
}

/// <summary>
/// Virtual disks (WSL ext4.vhdx, emulator and app VM images). In use → not suggested at all.
/// Otherwise information only: deleting a VM disk deletes everything inside it.
/// </summary>
public sealed class VmImagesDetector : IDetector
{
    public string Name => "vm-images";

    private static readonly string[] Extensions = [".vhdx", ".vhd", ".vmdk", ".qcow2"];

    public IEnumerable<Finding> Detect(DetectorContext ctx, Rule rule)
    {
        var ix = ctx.Index;
        var programs = new[] { ctx.Var("ProgramFiles"), ctx.Var("ProgramFilesX86") };
        // Disks of one VM sit in one folder (e.g. an emulator's Data.vhdx + Root.vhd): one finding per folder.
        var disks = DetectorUtil.FilesWith(ctx.Snapshot, FileFlags.Big)
            .Where(f => Extensions.Any(e => f.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .Select(f => (File: f, Path: ix.PathOf(f)))
            .Where(x => !DetectorUtil.IsUnderAny(x.Path, programs)) // shipped with a program (e.g. WSL's own system.vhd)
            .GroupBy(x => PathUtil.Parent(x.Path) ?? x.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var vm in disks)
        {
            var list = vm.OrderByDescending(x => x.File.Size).ToList();
            var wsl = list.Any(x => x.File.Name.EndsWith("ext4.vhdx", StringComparison.OrdinalIgnoreCase));
            var name = wsl ? "WSL" : list.Count == 1 ? Path.GetFileName(list[0].File.Name) : VmName(vm.Key, ctx);
            var f = FindingsEngine.NewFinding(rule, list[0].Path, new() { ["name"] = name });
            foreach (var x in list) f.Items.Add(FindingsEngine.FileItem(ix, x.File));
            if (ctx.Live && list.Any(x => DetectorUtil.IsInUse(x.Path)))
            {
                f.Risk = Risk.Protected;
                f.Evidence.Add(Lang.T("In use right now (a VM or WSL is running)", "Şu anda kullanımda (bir sanal makine ya da WSL çalışıyor)"));
            }
            if (wsl)
                f.Evidence.Add(Lang.T("The disk only grows. Freeing space inside WSL does not shrink it; there is no documented one-step way to compact it yet.",
                                      "Bu disk sadece büyür. WSL içinde yer açmak onu küçültmez; şimdilik belgelenmiş tek adımlık bir küçültme yolu yok."));
            yield return f;
        }
    }

    /// <summary>The app folder a VM's disks belong to: first folder below ProgramData/AppData/LocalAppData.</summary>
    private static string VmName(string dir, DetectorContext ctx)
    {
        foreach (var root in new[] { ctx.Var("ProgramData"), ctx.Var("LocalAppData"), ctx.Var("AppData") })
            if (root is not null && PathUtil.IsUnder(dir, root) && dir.Length > root.Length + 1)
                return dir[(root.Length + 1)..].Split('\\')[0];
        return PathUtil.Name(dir);
    }
}

/// <summary>Used space the scan could not see (system files, other users, shadow copies). Info only.</summary>
public sealed class UnaccountedDetector : IDetector
{
    public string Name => "unaccounted";

    private const long Threshold = 5L << 30;

    public IEnumerable<Finding> Detect(DetectorContext ctx, Rule rule)
    {
        var s = ctx.Snapshot;
        if (PathUtil.Normalize(s.Root).Length != 3 || s.Volume.TotalBytes == 0) yield break;
        var unseen = s.Volume.TotalBytes - s.Volume.FreeBytes - s.Stats.ScannedBytes;
        if (unseen < Threshold) yield break;
        var f = FindingsEngine.NewFinding(rule, s.Root + "|unaccounted");
        f.Bytes = unseen;
        f.Evidence.Add(Lang.T($"{Fmt.Count(s.Stats.AccessDeniedDirs, Lang.Culture)} folders could not be read", $"{Fmt.Count(s.Stats.AccessDeniedDirs, Lang.Culture)} klasör okunamadı"));
        yield return f;
    }
}

/// <summary>WinSxS: information and the safe DISM command; never touched directly.</summary>
public sealed class ComponentStoreDetector : IDetector
{
    public string Name => "component-store";

    public IEnumerable<Finding> Detect(DetectorContext ctx, Rule rule)
    {
        if (ctx.Var("Windows") is not { } win) yield break;
        var d = ctx.Index.Find(PathUtil.Join(win, "WinSxS"));
        if (d < 0) yield break;
        if (ctx.Index.Dir(d).Size < rule.MinBytes) yield break;
        var f = FindingsEngine.NewFinding(rule, ctx.Index.PathOf(d));
        f.Items.Add(FindingsEngine.DirItem(ctx.Index, d));
        f.Evidence.Add(Lang.T("Size shown counts hardlinks more than once; the real size is smaller. DISM /AnalyzeComponentStore shows it.",
                              "Gösterilen boyut hardlink'leri birden çok kez sayar; gerçek boyut daha küçük. DISM /AnalyzeComponentStore gerçeğini gösterir."));
        yield return f;
    }
}
