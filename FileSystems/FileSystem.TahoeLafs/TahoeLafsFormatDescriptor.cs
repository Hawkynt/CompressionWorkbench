#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.TahoeLafs;

/// <summary>
/// Descriptor for Tahoe-LAFS storage-server share containers. Immutable-share
/// containers use a 12-byte versioned header; mutable-share containers use the
/// distinct 32-byte <c>Tahoe mutable container vN</c> magic and fixed lease area.
/// The enclosed share data is surfaced opaquely because reconstructing Tahoe
/// plaintext requires capabilities, encryption keys and enough erasure shares.
///
/// <para>
/// The archive-style namespace intentionally remains read-only: changing an
/// opaque share payload is not equivalent to a valid Tahoe file mutation.
/// Maintenance is limited to the outer storage container. Mutable containers
/// may contain unused growth space between share data and their extra-lease
/// table; that region can be mapped/wiped, the lease table can be packed forward
/// for defrag, and the trailing unused capacity can be removed for shrink.
/// Immutable containers are already packed and therefore pass through unchanged.
/// </para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/tahoe-lafs/tahoe-lafs/blob/master/src/allmydata/storage/immutable.py</c> — immutable storage-container layout</description></item>
///   <item><description><c>https://github.com/tahoe-lafs/tahoe-lafs/blob/master/src/allmydata/storage/mutable.py</c> — mutable storage-container layout</description></item>
///   <item><description><c>https://tahoe-lafs.readthedocs.io/en/latest/specifications/mutable.html</c> — mutable-file format documentation</description></item>
/// </list>
/// </summary>
public sealed class TahoeLafsFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveShrinkable,
  IArchiveDefragmentable,
  IArchiveLayoutMap {

  public string Id => "TahoeLafs";
  public string DisplayName => "Tahoe-LAFS share";
  public FormatCategory Category => FormatCategory.Archive;

  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract;

  public string DefaultExtension => ".tahoe-share";
  public IReadOnlyList<string> Extensions => [".tahoe-share", ".share"];
  public IReadOnlyList<string> CompoundExtensions => [];

  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Immutable storage-container schemas. Version 2 changes lease-secret
    // storage; it is not the mutable-share format.
    new([0x00, 0x00, 0x00, 0x01], Offset: 0, Confidence: 0.55),
    new([0x00, 0x00, 0x00, 0x02], Offset: 0, Confidence: 0.55),
    // Mutable shares have a separate, high-confidence 32-byte storage magic.
    new(TahoeLafsContainer.MutableV1Magic.ToArray(), Offset: 0, Confidence: 0.98),
    new(TahoeLafsContainer.MutableV2Magic.ToArray(), Offset: 0, Confidence: 0.98),
  ];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Tahoe-LAFS storage-server share container — read-only opaque share data with mutable-container pack/wipe/shrink maintenance.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = new TahoeLafsReader(stream);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      index, entry.Name, entry.Size, entry.Size, "Stored", entry.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = new TahoeLafsReader(stream);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (files != null && !MatchesFilter(entry.Name, files)) continue;
      WriteFile(outputDir, entry.Name, reader.Extract(entry));
    }
  }

  /// <summary>
  /// Removes unused mutable-container growth space. Immutable share containers
  /// are already tightly framed and are copied byte-for-byte.
  /// </summary>
  public void Shrink(Stream input, Stream output)
    => TahoeLafsMaintenance.Shrink(input, output);

  /// <summary>
  /// Packs a mutable share's extra-lease table directly behind live share data,
  /// leaving the original physical length as zeroed free tail space.
  /// Immutable share containers need no physical re-layout.
  /// </summary>
  public void Defragment(Stream archive)
    => TahoeLafsMaintenance.Defragment(archive);

  public void Defragment(Stream archive, DefragOptions options)
    => TahoeLafsMaintenance.Defragment(archive, options);

  /// <summary>
  /// Maps required headers, share data and leases plus only those gaps which the
  /// mutable storage-container fields prove to be unused.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive)
    => TahoeLafsMaintenance.EnumerateLayout(archive);
}
