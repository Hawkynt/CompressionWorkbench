#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Sfs;

/// <summary>
/// Builds an Amiga Smart File System volume: root block, bitmap, admin space,
/// object node table, extent tree, root directory and the files themselves.
/// </summary>
/// <remarks>
/// <para>SFS keeps a file's blocks out of the file's own entry. The directory
/// entry names one key; the key indexes a tree of extents, each of which says
/// how many blocks it covers and which key comes next. So a file is a chain
/// through that tree, and where the chain's links point is the only record of
/// where its bytes are — which is exactly what a layout pass rewrites.</para>
///
/// <para>Every block carrying a header is checksummed by the whole block's
/// longwords summing to zero, and every one of them also records its own block
/// number, so a block that moved without being rewritten fails both checks at
/// once.</para>
///
/// <para>What this writes is the simplest volume the structures allow: one
/// object container for a flat root directory, one leaf of extents, one node
/// container. Hash tables, soft links, sub-directories and multi-level trees
/// are shapes the format has and this does not produce.</para>
/// </remarks>
public sealed class SfsWriter {

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Bytes per block. SFS allows 512 up to 32768; 512 is its own default.</summary>
  public int BlockSize { get; init; } = 512;

  /// <summary>Seconds since 1978-01-01, which is how Amiga volumes date themselves.</summary>
  public uint DateCreated { get; init; } = 0x2E000000;

  /// <summary>Adds a file to the root directory.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var clean = Path.GetFileName(name);
    if (clean.Length is 0 or > 105)
      throw new ArgumentException($"SFS: '{name}' is not a name this can write.", nameof(name));
    this._files.Add((clean, data));
  }

  // Fixed blocks. The root block is block 0 by definition; the rest are placed
  // here because nothing else needs them anywhere in particular.
  private const int RootBlock = 0;
  private const int BitmapBlock = 1;

  /// <summary>Lays the volume out and returns its bytes.</summary>
  public byte[] Build() {
    var bs = this.BlockSize;
    if (bs is < 512 or > 32768 || (bs & (bs - 1)) != 0)
      throw new InvalidOperationException($"SFS: {bs} is not a block size this format has.");

    // One bit per block, and the bitmap has to cover the volume it is sized for
    // — so its own size feeds back into the count. Two passes settle it.
    var directory = this.BuildRootDirectory(out var objectNodes);
    var payloadBlocks = this._files.Sum(f => Math.Max(1, Blocks(f.Data.LongLength, bs)));

    var bitmapBlocks = 1;
    int totalBlocks;
    while (true) {
      totalBlocks = FixedBlocks(bitmapBlocks) + payloadBlocks + 1;   // +1: the root block's copy
      var wanted = Blocks((totalBlocks + 7) / 8L, bs - SfsLayout.BlockHeaderBytes);
      if (wanted <= bitmapBlocks) break;
      bitmapBlocks = wanted;
    }

    var adminBlock = BitmapBlock + bitmapBlocks;
    var nodeBlock = adminBlock + 1;
    var extentBlock = nodeBlock + 1;
    var objectBlock = extentBlock + 1;
    var firstDataBlock = objectBlock + 1;

    var image = new byte[(long)totalBlocks * bs];

    // Where each file's bytes go: one extent apiece, laid down in order.
    var starts = new int[this._files.Count];
    var counts = new int[this._files.Count];
    var cursor = firstDataBlock;
    for (var i = 0; i < this._files.Count; ++i) {
      counts[i] = Math.Max(1, Blocks(this._files[i].Data.LongLength, bs));
      starts[i] = cursor;
      this._files[i].Data.CopyTo(image, (long)cursor * bs);
      cursor += counts[i];
    }

    this.WriteRootBlock(image, RootBlock, totalBlocks, bitmapBlocks,
      adminBlock, objectBlock, extentBlock, nodeBlock, sequence: 1);
    this.WriteRootBlock(image, totalBlocks - 1, totalBlocks, bitmapBlocks,
      adminBlock, objectBlock, extentBlock, nodeBlock, sequence: 2);

    this.WriteBitmap(image, bitmapBlocks, totalBlocks, firstDataBlock + payloadBlocks);
    this.WriteAdminSpace(image, adminBlock, firstDataBlock);
    this.WriteNodeContainer(image, nodeBlock, objectBlock, objectNodes.Length);
    this.WriteExtentTree(image, extentBlock, starts, counts);
    this.WriteObjectContainer(image, objectBlock, directory, starts);

    return image;

    int FixedBlocks(int bitmap) => 1 + bitmap + 4;   // root, bitmap, admin, nodes, extents, objects
  }

  private static int Blocks(long length, int per) => (int)((length + per - 1) / per);

  private void WriteRootBlock(
      byte[] image, int block, int totalBlocks, int bitmapBlocks,
      int adminBlock, int objectBlock, int extentBlock, int nodeBlock, ushort sequence) {
    var bs = this.BlockSize;
    var root = image.AsSpan(block * bs, bs);

    SfsLayout.RootId.CopyTo(root);
    BinaryPrimitives.WriteUInt32BigEndian(root[8..], (uint)block);
    BinaryPrimitives.WriteUInt16BigEndian(root[SfsLayout.RbVersion..], SfsLayout.StructureVersion);
    BinaryPrimitives.WriteUInt16BigEndian(root[SfsLayout.RbSequenceNumber..], sequence);
    BinaryPrimitives.WriteUInt32BigEndian(root[SfsLayout.RbDateCreated..], this.DateCreated);
    root[SfsLayout.RbBits] = SfsLayout.RootBitsCaseSensitive;

    BinaryPrimitives.WriteUInt32BigEndian(root[SfsLayout.RbFirstByteHigh..], 0);
    BinaryPrimitives.WriteUInt32BigEndian(root[SfsLayout.RbFirstByte..], 0);
    var lastByte = (long)totalBlocks * bs - 1;
    BinaryPrimitives.WriteUInt32BigEndian(root[SfsLayout.RbLastByteHigh..], (uint)(lastByte >> 32));
    BinaryPrimitives.WriteUInt32BigEndian(root[SfsLayout.RbLastByte..], (uint)lastByte);

    BinaryPrimitives.WriteUInt32BigEndian(root[SfsLayout.RbTotalBlocks..], (uint)totalBlocks);
    BinaryPrimitives.WriteUInt32BigEndian(root[SfsLayout.RbBlockSize..], (uint)bs);
    BinaryPrimitives.WriteUInt32BigEndian(root[SfsLayout.RbBitmapBase..], BitmapBlock);
    BinaryPrimitives.WriteUInt32BigEndian(root[SfsLayout.RbAdminSpaceContainer..], (uint)adminBlock);
    BinaryPrimitives.WriteUInt32BigEndian(root[SfsLayout.RbRootObjectContainer..], (uint)objectBlock);
    BinaryPrimitives.WriteUInt32BigEndian(root[SfsLayout.RbExtentBNodeRoot..], (uint)extentBlock);
    BinaryPrimitives.WriteUInt32BigEndian(root[SfsLayout.RbObjectNodeRoot..], (uint)nodeBlock);

    _ = bitmapBlocks;
    SfsLayout.SetChecksum(root);
  }

  /// <summary>
  /// Marks every block the volume has handed out. A set bit is a used block,
  /// counted from the top of each longword.
  /// </summary>
  private void WriteBitmap(byte[] image, int bitmapBlocks, int totalBlocks, int usedThrough) {
    var bs = this.BlockSize;
    var perBlock = bs - SfsLayout.BlockHeaderBytes;

    for (var i = 0; i < bitmapBlocks; ++i) {
      var at = (BitmapBlock + i) * bs;
      var block = image.AsSpan(at, bs);
      BinaryPrimitives.WriteUInt32BigEndian(block, SfsLayout.BitmapId);
      BinaryPrimitives.WriteUInt32BigEndian(block[8..], (uint)(BitmapBlock + i));

      for (var b = 0; b < perBlock * 8; ++b) {
        var blockNumber = i * perBlock * 8 + b;
        if (blockNumber >= totalBlocks) break;

        var used = blockNumber < usedThrough || blockNumber == totalBlocks - 1;
        if (!used) continue;

        var byteAt = SfsLayout.BlockHeaderBytes + b / 8;
        block[byteAt] |= (byte)(0x80 >> (b % 8));
      }

      SfsLayout.SetChecksum(block);
    }
  }

  /// <summary>
  /// Records which blocks are given over to the volume's own structures. One
  /// space, one longword of bits, the top bit being its first block.
  /// </summary>
  private void WriteAdminSpace(byte[] image, int block, int firstDataBlock) {
    var bs = this.BlockSize;
    var admin = image.AsSpan(block * bs, bs);

    BinaryPrimitives.WriteUInt32BigEndian(admin, SfsLayout.AdminSpaceContainerId);
    BinaryPrimitives.WriteUInt32BigEndian(admin[8..], (uint)block);
    BinaryPrimitives.WriteUInt32BigEndian(admin[SfsLayout.AscNext..], 0);
    BinaryPrimitives.WriteUInt32BigEndian(admin[SfsLayout.AscPrevious..], 0);
    admin[SfsLayout.AscBits] = 2;    // four bytes of bits per space

    BinaryPrimitives.WriteUInt32BigEndian(admin[SfsLayout.AscSpaces..], 0);
    var bits = firstDataBlock >= 32 ? uint.MaxValue : ~(uint.MaxValue >> firstDataBlock);
    BinaryPrimitives.WriteUInt32BigEndian(admin[(SfsLayout.AscSpaces + 4)..], bits);

    SfsLayout.SetChecksum(admin);
  }

  /// <summary>
  /// Maps object node numbers to the block of the container holding them. The
  /// root directory is node one, which is the number the filesystem expects.
  /// </summary>
  private void WriteNodeContainer(byte[] image, int block, int objectBlock, int nodeCount) {
    var bs = this.BlockSize;
    var nodes = image.AsSpan(block * bs, bs);

    BinaryPrimitives.WriteUInt32BigEndian(nodes, SfsLayout.NodeContainerId);
    BinaryPrimitives.WriteUInt32BigEndian(nodes[8..], (uint)block);
    BinaryPrimitives.WriteUInt32BigEndian(nodes[SfsLayout.NcNodeNumber..], SfsLayout.RootNode);
    BinaryPrimitives.WriteUInt32BigEndian(nodes[SfsLayout.NcNodes..], (uint)(nodeCount + 1));

    for (var i = 0; i <= nodeCount; ++i) {
      var slot = SfsLayout.NcFirstNode + i * 4;
      if (slot + 4 > bs)
        throw new InvalidOperationException(
          "SFS: more object nodes than one node container holds; this writes only one.");
      BinaryPrimitives.WriteUInt32BigEndian(nodes[slot..], (uint)objectBlock);
    }

    SfsLayout.SetChecksum(nodes);
  }

  /// <summary>
  /// Writes the tree of extents, one leaf, sorted by the block each starts at.
  /// </summary>
  /// <remarks>
  /// A file's entry names only its first extent's key. Every extent after that
  /// is reached by the key in the one before it, so the chain — not the order
  /// in the leaf — is what says which blocks a file owns and in what order.
  /// </remarks>
  private void WriteExtentTree(byte[] image, int block, int[] starts, int[] counts) {
    var bs = this.BlockSize;
    var tree = image.AsSpan(block * bs, bs);

    BinaryPrimitives.WriteUInt32BigEndian(tree, SfsLayout.BNodeContainerId);
    BinaryPrimitives.WriteUInt32BigEndian(tree[8..], (uint)block);
    BinaryPrimitives.WriteUInt16BigEndian(tree[SfsLayout.BtcNodeCount..], (ushort)starts.Length);
    tree[SfsLayout.BtcIsLeaf] = 1;
    tree[SfsLayout.BtcNodeSize] = SfsLayout.ExtentNodeBytes;

    var order = Enumerable.Range(0, starts.Length).OrderBy(i => starts[i]).ToArray();
    if (SfsLayout.BtcNodes + order.Length * SfsLayout.ExtentNodeBytes > bs)
      throw new InvalidOperationException(
        "SFS: more extents than one tree leaf holds; this writes only one.");

    for (var slot = 0; slot < order.Length; ++slot) {
      var i = order[slot];
      var node = tree[(SfsLayout.BtcNodes + slot * SfsLayout.ExtentNodeBytes)..];
      BinaryPrimitives.WriteUInt32BigEndian(node[SfsLayout.ExKey..], (uint)starts[i]);
      BinaryPrimitives.WriteUInt32BigEndian(node[SfsLayout.ExNext..], 0);
      BinaryPrimitives.WriteUInt32BigEndian(node[SfsLayout.ExPrev..], 0);
      BinaryPrimitives.WriteUInt16BigEndian(node[SfsLayout.ExBlocks..], (ushort)counts[i]);
    }

    SfsLayout.SetChecksum(tree);
  }

  /// <summary>Lays out the root directory's entries and says which node each got.</summary>
  private byte[] BuildRootDirectory(out uint[] objectNodes) {
    objectNodes = new uint[this._files.Count];
    var entries = new List<byte>();

    for (var i = 0; i < this._files.Count; ++i) {
      objectNodes[i] = SfsLayout.RootNode + 1 + (uint)i;
      var name = Encoding.ASCII.GetBytes(this._files[i].Name);
      var entry = new byte[SfsLayout.ObjectBytes(name.Length, 0)];

      BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(SfsLayout.ObObjectNode), objectNodes[i]);
      BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(SfsLayout.ObProtection), 0);
      BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(SfsLayout.ObSize), (uint)this._files[i].Data.Length);
      BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(SfsLayout.ObDateModified), this.DateCreated);
      entry[SfsLayout.ObBits] = 0;
      name.CopyTo(entry, SfsLayout.ObName);
      entries.AddRange(entry);
    }

    return entries.ToArray();
  }

  /// <summary>
  /// Writes the root directory's container, filling each entry's data field
  /// with the key of the extent holding that file's first block.
  /// </summary>
  private void WriteObjectContainer(byte[] image, int block, byte[] directory, int[] starts) {
    var bs = this.BlockSize;
    var container = image.AsSpan(block * bs, bs);

    BinaryPrimitives.WriteUInt32BigEndian(container, SfsLayout.ObjectContainerId);
    BinaryPrimitives.WriteUInt32BigEndian(container[8..], (uint)block);
    BinaryPrimitives.WriteUInt32BigEndian(container[SfsLayout.OcParent..], 0);
    BinaryPrimitives.WriteUInt32BigEndian(container[SfsLayout.OcNext..], 0);
    BinaryPrimitives.WriteUInt32BigEndian(container[SfsLayout.OcPrevious..], 0);

    if (SfsLayout.OcObjects + directory.Length + 2 > bs)
      throw new InvalidOperationException(
        "SFS: more directory entries than one object container holds; this writes only one.");

    directory.CopyTo(container[SfsLayout.OcObjects..]);

    // Fill in where each file's blocks start, now that they are placed.
    var cursor = SfsLayout.OcObjects;
    for (var i = 0; i < starts.Length; ++i) {
      BinaryPrimitives.WriteUInt32BigEndian(container[(cursor + SfsLayout.ObData)..], (uint)starts[i]);
      cursor += EntryBytes(container, cursor);
    }

    SfsLayout.SetChecksum(container);
  }

  /// <summary>How long the entry at <paramref name="at" /> is, strings and pad included.</summary>
  internal static int EntryBytes(ReadOnlySpan<byte> container, int at) {
    var cursor = at + SfsLayout.ObName;
    while (cursor < container.Length && container[cursor] != 0) ++cursor;
    ++cursor;                                              // the name's terminator
    while (cursor < container.Length && container[cursor] != 0) ++cursor;
    ++cursor;                                              // the comment's
    return ((cursor - at) + 1) & ~1;
  }
}
