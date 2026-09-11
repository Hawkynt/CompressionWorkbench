#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Wafl;

/// <summary>
/// Conservative read-only descriptor for flat logical NetApp WAFL volume images.
/// </summary>
/// <remarks>
/// <para>
/// Stage 0 validates the documented volinfo roots at VBNs 1 and 2 using NetApp's
/// published <c>0xdab8fbab</c> magic. Stage 1 follows the additional public
/// volinfo contract for the classic 32-bit direct-fsinfo profile: volinfo carries
/// a backward-compatible fsinfo magic and a VBN lookup table whose entry zero
/// references the active fsinfo block. Candidate references are accepted only
/// when their target blocks carry that fsinfo magic, and ambiguity fails closed.
/// </para>
/// <para>
/// This does not yet decode the inode file or namespace, and it does not turn a
/// physical ONTAP RAID member into a logical VBN image. Version-specific inode
/// layouts, FlexVol VVBN/PVBN mapping, allocation maps, snapshots and consistency
/// point commit/checksum rules remain prerequisites for safe mutation. Compact,
/// defrag, wipe, shrink, re-layout and purge therefore remain unavailable.
/// </para>
/// </remarks>
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
    "NetApp WAFL — Stage 0 documented volinfo detection plus Stage 1 structural volinfo→fsinfo traversal for the disclosed classic 32-bit direct-fsinfo profile. " +
    "Verified fsinfo blocks are listable/extractable while inode and namespace decoding remain intentionally unavailable. " +
    "Modern FlexVol aggregate/RAID mapping, version-specific inode/directory layouts, allocation maps and snapshot/free-space reachability are still required for safe offline R/W, " +
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
    // leaves a bounded raw-image view alive. Small structural entries are backed
    // by their own 4 KiB copy and remain valid independently as well.
    return reader.OpenEntry(entry);
  }
}
