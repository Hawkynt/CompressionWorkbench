#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Compression.Tests.Apfs;

/// <summary>
/// Finds a container's structures the way a reader finds them, rather than by
/// block numbers written down in a test.
/// </summary>
/// <remarks>
/// Those numbers moved when the container gained a real checkpoint data area and
/// stopped overlapping its own descriptor area. Tests that knew the old layout
/// by heart then failed for having been right about the wrong thing — which is
/// noise standing exactly where a real regression would appear.
/// </remarks>
internal static class ApfsTestLayout {

  private const int BlockSize = 4096;

  /// <summary>The container's object map block, from <c>nx_omap_oid</c>.</summary>
  internal static ulong ContainerOmapBlock(ReadOnlySpan<byte> image)
    => BinaryPrimitives.ReadUInt64LittleEndian(image[160..]);

  /// <summary>The container object map's B-tree root, from <c>om_tree_oid</c>.</summary>
  internal static ulong ContainerOmapTreeBlock(ReadOnlySpan<byte> image) {
    var omap = (int)ContainerOmapBlock(image) * BlockSize;
    return BinaryPrimitives.ReadUInt64LittleEndian(image[(omap + 48)..]);
  }

  /// <summary>
  /// The volume superblock, resolved through the container's object map exactly
  /// as the reader resolves it: the map's single record names the block.
  /// </summary>
  internal static ulong ApsbBlock(ReadOnlySpan<byte> image)
    => FirstOmapTarget(image, (int)ContainerOmapTreeBlock(image) * BlockSize);

  /// <summary>The volume's filesystem-tree root, from the APSB's own object map.</summary>
  internal static ulong FsTreeBlock(ReadOnlySpan<byte> image) {
    var apsb = (int)ApsbBlock(image) * BlockSize;
    var volOmap = (int)BinaryPrimitives.ReadUInt64LittleEndian(image[(apsb + 40)..]) * BlockSize;
    var volTree = (int)BinaryPrimitives.ReadUInt64LittleEndian(image[(volOmap + 48)..]) * BlockSize;
    return FirstOmapTarget(image, volTree);
  }

  /// <summary>
  /// The block the first record of an object map's root node points at.
  /// </summary>
  /// <remarks>
  /// An object map's records are all one size, so its slots hold two offsets and
  /// no lengths — the sizes are in the root's footer. The variable-length form is
  /// still read here as well, so this keeps working whichever way the node was
  /// written.
  /// </remarks>
  private static ulong FirstOmapTarget(ReadOnlySpan<byte> image, int treeAt) {
    const int btnHeaderEnd = 56;
    const int btreeInfoSize = 40;
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(image[(treeAt + 32)..]);
    var tocAt = treeAt + btnHeaderEnd + BinaryPrimitives.ReadUInt16LittleEndian(image[(treeAt + 40)..]);
    var valueEnd = treeAt + BlockSize - btreeInfoSize;

    // Fixed: (keyOff, valOff). Variable: (keyOff, keyLen, valOff, valLen).
    var fixedKv = (flags & 0x0004) != 0;
    var valOff = BinaryPrimitives.ReadUInt16LittleEndian(image[(tocAt + (fixedKv ? 2 : 4))..]);
    // An omap value is (flags, size, paddr); the block number is its last word.
    return BinaryPrimitives.ReadUInt64LittleEndian(image[(valueEnd - valOff + 8)..]);
  }
}
