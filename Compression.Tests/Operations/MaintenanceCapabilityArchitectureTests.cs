using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

[TestFixture]
public class MaintenanceCapabilityArchitectureTests {
  [OneTimeSetUp]
  public void InitializeRegistry() => FormatRegistration.EnsureInitialized();

  [Test]
  public void SupportsOptimizeFlag_NeverAdvertisesCompressionWithoutExplicitCapability() {
    var offenders = FormatRegistry.All
      .Where(descriptor => descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize))
      .Where(descriptor => descriptor is not ICompressionOptimizable)
      .Select(descriptor => descriptor.Id)
      .OrderBy(id => id, StringComparer.Ordinal)
      .ToArray();

    Assert.That(offenders, Is.Empty,
      "Legacy SupportsOptimize may remain only as a compatibility advertisement for " +
      "the explicit ICompressionOptimizable capability. It must not mean repack, " +
      "canonicalization, directory sorting, defragmentation, or geometry changes.");
  }

  [Test]
  public void LayoutCapability_IsNotInferredFromCreateAndSchema() {
    var zip = FormatRegistry.GetById("Zip");

    Assert.Multiple(() => {
      Assert.That(zip, Is.InstanceOf<IArchiveCreatable>());
      Assert.That(zip, Is.InstanceOf<IFormatOptionsSchema>());
      Assert.That(zip, Is.Not.InstanceOf<ILayoutOptimizable>(),
        "A creatable archive with tunable compression options is not an allocation-geometry optimizer.");
    });
  }

  [Test]
  public void RepackCapability_IsExplicitForSupportedContainers() {
    Assert.Multiple(() => {
      Assert.That(FormatRegistry.GetById("Zip"), Is.InstanceOf<IArchiveRepackable>());
      Assert.That(FormatRegistry.GetById("SevenZip"), Is.InstanceOf<IArchiveRepackable>());
    });
  }
}
