#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Gpfs;

/// <summary>
/// Stage 0 detection-only descriptor for IBM Spectrum Scale (GPFS) NSD
/// descriptor images. Surfaces only a synthetic <c>metadata.ini</c> and
/// the raw image bytes; no real file-walk is attempted.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.ibm.com/docs/en/storage-scale</c> — IBM Storage Scale (formerly Spectrum Scale / GPFS) official documentation, incl. NSD concepts</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/GPFS</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class GpfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Gpfs";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "IBM Spectrum Scale / GPFS";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".gpfs";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".gpfs"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 0x4347465C NSD descriptor magic at offset 0.
    new([0x43, 0x47, 0x46, 0x5C], Offset: 0, Confidence: 0.90),
  ];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
    /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  // Stage-0 deferral rationale (per CONTRIBUTING.md "Promotion rule"):
  //
  //   * Closed on-disk format. IBM has never published the GPFS / Spectrum Scale
  //     on-disk specification. The NSD descriptor's first 4-8 bytes (magic +
  //     trailing word) are the only fields routinely surfaced in IBM Redbooks
  //     ("SG24-7844", "SG24-8254") and disk-dump postmortems; the rest of the
  //     NSD descriptor, the storage-pool / failure-group topology, the inode
  //     table, the indirect-block format, and the directory format are
  //     proprietary and have no public spec.
  //
  //   * No single-disk content surface. GPFS is a cluster filesystem. The file
  //     namespace, allocation maps, and inode table live in the cluster
  //     manager and are striped across every NSD in the storage pool. A
  //     single .gpfs image is one shard of that stripe — even with a full
  //     on-disk spec it could not be walked in isolation, because the
  //     metadata path requires quorum from the other NSDs and the cluster
  //     configuration server.
  //
  //   * No fsck-style oracle. There is no IBM-published or third-party tool
  //     (open or closed) that operates on a single NSD image off the cluster.
  //     mmfsck, mmlsfs, mmlsnsd all require the live cluster manager.
  //     Without such an oracle, Stage-2 (WORM) and Stage-3 (R/W) acceptance
  //     gates per CONTRIBUTING.md §"Promotion rule" cannot be satisfied.
  //
  // Conclusion: stay Stage-0 indefinitely. Detection by NSD magic only;
  // List surfaces metadata.ini + the raw image. Promotion deferred until
  // IBM publishes the on-disk format or a sole-disk reverse-engineered
  // reader-with-validator emerges (neither exists as of 2026-06).
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "IBM Spectrum Scale / GPFS — Stage-0 detection-only — proprietary IBM on-disk format; " +
    "magic 0x4347465C at offset 0 of NSD descriptor. Promotion to R/O deferred: full inode/" +
    "directory/allocation layout not publicly specified, file table lives in cluster manager " +
    "across multiple NSDs (no single-image surface), and no fsck-equivalent oracle exists " +
    "off-cluster. See descriptor source comment for the full deferral rationale.";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new GpfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new GpfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new GpfsReader(archive);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"GPFS entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }
}
