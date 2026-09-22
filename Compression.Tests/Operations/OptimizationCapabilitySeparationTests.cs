using Compression.Lib;
using Compression.Registry;
using FileFormat.MacBinary;
using FileFormat.Zstd;
using FileFormat.Zip;
using FileSystem.ApplePascal;
using FileSystem.Mfs;

namespace Compression.Tests.Operations;

/// <summary>
/// Regression coverage for the maintenance taxonomy: operations that rewrite bytes
/// for unrelated reasons must not leak into one generic "Optimize" capability.
/// </summary>
[TestFixture]
public sealed class OptimizationCapabilitySeparationTests {
  [Test, Category("Architecture")]
  public void MacBinary_IsCanonicalizable_NotCompressionOptimizable() {
    var descriptor = new MacBinaryFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(OptimizationCapabilities.CanCanonicalize(descriptor), Is.True);
      Assert.That(OptimizationCapabilities.CanCompress(descriptor), Is.False);
      Assert.That(OptimizationCapabilities.CanRepack(descriptor), Is.False);
      Assert.That(OptimizationCapabilities.CanChangeAllocationGeometry(descriptor), Is.False);
    });
  }

  [Test, Category("Architecture")]
  public void Zstd_IsCompressionOptimizable_NotCanonicalizableOrGeometry() {
    var descriptor = new ZstdFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(OptimizationCapabilities.CanCompress(descriptor), Is.True);
      Assert.That(OptimizationCapabilities.CanCanonicalize(descriptor), Is.False);
      Assert.That(OptimizationCapabilities.CanRepack(descriptor), Is.False);
      Assert.That(OptimizationCapabilities.CanChangeAllocationGeometry(descriptor), Is.False);
    });
  }

  [Test, Category("Architecture")]
  public void Zip_SeparatesCompressionFromRepack() {
    var descriptor = new ZipFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(OptimizationCapabilities.CanCompress(descriptor), Is.True);
      Assert.That(OptimizationCapabilities.CanRepack(descriptor), Is.True);
      Assert.That(OptimizationCapabilities.CanCanonicalize(descriptor), Is.False);
      Assert.That(OptimizationCapabilities.CanChangeAllocationGeometry(descriptor), Is.False);
    });
  }

  [Test, Category("Architecture")]
  public void ApplePascal_SeparatesDirectoryOrderingFromAllocationGeometry() {
    var descriptor = new ApplePascalFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(OptimizationCapabilities.CanSortDirectoryEntries(descriptor), Is.True);
      Assert.That(OptimizationCapabilities.CanChangeAllocationGeometry(descriptor), Is.True);
      Assert.That(OptimizationCapabilities.CanCompress(descriptor), Is.False);
      Assert.That(OptimizationCapabilities.CanCanonicalize(descriptor), Is.False);
    });
  }

  [Test, Category("Architecture")]
  public void LegacyStreamOptimizeClaims_HaveExplicitCompressionCapability() {
    FormatRegistration.EnsureInitialized();

    var offenders = FormatRegistry.All
      .Where(descriptor => descriptor is IStreamFormatOperations)
      .Where(descriptor =>
        descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize)
        || descriptor.Methods.Any(method => method.SupportsOptimize))
      .Where(descriptor => descriptor is not ICompressionOptimizable)
      .Select(descriptor => descriptor.Id)
      .OrderBy(id => id, StringComparer.Ordinal)
      .ToArray();

    Assert.That(offenders, Is.Empty,
      "Legacy stream optimizer claims must opt into ICompressionOptimizable: "
      + string.Join(", ", offenders));
  }

  [Test, Category("Architecture")]
  public void BlockMover_IsRequiredForExtentDefragmentation() {
    Assert.Multiple(() => {
      Assert.That(OptimizationCapabilities.CanDefragmentExtents(new MfsFormatDescriptor()), Is.True);
      Assert.That(OptimizationCapabilities.CanDefragmentExtents(new ApplePascalFormatDescriptor()), Is.False,
        "a descriptor that defragments through a private mover must not advertise the public block-mover capability");
    });
  }
}
