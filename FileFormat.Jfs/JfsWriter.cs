#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Jfs;

/// <summary>
/// Writes a minimal IBM JFS filesystem image. Flat file layout in the root
/// directory; up to 10 files via inline dtree. Larger files use single-extent
/// xtree; small files (≤352 bytes) use inode-inline data.
/// Roundtrips through <see cref="JfsReader"/>.
/// </summary>
public sealed class JfsWriter {
  private const int BlockSize = 4096;
  private const int InodeSize = 512;
  private const int SuperblockOff = 32768; // byte offset = block 8
  private const uint JfsMagic = 0x3153464A; // "JFS1"
  private const int MaxInlineFiles = 10;
  private const int InlineDataMax = InodeSize - 160; // 352 bytes
  // Layout constants (block numbers)
  private const int AitBlock = 10;
  private const int FilesetBlock = 14;
  private const int DataStartBlock = 15; // first available block for file data

  private readonly List<(string name, byte[] data)> _files = [];

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (_files.Count >= MaxInlineFiles)
      throw new InvalidOperationException($"JfsWriter supports at most {MaxInlineFiles} files (inline dtree limit).");
    var leaf = Path.GetFileName(name);
    if (leaf.Length > 27) leaf = leaf[..27];
    _files.Add((leaf, data));
  }

  public void WriteTo(Stream output) {
    // Every file gets at least one data block (avoids fragile inline detection
    // in the reader where nextIdx byte overlaps with file data).
    var fileBlocks = new int[_files.Count];
    var fileBlockCounts = new int[_files.Count];
    var nextBlock = DataStartBlock;
    for (var i = 0; i < _files.Count; i++) {
      fileBlocks[i] = nextBlock;
      fileBlockCounts[i] = Math.Max(1, (_files[i].data.Length + BlockSize - 1) / BlockSize);
      nextBlock += fileBlockCounts[i];
    }
    var totalBlocks = nextBlock;
    var image = new byte[totalBlocks * BlockSize];

    // ── Superblock at byte 32768 (block 8) ──
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SuperblockOff), JfsMagic);
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(SuperblockOff + 88), BlockSize);
    // AIT pxd_t at superblock+96: simplified — addr at +100 (block address of AIT)
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(SuperblockOff + 100), AitBlock);

    // ── AIT: inode #16 (FILESYSTEM_I) → xtree pointing to fileset inode table ──
    var fsInodeOff = AitBlock * BlockSize + 16 * InodeSize;
    // xtree root at inode+160: header(24 bytes) + first xad_t(16 bytes)
    var xtOff = fsInodeOff + 160;
    image[xtOff] = 0; // leaf flag
    image[xtOff + 1] = 1; // nextIndex = 1 (one extent)
    // xad_t at xtOff+24: block count at +8 (uint32 LE, low 24 bits) and addr at +12 (uint32 LE)
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(xtOff + 24 + 8), 1); // 1 block
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(xtOff + 24 + 12), FilesetBlock);

    // ── Fileset inode table at block 14 ──
    // Inode #2 = root directory
    var rootInodeOff = FilesetBlock * BlockSize + 2 * InodeSize;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(rootInodeOff), 0x41ED); // mode = directory + 0755
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(rootInodeOff + 48), BlockSize);

    // dtree at rootInode+160
    var dtOff = rootInodeOff + 160;
    image[dtOff] = 0; // flag
    image[dtOff + 1] = (byte)_files.Count; // nextIndex = number of entries
    // stbl at dtOff+8: stbl[i] = i+1 (slots 1..N)
    for (var i = 0; i < _files.Count; i++)
      image[dtOff + 8 + i] = (byte)(i + 1);

    // Slots 1..N at dtOff + slotIdx*32
    for (var i = 0; i < _files.Count; i++) {
      var slotOff = dtOff + (i + 1) * 32;
      var childIno = 3 + i;
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(slotOff), (uint)childIno);
      var nameBytes = Encoding.UTF8.GetBytes(_files[i].name);
      var nameLen = Math.Min(nameBytes.Length, 27);
      image[slotOff + 4] = (byte)nameLen;
      nameBytes.AsSpan(0, nameLen).CopyTo(image.AsSpan(slotOff + 5));
    }

    // File inodes #3..#(2+N)
    for (var i = 0; i < _files.Count; i++) {
      var fileInodeOff = FilesetBlock * BlockSize + (3 + i) * InodeSize;
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(fileInodeOff), 0x81A4); // mode = regular + 0644
      BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(fileInodeOff + 48), _files[i].data.Length);

      // Single extent pointing to data block(s).
      var fxtOff = fileInodeOff + 160;
      image[fxtOff] = 0; // leaf flag
      image[fxtOff + 1] = 1; // nextIndex = 1 extent
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(fxtOff + 24 + 8), (uint)fileBlockCounts[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(fxtOff + 24 + 12), (uint)fileBlocks[i]);
      _files[i].data.CopyTo(image, fileBlocks[i] * BlockSize);
    }

    output.Write(image);
  }
}
