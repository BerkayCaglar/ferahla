using Ferahla.Core.Findings;
namespace Ferahla.Windows.Detectors;

/// <summary>All coded detectors shipped with Ferahla.</summary>
public static class BuiltInDetectors
{
    public static IEnumerable<IDetector> All() =>
    [
        new InstallersDetector(),
        new ArchivesDetector(),
        new PartialsDetector(),
        new UnityEditorsDetector(),
        new CliVersionsDetector(),
        new PlaywrightDetector(),
        new WindowsSdkDetector(),
        new RepoClonesDetector(),
        new HiberfilDetector(),
        new VmImagesDetector(),
        new UnaccountedDetector(),
        new ComponentStoreDetector(),
        new DuplicatesDetector(),
    ];
}
