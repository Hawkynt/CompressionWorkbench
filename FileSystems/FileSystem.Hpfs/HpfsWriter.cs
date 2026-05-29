#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Hpfs;

/// <summary>
/// Builds a minimal HPFS (OS/2 High Performance File System) image from scratch.
/// Layout:
///   LBA  0:       Boot sector (BPB + OEM ID)
///   LBA 16:       Superblock (8-byte magic + root fnode LBA + total sectors + bitmap start)
///   LBA 17:       Spare block (8-byte magic, minimal)
///   LBA 18:       Root fnode (magic + direct alloc pointing to root dir block)
///   LBA 20..23:   Root directory block (2048 bytes = 4 LBAs, with dir entries)
///   LBA 24:       Bitmap band 0 (allocation bitmap for the whole volume)
///   LBA 32+:      File fnodes (1 LBA each), then file data (contiguous)
///
/// Limitations: root directory only, direct allocation only (no B+ tree),
/// single bitmap band, max ~60 small-named files per dir block.
/// </summary>
internal sealed class HpfsWriter {

  private readonly List<(string Name, byte[] Data)> _files = [];

  internal const int LbaSize = 512;
  internal const int DirBlockLbas = 4; // 2048 bytes per dir block
  internal const int DirBlockSize = LbaSize * DirBlockLbas;

  // Fixed layout LBAs
  private const uint BootLba = 0;
  private const uint SuperblockLba = 16;
  private const uint SpareBlockLba = 17;
  private const uint RootFnodeLba = 18;
  private const uint RootDirLba = 20; // 4 LBAs = 2048 bytes
  private const uint BitmapLba = 24;  // 1 LBA for allocation bitmap
  private const uint FirstFileFnodeLba = 32;

  // Magics
  private static readonly byte[] SuperblockMagic = [0xF9, 0x95, 0xE8, 0xF9, 0xFA, 0x53, 0xE9, 0xF9];
  private static readonly byte[] SpareBlockMagic = [0xF9, 0x11, 0xDC, 0x39, 0xFA, 0x93, 0xB8, 0xF9];
  private static readonly byte[] FnodeMagic = [0xF7, 0xE4, 0x0A, 0xAE];
  private static readonly byte[] DirBlockMagic = [0x77, 0xE4, 0x0A, 0xAE];

  /// <summary>Adds a file to the image. Name is flattened to filename only.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var flat = Path.GetFileName(name);
    if (string.IsNullOrEmpty(flat))
      throw new ArgumentException("File name must not be empty.", nameof(name));
    _files.Add((flat, data));
  }

  /// <summary>Builds the HPFS image and returns the raw bytes.</summary>
  public byte[] Build() {
    // Calculate layout
    var fileCount = _files.Count;
    var fileFnodeLbas = new uint[fileCount];
    var fileDataLbas = new uint[fileCount];
    var fileDataLens = new uint[fileCount]; // in LBAs

    var nextLba = (uint)FirstFileFnodeLba;

    // Assign fnode LBAs (1 LBA each)
    for (var i = 0; i < fileCount; i++) {
      fileFnodeLbas[i] = nextLba;
      nextLba++;
    }

    // Assign data LBAs (contiguous, rounded up to LBA boundaries)
    for (var i = 0; i < fileCount; i++) {
      fileDataLbas[i] = nextLba;
      var dataLbas = (uint)((_files[i].Data.Length + LbaSize - 1) / LbaSize);
      fileDataLens[i] = dataLbas;
      nextLba += dataLbas;
    }

    var totalLbas = Math.Max(nextLba, 128u); // minimum 64 KB image
    var image = new byte[(long)totalLbas * LbaSize];

    // 1. Boot sector at LBA 0
    WriteBootSector(image);

    // 2. Superblock at LBA 16
    WriteSuperblock(image, (uint)totalLbas);

    // 3. Spare block at LBA 17
    WriteSpareBlock(image);

    // 4. Root fnode at LBA 18
    WriteRootFnode(image);

    // 5. Root directory block at LBA 20
    WriteRootDirBlock(image, fileFnodeLbas, fileDataLens);

    // 6. Bitmap at LBA 24
    WriteBitmap(image, nextLba);

    // 7. File fnodes and data
    for (var i = 0; i < fileCount; i++) {
      WriteFileFnode(image, fileFnodeLbas[i], fileDataLbas[i], fileDataLens[i]);
      if (_files[i].Data.Length > 0)
        Buffer.BlockCopy(_files[i].Data, 0, image, (int)(fileDataLbas[i] * LbaSize), _files[i].Data.Length);
    }

    return image;
  }

  /// <summary>Writes the image to a stream.</summary>
  public void WriteTo(Stream output) {
    var data = Build();
    output.Write(data, 0, data.Length);
  }

  private static void WriteBootSector(byte[] image) {
    // OEM ID at offset 3: "IBM 20.0" is a classic HPFS identifier
    Encoding.ASCII.GetBytes("IBM 20.0").CopyTo(image.AsSpan(3, 8));
    // Bytes per sector at offset 11 (u16 LE)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(11, 2), LbaSize);
    // Boot signature at offset 510
    image[510] = 0x55;
    image[511] = 0xAA;
  }

  private void WriteSuperblock(byte[] image, uint totalSectors) {
    var off = (int)(SuperblockLba * LbaSize);

    // 8-byte magic
    SuperblockMagic.CopyTo(image.AsSpan(off, 8));

    // Version at offset 8 (u8): 2 = HPFS
    image[off + 8] = 2;

    // Functional version at offset 9: 2
    image[off + 9] = 2;

    // Root fnode LBA at offset 12
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 12, 4), RootFnodeLba);

    // Total sectors at offset 16
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 16, 4), totalSectors);

    // Number of bad sectors at offset 20: 0
    // Bitmap start LBA at offset 24
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 24, 4), BitmapLba);

    // Spare block LBA at offset 28
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 28, 4), SpareBlockLba);
  }

  private static void WriteSpareBlock(byte[] image) {
    var off = (int)(SpareBlockLba * LbaSize);
    // 8-byte spare block magic
    SpareBlockMagic.CopyTo(image.AsSpan(off, 8));
    // Rest is zeroed (no hot-fix entries, no dirty flags)
  }

  private static void WriteRootFnode(byte[] image) {
    var off = (int)(RootFnodeLba * LbaSize);

    // Fnode magic
    FnodeMagic.CopyTo(image.AsSpan(off, 4));

    // AllocSec header at 0xC0: 8 bytes. height=0 at byte 0xC0+7 (direct list)
    // Already zeroed = height 0 = direct allocation list

    // First direct-allocation entry at 0xC4:
    //   [4: logical sector offset] [4: length in sectors] [4: physical LBA]
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xC4 + 0, 4), 0); // logical offset
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xC4 + 4, 4), DirBlockLbas); // length
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xC4 + 8, 4), RootDirLba); // physical LBA
  }

  private void WriteRootDirBlock(byte[] image, uint[] fileFnodeLbas, uint[] fileDataLens) {
    var off = (int)(RootDirLba * LbaSize);

    // Dir block magic
    DirBlockMagic.CopyTo(image.AsSpan(off, 4));

    // Dirents start at offset 0x14 (20) into the block
    var cursor = off + 0x14;
    var blockEnd = off + DirBlockSize;

    for (var i = 0; i < _files.Count; i++) {
      var (name, data) = _files[i];
      var nameBytes = Encoding.Latin1.GetBytes(name);
      if (nameBytes.Length > 254) nameBytes = nameBytes[..254];

      // Record layout:
      //   0: u16 recLen
      //   2: u16 flags (0 = regular file)
      //   4: u32 fnodeLba
      //   8: u32 mtime (leave 0)
      //  12: u32 fileSize
      //  16..29: reserved/timestamps
      //  30: u8 nameLen
      //  31: name bytes

      var recLen = 32 + nameBytes.Length;
      // Align to 4 bytes
      if ((recLen & 3) != 0) recLen = (recLen + 3) & ~3;

      if (cursor + recLen + 32 > blockEnd)
        break; // No room for this entry + sentinel; stop adding

      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(cursor, 2), (ushort)recLen);
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(cursor + 2, 2), 0); // flags: regular file
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cursor + 4, 4), fileFnodeLbas[i]);
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cursor + 12, 4), (uint)data.Length);
      image[cursor + 30] = (byte)nameBytes.Length;
      nameBytes.CopyTo(image.AsSpan(cursor + 31, nameBytes.Length));

      cursor += recLen;
    }

    // End-of-block sentinel dirent
    if (cursor + 32 <= blockEnd) {
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(cursor, 2), 32); // min record length
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(cursor + 2, 2), 0x0001); // "special" flag (end sentinel)
    }
  }

  private static void WriteFileFnode(byte[] image, uint fnodeLba, uint dataLba, uint dataLenLbas) {
    var off = (int)(fnodeLba * LbaSize);

    // Fnode magic
    FnodeMagic.CopyTo(image.AsSpan(off, 4));

    // AllocSec header at 0xC0: height=0 (direct list, already zeroed)

    // First direct-allocation entry at 0xC4
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xC4 + 0, 4), 0); // logical offset
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xC4 + 4, 4), dataLenLbas); // length
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xC4 + 8, 4), dataLba); // physical LBA
  }

  private static void WriteBitmap(byte[] image, uint usedLbas) {
    var off = (int)(BitmapLba * LbaSize);
    // HPFS bitmap: 1 bit per sector, bit=1 means FREE, bit=0 means USED.
    // Fill the entire LBA with 0xFF (all free) then clear bits for used sectors.
    for (var i = off; i < off + LbaSize; i++)
      image[i] = 0xFF;

    // Mark used sectors (bits 0..usedLbas-1) as allocated (bit=0)
    for (var i = 0u; i < usedLbas && i < LbaSize * 8; i++) {
      var byteIdx = (int)(i / 8);
      var bitIdx = (int)(i % 8);
      image[off + byteIdx] &= (byte)~(1 << bitIdx); // Clear bit = used
    }
  }
}
