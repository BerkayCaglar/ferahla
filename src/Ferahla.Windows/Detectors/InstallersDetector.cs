using Ferahla.Core;
using Ferahla.Core.Findings;
using Ferahla.Core.Model;
using Ferahla.Core.Rules;
using Ferahla.Core.Text;

namespace Ferahla.Windows.Detectors;

/// <summary>
/// Installers in Downloads/Desktop, grouped by product. If the product is installed, every installer is suggested
/// except the newest one when it is newer than the registry's version (it may be an update not installed yet).
/// The registry version is only used for that one decision: apps that update in place often keep a stale
/// DisplayVersion (e.g. "1.4.2" while 4.x runs). If it isn't installed, only older versions of a product with
/// several installers are suggested, at review level. An archive with the same stem as a suggested installer
/// (setup.exe + setup.zip) is added to the same finding.
/// </summary>
public sealed class InstallersDetector : IDetector
{
    public string Name => "installers";

    public IEnumerable<Finding> Detect(DetectorContext ctx, Rule rule)
    {
        var ix = ctx.Index;
        var s = ctx.Snapshot;
        var dlDesk = new[] { ctx.Var("Downloads"), ctx.Var("Desktop") };
        // Loose files the user downloaded: at most two folders below Downloads/Desktop and not inside a project or
        // build output (a project's bin\*.exe or a bundled vc_redist is not a downloaded installer).
        bool Loose(FileEntry f)
        {
            if (ctx.InProject(f.Dir)) return false;
            var path = ix.PathOf(f);
            var root = dlDesk.FirstOrDefault(r => r is not null && PathUtil.IsUnder(path, r));
            return root is not null && path.AsSpan(root.Length).Count('\\') <= 3;
        }
        var installers = DetectorUtil.FilesWith(s, FileFlags.Installer).Where(Loose).ToList();
        if (installers.Count == 0) yield break;
        var archives = DetectorUtil.FilesWith(s, FileFlags.Archive).Where(Loose).ToList();
        var apps = s.Apps.Select(a => (App: a, Key: DetectorUtil.AppKey(a.Name))).Where(a => a.Key.Length >= 3).ToList();

        foreach (var group in installers.GroupBy(f => DetectorUtil.ProductKey(f.Name)).Where(g => g.Key.Length >= 3))
        {
            var files = group.Select(f => (File: f, Version: DetectorUtil.VersionIn(Path.GetFileName(f.Name))))
                             .OrderBy(x => x.Version ?? new Version(0, 0)).ThenBy(x => x.File.LastWriteTicks).ToList();
            var app = apps.Where(a => DetectorUtil.SameProduct(group.Key, a.Key))
                          .OrderByDescending(a => DetectorUtil.VersionIn(a.App.Version ?? "") ?? new Version(0, 0))
                          .Select(a => a.App).FirstOrDefault();

            List<FileEntry> suggest;
            var risk = rule.Risk;
            var evidence = new List<string>();
            if (app is not null)
            {
                var installed = DetectorUtil.VersionIn(app.Version ?? "");
                var newest = files[^1];
                var keepNewest = installed is null || newest.Version is null || newest.Version > installed;
                suggest = files.Select(x => x.File).ToList();
                if (keepNewest && files.Count > 1) suggest.RemoveAt(suggest.Count - 1);
                // A single installer newer than what is installed may be an update waiting: leave it.
                if (keepNewest && files.Count == 1 && installed is not null && newest.Version is not null) suggest.Clear();
                var kept = files.Count - suggest.Count;
                // Some display names already carry the version ("Example Tool 9.0.0").
                var shown = app.Version is { Length: > 0 } v && !app.Name.Contains(v, StringComparison.OrdinalIgnoreCase) ? $"{app.Name} {v}" : app.Name;
                evidence.Add(Lang.T($"Installed: {shown}", $"Kurulu: {shown}"));
                if (kept > 0) evidence.Add(Lang.T($"The newest installer stays: {Path.GetFileName(newest.File.Name)}", $"En yeni kurulum dosyası kalıyor: {Path.GetFileName(newest.File.Name)}"));
            }
            else
            {
                if (files.Count < 2) continue;
                suggest = files.Take(files.Count - 1).Select(x => x.File).ToList();
                risk = Risk.Review;
                evidence.Add(Lang.T($"{files.Count} versions of the same installer; the newest stays. Not found among installed apps.",
                                    $"Aynı kurulumun {files.Count} sürümü var; en yenisi kalıyor. Kurulu uygulamalarda bulunamadı."));
            }
            if (suggest.Count == 0) continue;

            var stems = suggest.Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var twins = archives.Where(a => stems.Contains(Path.GetFileNameWithoutExtension(a.Name))).ToList();

            var product = app?.Name ?? DetectorUtil.Title(group.Key, null);
            var count = suggest.Count + twins.Count;
            var f = FindingsEngine.NewFinding(rule, ix.PathOf(suggest[0]), new() { ["product"] = product, ["count"] = count.ToString(Lang.Culture) });
            f.Risk = risk;
            foreach (var file in suggest.Concat(twins)) f.Items.Add(FindingsEngine.FileItem(ix, file));
            if (twins.Count > 0) evidence.Add(Lang.T("Same installer also kept as an archive", "Aynı kurulum bir de arşiv olarak duruyor"));
            f.Evidence.AddRange(evidence);
            yield return f;
        }
    }
}
