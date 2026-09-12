#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Gpfs;

/// <summary>
/// Structural-inspection descriptor for IBM Storage Scale (GPFS) Network Shared
/// Disk images.
///
/// <para>
/// IBM documents NSD v2 as a GPT disk with a single GPFS partition. That outer
/// structure is public and safe to recognize. The byte layout of the GPFS inode,
/// directory and allocation metadata inside the partition is not publicly
/// specified sufficiently to implement an independent offline file walker or
/// editor, so this descriptor deliberately does not advertise create/modify or
/// destructive maintenance operations.
/// </para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.ibm.com/docs/en/storage-scale</c> — IBM Storage Scale documentation; NSD v1/v2 creation and GPT behavior</description></item>
///   <item><description><c>https://qnx.com/developers/docs/7.1/com.qnx.doc.neutrino.utilities/topic/d/diskimage_config_file.html</c> — QNX diskimage GPT type table, including the IBM GPFS partition GUID</description></item>
/// </list>
/// </summary>
public sealed class GpfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, ILayoutOptimizable {

  /// <summary>Gets the id.</summary>
  public string Id => "Gpfs";

  /// <summary>Gets the display name.</summary>
  public string DisplayName => "IBM Storage Scale / GPFS";

  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Archive;

  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;

  /// <summary>Gets the default extension.</summary>
  public string DefaultExtension => ".gpfs";

  /// <summary>Gets the extensions.</summary>
  public IReadOnlyList<string> Extensions => [".gpfs"];

  /// <summary>Gets the compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>Gets the magic signatures.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // NSD v2 uses GPT with a single GPFS partition. In the canonical GPT layout
    // the first partition entry begins at LBA 2 (offset 1024) and starts with
    // this mixed-endian type GUID. GpfsDetectionSource additionally recognizes
    // the GPFS type when it appears in another entry of the standard GPT table.
    new(GpfsReader.GpfsPartitionTypeGuidBytes, Offset: 1024, Confidence: 0.98),
  ];

  /// <summary>Gets the methods.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];

  /// <summary>Gets the tar compression format id.</summary>
  public string? TarCompressionFormatId => null;

  /// <summary>Gets the family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <summary>Gets the description.</summary>
  public string Description =>
    "IBM Storage Scale / GPFS — Stage-0 structural inspection. NSD v2 GPT envelopes and the " +
    "IBM GPFS partition GUID are recognized, while a historical workbench descriptor signature " +
    "is retained only as an explicit-format compatibility fallback. Promotion to filesystem R/O " +
    "or R/W is deferred because the inode/directory/allocation byte layout is proprietary and a " +
    "complete filesystem may span multiple NSDs; no safe independent offline rewrite oracle is available.";

  /// <summary>Lists the synthetic structural entries in the supplied image.</summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new GpfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>Extracts the selected synthetic structural entries.</summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new GpfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    using var r = new GpfsReader(archive);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"GPFS entry not found: {entryName}");
    var data = r.Extract(entry);
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }

  /// <summary>
  /// Reports the public NSD envelope without reading the full image or inventing
  /// filesystem allocation geometry that is not derivable from public documentation.
  /// </summary>
  public LayoutAnalysis AnalyzeLayout(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("GPFS layout analysis requires a readable, seekable stream.", nameof(image));

    var originalPosition = image.Position;
    try {
      var prefixLength = (int)Math.Min(image.Length, GpfsDetectionSource.ProbeLength);
      var prefix = new byte[prefixLength];
      image.Position = 0;
      image.ReadExactly(prefix);

      var notes = new List<string>();
      if (GpfsDetectionSource.TryReadGpfsPartition(
            prefix,
            requireCompleteEntryTable: false,
            out var partitionOffset,
            out var partitionSize,
            out _)) {
        if (partitionSize <= 0 || partitionOffset < 0 || partitionOffset > image.Length - partitionSize)
          throw new InvalidDataException("GPFS: GPT partition range exceeds the image bounds.");
        notes.Add(
          $"NSD v2 GPT envelope detected; GPFS partition starts at byte {partitionOffset:N0} " +
          $"and spans {partitionSize:N0} byte(s).");
      } else if (prefix.AsSpan().StartsWith(GpfsReader.NsdMagic)) {
        notes.Add(
          "Historical workbench descriptor signature detected. It is retained for compatibility only and is not treated as a normative IBM disk signature.");
      } else if (GpfsDetectionSource.HasGptHeader(prefix)) {
        throw new InvalidDataException(
          $"GPFS: GPT is present but contains no {GpfsReader.GpfsPartitionTypeGuid:D} IBM GPFS partition in the probed entry table.");
      } else {
        throw new InvalidDataException("GPFS: no supported NSD envelope was found.");
      }

      notes.Add(
        "No allocation-unit, free-space, inode or directory geometry is claimed: public IBM documentation describes the NSD envelope and operational structures, not enough byte-level metadata layout for a safe independent offline rewrite.");
      notes.Add(
        "Compact, defrag, wipe, shrink, layout rebuild and purge remain disabled until a verifiable allocation/namespace implementation exists.");

      return new LayoutAnalysis {
        ImageSize = image.Length,
        CurrentUnitSize = 0,
        CurrentSlackBytes = 0,
        OptimalUnitSize = 0,
        OptimalSlackBytes = 0,
        Notes = notes,
      };
    } finally {
      image.Position = originalPosition;
    }
  }
}
