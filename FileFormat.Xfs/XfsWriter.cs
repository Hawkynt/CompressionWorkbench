#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Xfs;

/// <summary>
/// Writes a minimal XFS filesystem image with short-form root directory and
/// extent-based file data. All values are big-endian per XFS spec.
/// Roundtrips through <see cref="XfsReader"/>.
/// </summary>
public sealed class XfsWriter {
  private const int BlockSize = 4096;
  private const int InodeSize = 256;
  private const int InodesPerBlock = BlockSize / InodeSize; // 16
  private const uint XfsMagic = 0x58465342; // "XFSB"
  private const ushort InodeMagic = 0x494E; // "IN"
  private const int InodeBlock = 4; // block containing root dir + file inodes
  private const int DataStartBlock = 5;
  private const ulong RootIno = 64; // inode 64 = block 4, offset 0
  private const int AgBlocks = 4096; // AG size in blocks
  private const int AgBlkLog = 12;   // log2(AgBlocks)

  private readonly List<(string name, byte[] data)> _files = [];

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (_files.Count >= InodesPerBlock - 1)
      throw new InvalidOperationException($"XfsWriter supports at most {InodesPerBlock - 1} files.");
    var leaf = Path.GetFileName(name);
    if (leaf.Length > 250) leaf = leaf[..250];
    _files.Add((leaf, data));
  }

  public void WriteTo(Stream output) {
    var fileDataBlocks = new int[_files.Count];
    var fileBlockCounts = new int[_files.Count];
    var nextBlock = DataStartBlock;
    for (var i = 0; i < _files.Count; i++) {
      fileDataBlocks[i] = nextBlock;
      fileBlockCounts[i] = Math.Max(1, (_files[i].data.Length + BlockSize - 1) / BlockSize);
      nextBlock += fileBlockCounts[i];
    }
    var totalBlocks = nextBlock;
    var image = new byte[totalBlocks * BlockSize];

    // ── Superblock (block 0, big-endian) ──
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0), XfsMagic);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4), BlockSize);
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(8), (ulong)totalBlocks); // dblocks
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(56), RootIno);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(80), 1); // agCount
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(84), InodeSize);
    image[88] = AgBlkLog;
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(102), AgBlocks);

    // ── Root directory inode (inode 64 at block 4, offset 0) ──
    var rootOff = InodeBlock * BlockSize;
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(rootOff), InodeMagic);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(rootOff + 2), 0x41ED); // mode = dir + 0755
    image[rootOff + 5] = 1; // format = short-form (inline dir)
    // size at offset 8 (uint64 BE): compute after building dir data
    // Short-form dir data at inode + 176
    var dirOff = rootOff + 176;
    image[dirOff] = (byte)_files.Count; // count (4-byte inodes)
    image[dirOff + 1] = 0; // i8count = 0
    // parent inode (4 bytes BE) at dirOff+2 — root is its own parent
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(dirOff + 2), (uint)RootIno);
    var entryPos = dirOff + 6;
    for (var i = 0; i < _files.Count; i++) {
      var childIno = (uint)(RootIno + 1 + (uint)i);
      var nameBytes = Encoding.UTF8.GetBytes(_files[i].name);
      var nameLen = Math.Min(nameBytes.Length, 250);
      image[entryPos] = (byte)nameLen;
      BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(entryPos + 1), 0); // offset (unused for sf)
      nameBytes.AsSpan(0, nameLen).CopyTo(image.AsSpan(entryPos + 3));
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(entryPos + 3 + nameLen), childIno);
      entryPos += 3 + nameLen + 4;
    }
    var dirSize = entryPos - dirOff;
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(rootOff + 8), (ulong)dirSize);

    // ── File inodes (inode 65, 66, ... at block 4, offset 256, 512, ...) ──
    for (var i = 0; i < _files.Count; i++) {
      var ioff = InodeBlock * BlockSize + (1 + i) * InodeSize;
      BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(ioff), InodeMagic);
      BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(ioff + 2), 0x81A4); // mode = file + 0644
      image[ioff + 5] = 2; // format = extents
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 8), (ulong)_files[i].data.Length); // size
      BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(ioff + 76), 1); // nextents = 1

      // Encode one BMV extent at ioff+176 (16 bytes): logical=0, startBlock, blockCount
      var startBlock = (ulong)fileDataBlocks[i];
      var blockCount = (ulong)fileBlockCounts[i];
      var hi = (startBlock >> 43) & 0x1FF; // high 9 bits of start block
      var lo = (startBlock << 21) | (blockCount & 0x1FFFFF);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 176), hi);
      BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(ioff + 184), lo);

      // Write file data
      _files[i].data.CopyTo(image, fileDataBlocks[i] * BlockSize);
    }

    output.Write(image);
  }
}
