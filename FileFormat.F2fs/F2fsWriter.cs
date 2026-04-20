#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.F2fs;

/// <summary>
/// Writes a minimal F2FS filesystem image. Root directory with flat file list.
/// Names are limited to 8 bytes (single filename slot) to keep dentry parsing
/// simple. Roundtrips through <see cref="F2fsReader"/>.
/// </summary>
public sealed class F2fsWriter {
  private const int BlockSize = 4096;
  private const int SbOffset = 1024;
  private const uint F2fsMagic = 0xF2F52010;
  private const uint RootNodeId = 3;
  private const int NatBlock = 1;
  private const int RootInodeBlock = 2;
  private const int RootDentryBlock = 3;
  private const int FirstFileBlock = 4;
  private const int NameSlotLen = 8;

  private readonly List<(string name, byte[] data)> _files = [];

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (_files.Count >= 212)
      throw new InvalidOperationException("F2fsWriter supports at most 212 files (one dentry block).");
    var leaf = Path.GetFileName(name);
    if (leaf.Length > NameSlotLen) leaf = leaf[..NameSlotLen];
    _files.Add((leaf, data));
  }

  public void WriteTo(Stream output) {
    var n = _files.Count;
    // Each file gets inode block + at least one data block.
    var fileInodeBlocks = new int[n];
    var fileDataBlocks = new int[n];
    var nextBlock = FirstFileBlock;
    for (var i = 0; i < n; i++) {
      fileInodeBlocks[i] = nextBlock++;
      fileDataBlocks[i] = nextBlock++;
    }
    var totalBlocks = nextBlock;
    var image = new byte[totalBlocks * BlockSize];

    // ── Superblock (at byte 1024) ──
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SbOffset), F2fsMagic);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SbOffset + 12), 12); // log block size (2^12 = 4096)
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SbOffset + 72), NatBlock);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SbOffset + 80), FirstFileBlock);

    // ── NAT block (9 bytes per entry, indexed by nodeId) ──
    // Entry 3 = root inode → RootInodeBlock
    // Entry 4+i*2 = file inode i → fileInodeBlocks[i]
    WriteNatEntry(image, 3, RootNodeId, RootInodeBlock);
    for (var i = 0; i < n; i++) {
      var nodeId = (uint)(4 + i * 2);
      WriteNatEntry(image, (int)nodeId, nodeId, fileInodeBlocks[i]);
    }

    // ── Root directory inode (block 2) ──
    var rootNodeOff = RootInodeBlock * BlockSize;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootNodeOff), 0x41ED); // mode: dir + 0755
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(rootNodeOff + 40), BlockSize); // size
    // i_addr[0] at offset 128 points to dentry block
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(rootNodeOff + 128), RootDentryBlock);

    // ── Root dentry block (block 3) ──
    var dentryBlockOff = RootDentryBlock * BlockSize;
    var dentryArrayOff = dentryBlockOff + 64; // after bitmap + padding
    // Reader computes nrDentry = (blockSize - 64) / 19 = 212 for 4096 blocks,
    // and nameArrayOff = dentryArrayOff + nrDentry * 11.
    const int nrDentry = 212;
    var nameArrayOff = dentryArrayOff + nrDentry * 11;
    for (var i = 0; i < n; i++) {
      // Set bitmap bit i
      image[dentryBlockOff + i / 8] |= (byte)(1 << (i % 8));

      // Dentry entry: hash(4) + ino(4) + name_len(2) + file_type(1)
      var entryOff = dentryArrayOff + i * 11;
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entryOff), 0); // hash (unused)
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entryOff + 4), (uint)(4 + i * 2)); // ino
      var nameBytes = Encoding.UTF8.GetBytes(_files[i].name);
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 8), (ushort)nameBytes.Length);
      image[entryOff + 10] = 1; // file_type = regular file

      // Filename (up to 8 bytes per slot)
      var nameOff = nameArrayOff + i * NameSlotLen;
      var copyLen = Math.Min(nameBytes.Length, NameSlotLen);
      nameBytes.AsSpan(0, copyLen).CopyTo(image.AsSpan(nameOff));
    }

    // ── File inodes + data blocks ──
    for (var i = 0; i < n; i++) {
      var inoOff = fileInodeBlocks[i] * BlockSize;
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(inoOff), 0x81A4); // mode: file + 0644
      BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(inoOff + 40), (ulong)_files[i].data.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(inoOff + 128), (uint)fileDataBlocks[i]);

      // File data
      var dataOff = fileDataBlocks[i] * BlockSize;
      var copyLen = Math.Min(_files[i].data.Length, BlockSize);
      _files[i].data.AsSpan(0, copyLen).CopyTo(image.AsSpan(dataOff));
    }

    output.Write(image);
  }

  private static void WriteNatEntry(byte[] image, int nodeId, uint ino, int blockAddr) {
    const int entriesPerBlock = BlockSize / 9;
    var natBlock = nodeId / entriesPerBlock;
    var natIdx = nodeId % entriesPerBlock;
    var natOff = (NatBlock + natBlock) * BlockSize + natIdx * 9;
    image[natOff] = 0; // version
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(natOff + 1), ino);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(natOff + 5), (uint)blockAddr);
  }
}
