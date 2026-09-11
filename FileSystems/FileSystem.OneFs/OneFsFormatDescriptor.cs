#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.OneFs;

/// <summary>
/// Conservative inspection descriptor for Dell PowerScale / Isilon OneFS media.
/// </summary>
/// <remarks>
/// <para>
/// OneFS is a distributed filesystem. Dell documents a global namespace, a LIN
/// B+ tree whose values are mirrored inode addresses on node/drive/block tuples,
/// and IFM metatrees that map logical file blocks into protection groups spread
/// through the cluster. A single raw drive therefore does not carry enough state
/// to reconstruct arbitrary files or safely rewrite their allocation metadata.
/// </para>
/// <para>
/// No Dell publication found during the implementation review defines the
/// historical <c>"OneFS"</c> or <c>"ONEF"</c> literals as fixed offset-zero raw
/// media signatures. Advertising those bytes caused unrelated files beginning
/// with either string to be identified as OneFS. Detection is consequently by
/// explicit selection or the unambiguous <c>.onefs</c> extension only.
/// </para>
/// <para>
/// R/W and the destructive maintenance verbs remain deliberately unavailable.
/// Implementing add/remove, purge, wipe, relocation/defrag, shrink or relayout
/// without the raw allocation, protection and transaction formats would corrupt
/// media while satisfying only this library's own synthetic tests. The reader
/// instead exposes metadata describing the known architecture plus the original
/// image as a byte-exact bounded stream.
/// </para>
/// <para>
/// Clean-room references:
/// <list type="bullet">
///   <item><description><c>https://infohub.delltechnologies.com/en-us/p/onefs-metadata/</c>
///     — LIN tree, inode mirrors, IFM/DFM B+ trees and protection metadata.</description></item>
///   <item><description><c>https://infohub.delltechnologies.com/en-nz/l/dell-powerscale-onefs-technical-overview/file-system-structure/1/</c>
///     — distributed UFS-based filesystem and global namespace.</description></item>
///   <item><description>Dell PowerScale OneFS Technical Specifications Guide —
///     fixed 8 KiB filesystem block size.</description></item>
/// </list>
/// No external implementation code is copied or translated.
/// </para>
/// </remarks>
public sealed class OneFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>Gets the registry id.</summary>
  public string Id => "OneFs";

  /// <summary>Gets the display name.</summary>
  public string DisplayName => "Dell EMC Isilon OneFS";

  /// <summary>Gets the format category.</summary>
  public FormatCategory Category => FormatCategory.Archive;

  /// <summary>Gets the conservative read-only capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;

  /// <summary>Gets the conventional extension for explicitly supplied raw media.</summary>
  public string DefaultExtension => ".onefs";

  /// <summary>Gets the recognized extensions.</summary>
  public IReadOnlyList<string> Extensions => [".onefs"];

  /// <summary>Gets compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>
  /// Gets fixed magic signatures. Dell does not publish an authoritative
  /// offset-zero raw-media signature, so OneFS intentionally has none.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  /// <summary>Gets the pseudo-archive storage method.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];

  /// <summary>Gets the tar compression format id.</summary>
  public string? TarCompressionFormatId => null;

  /// <summary>Gets the algorithm family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <summary>Gets a description of the intentionally limited inspection surface.</summary>
  public string Description =>
    "Dell PowerScale / Isilon OneFS — opaque single-image inspection only. " +
    "OneFS presents a cluster-wide namespace: LIN metadata resolves to mirrored node/drive/block addresses " +
    "and file metatrees resolve data through distributed protection groups. Dell documents OneFS as UFS-based, " +
    "but does not publish a standalone raw-drive layout or fixed offset-zero media magic sufficient for offline parsing. " +
    "The historic 'OneFS'/'ONEF' signature claim was therefore removed. R/W, purge, wipe, defrag, shrink and relayout " +
    "remain blocked until the raw allocation/protection/transaction format can be proven against a real cluster or checker.";

  /// <summary>Lists the two conservative inspection entries without reading the image payload.</summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = new OneFsReader(stream, leaveOpen: true);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      index, entry.Name, entry.Size, entry.Size, "Stored", entry.IsDirectory, false, null)).ToList();
  }

  /// <summary>Extracts selected inspection entries through bounded streams.</summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = new OneFsReader(stream, leaveOpen: true);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory)
        continue;
      if (files is { Length: > 0 } && !MatchesFilter(entry.Name, files))
        continue;

      using var source = reader.OpenEntry(entry);
      using var target = CreateEntryFile(outputDir, entry.Name);
      source.CopyTo(target);
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);

    using var reader = new OneFsReader(archive, leaveOpen: true);
    var entry = reader.Entries.FirstOrDefault(candidate =>
      string.Equals(candidate.Name, entryName, StringComparison.Ordinal))
      ?? throw new FileNotFoundException($"OneFS entry not found: {entryName}", entryName);
    return reader.OpenEntry(entry);
  }
}
