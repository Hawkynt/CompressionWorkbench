#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.F2fs;

/// <summary>
/// Finds, for every data block of every file, the four bytes that name it.
/// </summary>
/// <remarks>
/// A block's address lives in the inode's own array of them, or in a direct
/// node one, two or three levels below it. The reader walks that to read a
/// file; this walks it to write one down — which is what a move needs and
/// reading never does.
/// </remarks>
internal static class F2fsLayout {

  private const int SuperblockOffset = 1024;
  private const int InodeAddressOffset = 360;
  private const int AddressesPerInode = 923;
  private const int NodeIdOffset = InodeAddressOffset + AddressesPerInode * 4;
  private const int AddressesPerBlock = 1018;
  private const int NodeIdsPerBlock = 1018;

  /// <summary>Every data block and the absolute offset of the address naming it.</summary>
  public static IEnumerable<(long Block, long AddressField)> DataAddresses(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    var superblock = ReadBytes(image, SuperblockOffset, 512);
    if (superblock == null) yield break;
    if (BinaryPrimitives.ReadUInt32LittleEndian(superblock) != 0xF2F52010u) yield break;

    var blockSize = 1 << (int)BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(16));
    if (blockSize < 512) blockSize = 4096;
    var natBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(84));

    image.Position = 0;
    using var reader = new F2fsReader(image, leaveOpen: true);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;

      var inodeBlock = LookupNat(image, natBlock, blockSize, entry.NodeId);
      if (inodeBlock <= 0) continue;

      var inode = ReadBytes(image, (long)inodeBlock * blockSize, blockSize);
      if (inode == null) continue;

      for (var i = 0; i < AddressesPerInode; ++i) {
        var address = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(InodeAddressOffset + i * 4));
        if (address == 0) continue;
        yield return (address, (long)inodeBlock * blockSize + InodeAddressOffset + i * 4);
      }

      for (var slot = 0; slot < 5; ++slot) {
        var nodeId = BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(NodeIdOffset + slot * 4));
        if (nodeId == 0) continue;

        var levels = slot switch { 0 or 1 => 1, 2 or 3 => 2, _ => 3 };
        foreach (var found in WalkNode(image, natBlock, blockSize, nodeId, levels))
          yield return found;
      }
    }
  }

  /// <summary>Walks one node block: addresses at the bottom, node ids above.</summary>
  private static IEnumerable<(long Block, long AddressField)> WalkNode(Stream image, int natBlock,
      int blockSize, uint nodeId, int levels) {
    var nodeBlock = LookupNat(image, natBlock, blockSize, nodeId);
    if (nodeBlock <= 0) yield break;

    var node = ReadBytes(image, (long)nodeBlock * blockSize, blockSize);
    if (node == null) yield break;

    if (levels <= 1) {
      for (var i = 0; i < AddressesPerBlock; ++i) {
        var address = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(i * 4));
        if (address == 0) continue;
        yield return (address, (long)nodeBlock * blockSize + i * 4);
      }

      yield break;
    }

    for (var i = 0; i < NodeIdsPerBlock; ++i) {
      var child = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(i * 4));
      if (child == 0) continue;
      foreach (var found in WalkNode(image, natBlock, blockSize, child, levels - 1))
        yield return found;
    }
  }

  /// <summary>Where the node allocation table says a node id lives.</summary>
  private static int LookupNat(Stream image, int natBlock, int blockSize, uint nodeId) {
    var entriesPerBlock = blockSize / 9;
    if (entriesPerBlock == 0) entriesPerBlock = 455;

    var at = (long)(natBlock + nodeId / entriesPerBlock) * blockSize + nodeId % entriesPerBlock * 9;
    var entry = ReadBytes(image, at, 9);
    return entry == null ? -1 : (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(5));
  }

  private static byte[]? ReadBytes(Stream image, long at, int length) {
    if (at < 0 || length <= 0 || at + length > image.Length) return null;

    var bytes = new byte[length];
    image.Position = at;
    image.ReadExactly(bytes);
    return bytes;
  }
}
