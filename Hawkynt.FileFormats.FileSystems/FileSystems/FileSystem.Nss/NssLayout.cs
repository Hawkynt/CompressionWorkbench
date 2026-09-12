#pragma warning disable CS1591
namespace FileSystem.Nss;

/// <summary>
/// The container this project writes for NSS, and the anchors that make it
/// recognisable as one.
/// </summary>
/// <remarks>
/// <para>NSS's object tree was never documented by Novell, and the only public
/// structural facts about it are the ASCII anchors its pool, volume and
/// superblock descriptors carry. <see cref="NssHeaders" /> finds those, and
/// that is all anyone here can honestly claim to read of a real NSS image.</para>
///
/// <para>So what is written is a container of this project's own shaping, under
/// its own magic and carrying no anchor of a real pool. It carried them once,
/// so that one scan would find both; that had it announce itself as a pool it
/// could not act as, which anything that knows NSS would identify and then fail
/// to read. Saying nothing is better than saying something false. An image from
/// a real NSS pool is still detected exactly as it was — as a pool with anchors
/// and no files this can name.</para>
///
/// <para>The layout is deliberately flat. A file is one run of blocks, and its
/// position is a field in the directory rather than anything implied, which is
/// what lets a layout pass move it by rewriting eight bytes.</para>
/// </remarks>
internal static class NssLayout {

  /// <summary>NSS's own block size, which this keeps.</summary>
  internal const int BlockSize = 4096;

  /// <summary>
  /// Where a real pool carries its anchors, which is where they are looked for
  /// when reading one. Nothing written here puts anything at the first or the
  /// third; the second holds this container's volume name and no anchor.
  /// </summary>
  internal const long PoolAnchor = 0;
  internal const long VolumeAnchor = BlockSize;
  internal const long SuperblockAnchor = 2 * BlockSize;

  /// <summary>The block the directory occupies.</summary>
  internal const long DirectoryBlock = 3;
  internal const long DirectoryOffset = DirectoryBlock * BlockSize;

  /// <summary>The first block a file may occupy.</summary>
  internal const long FirstDataBlock = 4;

  /// <summary>
  /// What marks the container as one of ours, written just past the pool
  /// anchor so a real NSS pool is never mistaken for one.
  /// </summary>
  /// <remarks>The value spells nothing: a marker that reads as words names whoever chose them.</remarks>
  internal static readonly byte[] ContainerMagic =
    [0xD4, 0x0A, 0x17, 0xBE, 0x1F, 0x91, 0x13, 0xCC];
  internal const long ContainerMagicOffset = 16;

  /// <summary>Where the file count sits.</summary>
  internal const long FileCountOffset = ContainerMagicOffset + 8;

  /// <summary>One directory entry: a name, then where its bytes are and how many.</summary>
  internal const int EntryOffsetField = 0;
  internal const int EntrySizeField = 8;
  internal const int EntryHeaderBytes = 16;
}
