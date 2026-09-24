using Compression.Lib;
using Compression.Registry;
using FileFormat.MacBinary;
using FileFormat.Zstd;
using FileFormat.Zip;
using FileSystem.ApplePascal;
using FileSystem.Lif;
using FileSystem.Jfs1;
using FileSystem.ExFat;
using FileSystem.Fatx;
using FileSystem.CpcDsk;
using FileSystem.Btrfs;
using FileSystem.Mfs;
using FileSystem.Ntfs;
using FileSystem.Ods1;
using FileSystem.SquashFs;
using FileSystem.Stacker;

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
  public void SquashFs_CompressionBlockSize_IsNotAllocationGeometry() {
    var descriptor = new SquashFsFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(OptimizationCapabilities.CanCompress(descriptor), Is.True);
      Assert.That(OptimizationCapabilities.CanChangeAllocationGeometry(descriptor), Is.False);
      Assert.That(OptimizationCapabilities.GetAllocationGeometryOptions(descriptor), Is.Empty);
    });
  }

  [Test, Category("Architecture")]
  public void Ntfs_GeometryOptions_ExcludeCompressionAndMetadata() {
    var descriptor = new NtfsFormatDescriptor();
    var keys = OptimizationCapabilities.GetAllocationGeometryOptions(descriptor)
      .Select(static option => option.Key)
      .ToArray();

    Assert.Multiple(() => {
      Assert.That(OptimizationCapabilities.CanChangeAllocationGeometry(descriptor), Is.True);
      Assert.That(keys, Does.Contain("ImageSize"));
      Assert.That(keys, Does.Contain("ClusterSize"));
      Assert.That(keys, Does.Contain("MftRecordSize"));
      Assert.That(keys, Does.Not.Contain("VolumeLabel"));
      Assert.That(keys, Does.Not.Contain("Compression"));
      Assert.That(keys, Does.Not.Contain("Generate8Dot3"));
    });
  }

  [Test, Category("Architecture")]
  public void RebuildTransportAlone_DoesNotImplyAllocationGeometry() {
    Assert.Multiple(() => {
      Assert.That(OptimizationCapabilities.CanChangeAllocationGeometry(new Ods1FormatDescriptor()), Is.False,
        "a volume-label-only schema is metadata, not allocation geometry");
      Assert.That(OptimizationCapabilities.CanChangeAllocationGeometry(new StackerFormatDescriptor()), Is.False,
        "compatibility and compression choices are not allocation geometry");
    });
  }

  [Test, Category("Architecture")]
  public void HandWrittenGeometrySchemas_ExposeOnlyGeometryOptions() {
    static string[] Keys(IFormatDescriptor descriptor)
      => OptimizationCapabilities.GetAllocationGeometryOptions(descriptor)
        .Select(static option => option.Key)
        .OrderBy(static key => key, StringComparer.Ordinal)
        .ToArray();

    Assert.Multiple(() => {
      Assert.That(Keys(new Jfs1FormatDescriptor()),
        Is.EquivalentTo(new[] { "AggregateBlockSize", "BlockSize" }),
        "JFS1 volume label is metadata, not geometry");
      Assert.That(Keys(new ExFatFormatDescriptor()),
        Is.EquivalentTo(new[] { "ClusterSize", "ImageSize" }),
        "exFAT volume label is metadata, not geometry");
      Assert.That(Keys(new CpcDskFormatDescriptor()),
        Is.EquivalentTo(new[] { "Sides", "Tracks" }));
      Assert.That(Keys(new LifFormatDescriptor()),
        Is.EquivalentTo(new[] { "DirectorySectors" }),
        "LIF file type and volume label are not allocation geometry");
    });
  }

  [Test, Category("Architecture")]
  public void Btrfs_IgnoredWriterKnobs_DoNotAdvertiseGeometryChange() {
    var descriptor = new BtrfsFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(OptimizationCapabilities.CanChangeAllocationGeometry(descriptor), Is.False);
      Assert.That(OptimizationCapabilities.GetAllocationGeometryOptions(descriptor), Is.Empty);
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
  public void FilesystemCoverage_UsesExplicitComposedBlockMover() {
    FormatRegistration.EnsureInitialized();

    Assert.That(FormatRegistry.GetFilesystemDriverCoverage("Fatx").HasBlockMover, Is.True);
  }

  [Test, Category("Architecture")]
  public void BlockMover_IsRequiredForExtentDefragmentation() {
    var fatx = new FatxFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(OptimizationCapabilities.CanDefragmentExtents(new MfsFormatDescriptor()), Is.True);
      Assert.That(OptimizationCapabilities.CanDefragmentExtents(new ApplePascalFormatDescriptor()), Is.True,
        "Apple Pascal exposes its real extent mover separately from directory ordering");
      Assert.That(OptimizationCapabilities.CanDefragmentExtents(fatx), Is.True,
        "A real same-format mover used by the descriptor must remain an explicit physical-defrag capability");
      Assert.That(OptimizationCapabilities.GetFilesystemBlockMoverType(fatx), Is.EqualTo(typeof(FatxBlockMover)));
      Assert.That(OptimizationCapabilities.CanDefragmentExtents(new StackerFormatDescriptor()), Is.False,
        "Rebuild-only defragmentation must not masquerade as physical extent movement");
    });
  }
}
