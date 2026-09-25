using Compression.Lib;
using Compression.Registry;
using FileFormat.MacBinary;
using FileFormat.Zstd;
using FileFormat.Zip;
using FileFormat.Ewf;
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
  public void LegacyOptimize_IsAvailableOnlyForOneExplicitRewriteEffect() {
    Assert.Multiple(() => {
      Assert.That(OptimizationCapabilities.CanLegacyOptimizeUnambiguously(new MacBinaryFormatDescriptor()), Is.True,
        "MacBinary maps legacy Optimize to canonicalization only");
      Assert.That(OptimizationCapabilities.CanLegacyOptimizeUnambiguously(new ZstdFormatDescriptor()), Is.True,
        "Zstd maps legacy Optimize to compression only");
      Assert.That(OptimizationCapabilities.CanLegacyOptimizeUnambiguously(new ZipFormatDescriptor()), Is.False,
        "ZIP exposes both compression and repack, so generic Optimize is ambiguous");
      Assert.That(OptimizationCapabilities.CanLegacyOptimizeUnambiguously(new EwfFormatDescriptor()), Is.False,
        "EWF exposes compression and canonicalization, so generic Optimize is ambiguous");
    });
  }

  [Test, Category("Architecture")]
  public void LegacyDescriptorOptimizeFlag_IsStreamCompressionOnly() {
    FormatRegistration.EnsureInitialized();

    var offenders = FormatRegistry.All
      .Where(descriptor => descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize))
      .Where(descriptor => descriptor is not IStreamFormatOperations
                           || descriptor is not ICompressionOptimizable)
      .Select(descriptor => descriptor.Id)
      .OrderBy(id => id, StringComparer.Ordinal)
      .ToArray();

    Assert.That(offenders, Is.Empty,
      "Descriptor-wide SupportsOptimize is legacy stream-compression metadata only; "
      + "non-stream maintenance must use the explicit capability interfaces: "
      + string.Join(", ", offenders));
  }

  [Test, Category("Architecture")]
  public void CompoundTarCompression_IsAnExplicitDescriptorCapability() {
    FormatRegistration.EnsureInitialized();

    var compound = FormatRegistry.All
      .Where(static descriptor => descriptor.Category == FormatCategory.CompoundTar)
      .ToArray();

    Assert.That(compound, Is.Not.Empty);
    Assert.Multiple(() => {
      foreach (var descriptor in compound) {
        Assert.That(descriptor, Is.InstanceOf<ICompressionOptimizable>(),
          $"{descriptor.Id} must expose compression on the compound descriptor itself");
        Assert.That(OptimizationCapabilities.CanCompress(descriptor), Is.True,
          $"{descriptor.Id} compression discovery must not depend on outer-codec inference");
      }
    });
  }

  [Test, Category("RoundTrip")]
  public void CompoundTar_Compress_RoundTripsThroughExplicitCapability() {
    var directory = Path.Combine(Path.GetTempPath(), "cwb_targz_opt_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(directory);
    try {
      var payloadPath = Path.Combine(directory, "payload.txt");
      var inputPath = Path.Combine(directory, "input.tar.gz");
      var outputPath = Path.Combine(directory, "output.tar.gz");
      var extractDir = Path.Combine(directory, "out");
      var payload = new string('A', 8192) + " compound tar sentinel";
      File.WriteAllText(payloadPath, payload);

      ArchiveOperations.Create(
        inputPath,
        [new ArchiveInput(payloadPath, "payload.txt")],
        new CompressionOptions(),
        FormatDetector.Format.TarGz);

      var result = ArchiveOperations.Compress(inputPath, outputPath, password: null);
      ArchiveOperations.Extract(outputPath, extractDir, password: null, files: null);

      Assert.Multiple(() => {
        Assert.That(result.EntriesOptimized, Is.EqualTo(1));
        Assert.That(File.ReadAllText(Path.Combine(extractDir, "payload.txt")), Is.EqualTo(payload));
      });
    } finally {
      if (Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
    }
  }

  [Test, Category("Architecture")]
  public void LegacyOptimize_RejectsAmbiguousZipRewrite() {
    var directory = Path.Combine(Path.GetTempPath(), "cwb_legacy_opt_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(directory);
    try {
      var payloadPath = Path.Combine(directory, "payload.txt");
      var inputPath = Path.Combine(directory, "input.zip");
      var outputPath = Path.Combine(directory, "output.zip");
      File.WriteAllText(payloadPath, "ambiguous optimize sentinel");

      ArchiveOperations.Create(
        inputPath,
        [new ArchiveInput(payloadPath, "payload.txt")],
        new CompressionOptions(),
        FormatDetector.Format.Zip);

      var ex = Assert.Throws<NotSupportedException>(
        () => ArchiveOperations.Optimize(inputPath, outputPath, password: null));

      Assert.That(ex!.Message, Does.Contain("ambiguous"));
      Assert.That(File.Exists(outputPath), Is.False);
    } finally {
      if (Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
    }
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
  public void ExplicitBlockMovers_AreWiredToConcreteDefragmenters() {
    FormatRegistration.EnsureInitialized();

    var offenders = FormatRegistry.All
      .Where(static descriptor => descriptor is IArchiveDefragmentable)
      .Where(OptimizationCapabilities.HasFilesystemBlockMover)
      .Where(static descriptor => !OptimizationCapabilities.HasConcreteExtentDefragmenter(descriptor))
      .Select(static descriptor => descriptor.Id)
      .OrderBy(static id => id, StringComparer.Ordinal)
      .ToArray();

    Assert.That(offenders, Is.Empty,
      "A declared filesystem block mover must be wired to a concrete descriptor defragmenter; "
      + "the generic rebuild default is not physical extent defragmentation: "
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
