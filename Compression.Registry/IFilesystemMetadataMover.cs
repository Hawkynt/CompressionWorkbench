#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Opt-in capability for filesystems whose own structures — the MFT, an
/// allocation bitmap, an inode table, a root directory — can be relocated
/// rather than only worked around.
/// </summary>
/// <remarks>
/// <para>A defragmenter that treats every metadata extent as immovable leaves
/// the volume's largest structures wherever mkfs happened to put them, which is
/// exactly what a layout request like "metadata at the front" asks it to
/// change. Whether a given structure <em>can</em> move is a property of the
/// format: it depends on there being something to repoint. NTFS records the
/// $MFT's position in its boot sector and every other system file's in that
/// file's own MFT record; ext keeps each group's bitmaps and inode table in the
/// group descriptor; FAT32 keeps the root directory's first cluster in the
/// BPB. A structure that is located by nothing — the boot sector itself, a
/// superblock at a fixed offset — cannot move at all and must not be listed.</para>
///
/// <para>The names are the ones the format's extent map already reports for
/// those regions, so a caller matches what it sees against
/// <see cref="RelocatableMetadata" /> without knowing the format.</para>
/// </remarks>
public interface IFilesystemMetadataMover {

  /// <summary>
  /// The metadata regions this filesystem can relocate, named as its extent map
  /// reports them. Everything not listed stays where it is.
  /// </summary>
  IReadOnlySet<string> RelocatableMetadata { get; }

  /// <summary>
  /// Gives the filesystem a chance to make the destination safe before the raw
  /// bytes are copied there.
  /// </summary>
  /// <remarks>
  /// Most metadata movers need no work here and the default is deliberately a
  /// no-op. Copy-on-write and self-hosting allocators are different: if an
  /// allocation-bitmap page itself moves, the destination bit must be recorded
  /// in the source page <em>before</em> that page is copied. Otherwise the moved
  /// copy can forget that its own new home is allocated. Filesystems with that
  /// invariant override this hook and normally claim the destination here.
  /// </remarks>
  void PrepareMetadataMove(Stream image, string metadataName,
    long oldOffset, long newOffset, long length) { }

  /// <summary>
  /// Repoints whatever locates <paramref name="metadataName" /> after its bytes
  /// have been copied from <paramref name="oldOffset" /> to
  /// <paramref name="newOffset" />, and moves the allocation with it.
  /// </summary>
  /// <param name="image">The filesystem image stream, readable and writable.</param>
  /// <param name="metadataName">One of <see cref="RelocatableMetadata" />.</param>
  /// <param name="oldOffset">Byte offset of the region before the move.</param>
  /// <param name="newOffset">Byte offset of the region after the move.</param>
  /// <param name="length">Length of the moved region in bytes.</param>
  /// <param name="liveRanges">Byte ranges that hold live data once every move
  /// has run. A structure's old home is routinely where a file has just landed,
  /// and releasing it would free space that is in use — so an implementation
  /// must not mark any part of these ranges free.</param>
  /// <exception cref="System.NotSupportedException">This region cannot be
  /// relocated on this volume — the caller keeps it where it is.</exception>
  void UpdateMetadataAfterMove(Stream image, string metadataName,
    long oldOffset, long newOffset, long length,
    IReadOnlyList<(long Offset, long Length)>? liveRanges = null);
}
