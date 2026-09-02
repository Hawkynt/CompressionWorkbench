#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.OneFs;

/// <summary>
/// Stage 0 detection-only descriptor for Dell EMC Isilon OneFS LIN-tree
/// root images. Surfaces only a synthetic <c>metadata.ini</c> and the raw
/// image bytes; no real file-walk is attempted.
///
/// <para>
/// <b>Why R/O promotion is impossible (per CONTRIBUTING.md promotion gates):</b>
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>No single-image content surface.</b> OneFS is a clustered scale-out NAS
/// — every file is split into "protection groups" striped across drives and
/// nodes with FEC (Forward Error Correction, N+M:B layout, e.g. N+2:1). A
/// single drive/node image carries only one stripe; the file data cannot be
/// reconstructed without the peer nodes. A read-only reader from one image
/// can never return correct file bytes.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>LIN tree is cluster-wide.</b> The Logical Inode Number tree (the OneFS
/// metadata index) lives across nodes, not in a single superblock. There is
/// no per-image inode-to-block mapping to walk.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Proprietary on-disk format, no public specification.</b> Dell EMC has
/// never published the OneFS on-disk format. No open-source reverse-engineered
/// reader exists. Without a spec we cannot honour the CONTRIBUTING rule
/// "never advertise capabilities you cannot prove against a real spec".
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>FreeBSD/UFS ancestry does NOT give us a UFS reader fallback.</b> OneFS
/// runs on a FreeBSD-derived kernel, but the filesystem layer is entirely
/// proprietary — it is NOT FFS/UFS at the on-disk level. UFS1 places its
/// superblock magic <c>0x00011954</c> at offset 8192; OneFS images have the
/// ASCII <c>"OneFS"</c> tag at offset 0 and no UFS superblock. Routing OneFS
/// images through <c>UfsReader</c> would fail the magic check and (if forced)
/// return arbitrary bytes — the textbook mutual-compensation trap.
/// </description>
/// </item>
/// </list>
///
/// <para>
/// <b>Conclusion:</b> Stage-0 detection only. Surface the magic, raw bytes,
/// and a <c>metadata.ini</c> documenting the limitation. R/O promotion is
/// blocked on (a) Dell EMC publishing the spec and (b) a multi-node ingest
/// path — neither is in reach.
/// </para>
///
/// References:
/// <list type="bullet">
///   <item><description>Dell EMC "PowerScale OneFS Technical Overview" whitepaper — high-level architecture only; no on-disk spec is published</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/OneFS_distributed_file_system</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class OneFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "OneFs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Dell EMC Isilon OneFS";
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
  public string DefaultExtension => ".onefs";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".onefs"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ASCII "OneFS" (5 bytes) at offset 0 — long tag form.
    new("OneFS"u8.ToArray(), Offset: 0, Confidence: 0.90),
    // ASCII "ONEF" (0x4F4E4546 BE) at offset 0 — short tag form.
    new("ONEF"u8.ToArray(), Offset: 0, Confidence: 0.85),
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
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description =>
    "Dell EMC Isilon OneFS — Stage 0, detection-only — proprietary distributed/clustered FS, " +
    "no single-image content surface (file data is FEC-striped across nodes). " +
    "FreeBSD-derived kernel but filesystem layer is NOT UFS-compatible (no UFS1 superblock at 8192). " +
    "No public on-disk spec; R/O promotion blocked. " +
    "Magic 'OneFS' / 'ONEF' at offset 0 of LIN-tree root.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new OneFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new OneFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new OneFsReader(archive);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"OneFS entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }
}
