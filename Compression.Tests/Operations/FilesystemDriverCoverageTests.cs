using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

[TestFixture]
public sealed class FilesystemDriverCoverageTests {
  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  [Test]
  public void EveryFileSystemProjectHasACommonDriverPath() {
    var coverage = FormatRegistry.GetFilesystemDriverCoverage();

    Assert.That(coverage, Is.Not.Empty,
      "Source generation did not mark any FileSystem.* descriptors.");

    var missing = coverage
      .Where(item => !item.HasDriverPath)
      .Select(item => item.FormatId)
      .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
      .ToArray();

    Assert.That(missing, Is.Empty,
      "Every FileSystem.* descriptor must expose a native provider/sidecar or the safe read-only archive projection.");
  }

  [Test]
  public void FilesystemCoverageIdsResolveToDescriptors() {
    foreach (var id in FormatRegistry.FilesystemFormatIds) {
      var descriptor = FormatRegistry.GetById(id);
      Assert.That(descriptor, Is.Not.Null, $"Filesystem id '{id}' has no descriptor.");
      Assert.That(FormatRegistry.GetFilesystemDriverCoverage(id).FormatId,
        Is.EqualTo(descriptor!.Id).IgnoreCase);
    }
  }

  [Test]
  public void NativeSidecarsAreVisibleInCoverage() {
    var coverage = FormatRegistry.GetFilesystemDriverCoverage()
      .ToDictionary(item => item.FormatId, StringComparer.OrdinalIgnoreCase);

    foreach (var id in FormatRegistry.FilesystemFormatIds) {
      var sidecar = FormatRegistry.GetFilesystemDriver(id);
      if (sidecar == null) continue;
      Assert.That(coverage[id].Binding, Is.EqualTo(FilesystemDriverBindingKind.SidecarNative),
        $"Generated sidecar for '{id}' is not the selected common-driver binding.");
      Assert.That(coverage[id].HasNativeReadinessProvider, Is.True);
    }
  }

  [Test]
  public void ArchiveModifyNeverCountsAsMountedWriteByItself() {
    foreach (var item in FormatRegistry.GetFilesystemDriverCoverage()) {
      if (!item.HasArchiveMutation || item.IsNative) continue;
      Assert.That(item.Binding, Is.EqualTo(FilesystemDriverBindingKind.ArchiveProjection),
        $"'{item.FormatId}' only has archive mutation; it must remain a read-only mounted projection until a native provider exists.");
    }
  }
}
