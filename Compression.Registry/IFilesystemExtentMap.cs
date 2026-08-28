#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Opt-in capability: the descriptor (or a partner type) can enumerate the
/// <em>actual</em> on-disk byte layout of a filesystem image — every used
/// cluster chain per file (one <see cref="DefragBlockInfo"/> per contiguous
/// run), every metadata-reserved region (boot sector, FAT, bitmap,
/// superblock, MFT, root directory, inode table, BAM, group descriptor table,
/// etc.), and optionally every free region.
///
/// <para><b>Fail-closed contract:</b> gaps in the returned set are interpreted
/// as free space by maintenance consumers. Therefore an implementation that
/// encounters an allocated-but-undecoded, damaged, ambiguous, or otherwise
/// unproven region MUST emit that region as
/// <see cref="DefragBlockKind.MetadataReserved"/> rather than silently omit it.
/// If the image cannot be walked safely at all, yield no extents; the inherited
/// generic <see cref="IWipeEmpty"/> implementation then wipes nothing.</para>
///
/// <para>Because this contract identifies all bytes that must be preserved,
/// every extent map is also an <see cref="IWipeEmpty"/> implementation: the
/// default wiper zeros only proven gaps (and cluster tips when a trustworthy
/// logical-size lookup exists). Formats that know about deleted directory
/// records or other hidden remnants may override the wipe for deeper cleaning.</para>
///
/// <para>Drives the Defragment-window block-map preview so the user sees the
/// real fragmented layout before pressing "Defragment" rather than the
/// post-defrag approximation.</para>
/// </summary>
public interface IFilesystemExtentMap : IWipeEmpty {
  /// <summary>
  /// Enumerates the actual on-disk layout of <paramref name="image"/>.
  /// Coverage may be sparse only where the omitted bytes are proven free;
  /// callers fill those gaps with <see cref="DefragBlockKind.Free"/>. Unknown
  /// allocated bytes must be returned as <see cref="DefragBlockKind.MetadataReserved"/>.
  /// The stream's position may be modified during enumeration but the caller
  /// owns its lifetime — implementations must not dispose <paramref name="image"/>.
  /// </summary>
  /// <param name="image">The filesystem image to walk. Must be readable and
  /// seekable.</param>
  /// <returns>Zero or more contiguous regions describing the on-disk
  /// layout. Order is unspecified; lengths are in bytes; offsets are
  /// relative to the start of <paramref name="image"/>.</returns>
  IEnumerable<DefragBlockInfo> EnumerateExtents(System.IO.Stream image);
}
