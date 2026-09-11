#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Wafl;

/// <summary>
/// Stage-0 descriptor for a flat logical NetApp WAFL volume image. It exposes a
/// synthetic <c>metadata.ini</c> plus the opaque image bytes and validates the
/// documented volinfo superblock copies without pretending to understand the
/// aggregate/FlexVol namespace.
///
/// <para>
/// <b>Stage-0 confirmed.</b> NetApp's current ONTAP EMS documentation identifies
/// volinfo as the WAFL superblock, places its two copies at VBNs 1 and 2 and
/// publishes magic <c>0xdab8fbab</c>. NetApp patents describe the 4 KiB block
/// model and volinfo/fsinfo/inode-file hierarchy. They do not publish enough of
/// modern aggregate/FlexVol and RAID mapping, allocation maps or snapshot
/// reachability to make offline mutation safe. Compact, defrag, wipe, shrink,
/// re-layout and purge therefore remain unavailable.
/// </para>
/// </summary>
public sealed class WaflFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>Gets the id.</summary>
  public string Id => "Wafl";

  /// <summary>Gets the display name.</summary>
  public string DisplayName => "NetApp WAFL";

  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Archive;

  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;

  /// <summary>Gets the default extension.</summary>
  public string DefaultExtension => ".wafl";

  /// <summary>Gets the extensions.</summary>
  public IReadOnlyList<string> Extensions => [".wafl"];

  /// <summary>Gets the compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>
  /// Gets fixed-offset signatures usable by the generic detector. WAFL volinfo
  /// is at fixed VBNs 1 and 2, but the published material does not define one
  /// stable byte offset for the volinfo-magic field inside every ONTAP generation;
  /// content validation is therefore performed by <see cref="WaflReader"/>.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  /// <summary>Gets the methods.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];

  /// <summary>Gets the tar compression format id.</summary>
  public string? TarCompressionFormatId => null;

  /// <summary>Gets the family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <summary>Gets the description.</summary>
  public string Description =>
    "NetApp WAFL — Stage-0 confirmed: documented volinfo detection plus opaque streaming for a flat logical VBN image. " +
    "ONTAP documents volinfo copies at VBNs 1/2 with magic 0xdab8fbab; the previous repository-only ASCII 'wafd' signature was removed. " +
    "Modern FlexVol aggregate/RAID mapping and snapshot/free-space reachability are not public at the byte level needed for safe offline R/W, " +
    "so compact/defrag/wipe/shrink/layout/purge remain disabled.";

  /// <summary>Lists the entries in the supplied container.</summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = new WaflReader(stream);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      index,
      entry.Name,
      entry.Size,
      entry.Size,
      "Stored",
      entry.IsDirectory,
      false,
      null)).ToList();
  }

  /// <summary>Extracts the supplied pseudo-entries.</summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = new WaflReader(stream);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (files is { Length: > 0 } && !MatchesFilter(entry.Name, files)) continue;

      using var source = reader.OpenEntry(entry);
      using var target = CreateEntryFile(outputDir, entry.Name);
      source.CopyTo(target);
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

    using var reader = new WaflReader(archive);
    var entry = reader.Entries.FirstOrDefault(candidate => candidate.Name == entryName)
      ?? throw new FileNotFoundException($"WAFL entry not found: {entryName}", entryName);

    if (!archive.CanSeek)
      return new MemoryStream(reader.Extract(entry), writable: false);

    // WaflReader does not own seekable caller streams, so disposing the reader
    // leaves this bounded view over the original archive alive.
    return reader.OpenEntry(entry);
  }
}
