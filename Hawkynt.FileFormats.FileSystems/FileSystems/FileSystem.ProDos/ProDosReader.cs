#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ProDos;

/// <summary>
/// Reader for Apple ProDOS <c>.po</c> (block-ordered) and <c>.2mg</c> images, and
/// for 140 KB 5.25" images stored in DOS 3.3 sector order (<c>.dsk</c>, <c>.do</c>) —
/// the usual container for Apple III SOS disks, whose file system is ProDOS's.
/// </summary>
/// <remarks>
/// ProDOS is block-based (512-byte blocks). The volume directory starts at block 2
/// and chains through adjacent blocks via a prev/next pointer pair at the start of
/// each directory block. Each directory block holds thirteen 39-byte entries.
/// File storage tiers: seedling (1 block, up to 512 bytes), sapling (index block of
/// block pointers, up to 128 KB), tree (master index -> index blocks -> data).
/// </remarks>
public sealed class ProDosReader : IDisposable {

  /// <summary>
  /// Defines the block size constant value.
  /// </summary>
  public const int BlockSize = 512;
  /// <summary>
  /// Defines the volume dir start block constant value.
  /// </summary>
  public const int VolumeDirStartBlock = 2;
  /// <summary>
  /// Defines the entries per block constant value.
  /// </summary>
  public const int EntriesPerBlock = 13;
  /// <summary>
  /// Defines the entry size constant value.
  /// </summary>
  public const int EntrySize = 39;

  /// <summary>
  /// Size in bytes of the Volume Directory Header (storage type 0xF) and the
  /// Subdirectory Header (storage type 0xE): one <see cref="EntrySize"/> slot.
  /// The header fills slot 0 of the key block (block bytes $04..$2A), so the
  /// first file entry starts at block byte $2B = 43, as in every ProDOS disk
  /// (ProDOS 8 Technical Reference Manual, B.2.2; checked against CiderPress II).
  /// </summary>
  public const int DirHeaderSize = EntrySize;

  // Directory header fields, as offsets from the header's start (block byte 4).
  public const int HeaderVersion = 0x1C;
  public const int HeaderMinVersion = 0x1D;
  public const int HeaderAccess = 0x1E;
  public const int HeaderEntryLength = 0x1F;
  public const int HeaderEntriesPerBlock = 0x20;
  public const int HeaderFileCount = 0x21;
  /// <summary>Volume header: bit_map_pointer. Subdirectory header: parent_pointer.</summary>
  public const int HeaderBitmapOrParentPointer = 0x23;
  /// <summary>Volume header: total_blocks (2 bytes). Subdirectory header: parent_entry_number (1 byte).</summary>
  public const int HeaderTotalBlocksOrParentEntry = 0x25;
  /// <summary>Subdirectory header: parent_entry_length.</summary>
  public const int HeaderParentEntryLength = 0x26;

  /// <summary>
  /// Returns the byte offset within a directory block at which the entry at
  /// <paramref name="slotIndex"/> begins: entries are <see cref="EntrySize"/>
  /// bytes apart from byte 4, and in a key block slot 0 is the directory header.
  /// </summary>
  public static int EntryOffsetInBlock(bool isFirstBlockOfChain, int slotIndex)
    => 4 + slotIndex * EntrySize;

  /// <summary>
  /// Returns the number of entry slots in a directory block: thirteen, the header
  /// counting as slot 0 of the key block.
  /// </summary>
  public static int SlotsInBlock(bool isFirstBlockOfChain) => EntriesPerBlock;

  /// <summary>.2mg header magic ("2IMG") at offset 0.</summary>
  private static readonly byte[] TwoImgMagic = "2IMG"u8.ToArray();

  private readonly byte[] _image;
  private readonly int _imageStart;  // Offset into _image where block 0 starts (0 for .po, 64 for .2mg).
  private readonly List<ProDosEntry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<ProDosEntry> Entries => _entries;
  /// <summary>
  /// Gets or sets the volume name.
  /// </summary>
  public string VolumeName { get; private set; } = "";

  /// <summary>Whether the image is a 140 KB disk in DOS 3.3 sector order, read through
  /// <see cref="DosOrderToProDosOrder"/>. Editing such an image in place would scramble
  /// it, so the descriptor's editing entry points refuse it.</summary>
  public bool IsDosOrder { get; private set; }

  /// <summary>Bytes of a 35-track, 16-sector 5.25" disk image.</summary>
  public const int FloppyImageLength = 35 * 16 * 256;

  /// <summary>
  /// The two DOS 3.3 logical sectors that hold each ProDOS block of a track, in order:
  /// block <c>k</c> of track <c>t</c> is DOS sector <c>[k].First</c> then <c>[k].Second</c>
  /// of that track. Derived from the two skew tables (ProDOS logical sector 2k and 2k+1
  /// mapped through physical order back to DOS logical order).
  /// </summary>
  private static readonly (int First, int Second)[] DosSectorsOfBlock =
    [(0, 14), (13, 12), (11, 10), (9, 8), (7, 6), (5, 4), (3, 2), (1, 15)];

  /// <summary>Reorders a DOS-order 140 KB image into ProDOS block order.</summary>
  public static byte[] DosOrderToProDosOrder(ReadOnlySpan<byte> dosOrder) {
    if (dosOrder.Length != FloppyImageLength)
      throw new ArgumentException("A DOS-order image is exactly 143 360 bytes.", nameof(dosOrder));
    var result = new byte[FloppyImageLength];
    for (var track = 0; track < 35; ++track)
      for (var k = 0; k < 8; ++k) {
        var (first, second) = DosSectorsOfBlock[k];
        var block = (track * 8 + k) * BlockSize;
        dosOrder.Slice((track * 16 + first) * 256, 256).CopyTo(result.AsSpan(block));
        dosOrder.Slice((track * 16 + second) * 256, 256).CopyTo(result.AsSpan(block + 256));
      }
    return result;
  }

  /// <summary>A plausible volume directory header in block 2: no previous block, storage type 0xF, a name.</summary>
  private static bool HasVolumeHeader(ReadOnlySpan<byte> image, int start) {
    var at = start + VolumeDirStartBlock * BlockSize;
    if (image.Length < at + BlockSize) return false;
    var block = image.Slice(at, BlockSize);
    return BinaryPrimitives.ReadUInt16LittleEndian(block) == 0
      && (block[4] >> 4) == 0x0F && (block[4] & 0x0F) != 0
      && block[4 + HeaderEntryLength] == EntrySize && block[4 + HeaderEntriesPerBlock] is > 0 and <= EntriesPerBlock;
  }

  /// <summary>
  /// Whether <paramref name="image"/> is a 140 KB DOS-order disk: no ProDOS volume header
  /// where block order puts it, but one once the sectors are reordered.
  /// </summary>
  public static bool LooksDosOrdered(ReadOnlySpan<byte> image)
    => image.Length == FloppyImageLength && !HasVolumeHeader(image, 0)
       && HasVolumeHeader(DosOrderToProDosOrder(image), 0);

  /// <summary>
  /// Initializes a new instance of <see cref="ProDosReader"/>.
  /// </summary>
  public ProDosReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _image = Normalise(ms.ToArray());
    _imageStart = DetectImageStart(_image);
    Parse();
  }

  /// <summary>
  /// Initializes a new instance of <see cref="ProDosReader"/>.
  /// </summary>
  public ProDosReader(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    _image = Normalise(data);
    _imageStart = DetectImageStart(_image);
    Parse();
  }

  private byte[] Normalise(byte[] data) {
    if (!LooksDosOrdered(data)) return data;
    IsDosOrder = true;
    return DosOrderToProDosOrder(data);
  }

  private static int DetectImageStart(byte[] data) {
    if (data.Length < 64) return 0;
    if (data.AsSpan(0, 4).SequenceEqual(TwoImgMagic)) {
      // .2mg: 64-byte header, then raw ProDOS-order blocks. Data format field at 0x0C
      // must be 1 (ProDOS order); we just skip the header.
      return 64;
    }
    return 0;
  }

  private int BlockOffset(int block) => _imageStart + block * BlockSize;

  private ReadOnlySpan<byte> Block(int block) {
    var off = BlockOffset(block);
    if (off < 0 || off + BlockSize > _image.Length)
      throw new InvalidDataException($"ProDOS: block {block} out of range.");
    return _image.AsSpan(off, BlockSize);
  }

  private void Parse() {
    if (_image.Length - _imageStart < BlockSize * 3)
      throw new InvalidDataException("ProDOS: image too small.");

    // Volume directory starts at block 2. First entry is the Volume Directory Header.
    // A valid header has storage_type nibble = 0xF.
    var firstBlock = Block(VolumeDirStartBlock);
    var headerStorageNibble = (firstBlock[4] >> 4) & 0x0F;
    if (headerStorageNibble != 0x0F)
      throw new InvalidDataException("ProDOS: volume directory header storage-type nibble is not 0xF.");

    var nameLen = firstBlock[4] & 0x0F;
    if (nameLen is > 0 and <= 15)
      this.VolumeName = Encoding.ASCII.GetString(firstBlock.Slice(5, nameLen));

    ReadDirectory(VolumeDirStartBlock, parentPath: "");
  }

  private void ReadDirectory(int startBlock, string parentPath) {
    var visited = new HashSet<int>();
    var block = startBlock;
    var firstBlock = true;
    // The key block's header says how many entries a block of this directory holds
    // (Apple III SOS writes twelve); anything implausible falls back to thirteen.
    var perBlock = EntriesPerBlock;
    if (BlockOffset(block) + BlockSize <= _image.Length) {
      var key = Block(block);
      if (key[4 + HeaderEntryLength] == EntrySize && key[4 + HeaderEntriesPerBlock] is > 0 and <= EntriesPerBlock)
        perBlock = key[4 + HeaderEntriesPerBlock];
    }

    while (block != 0 && visited.Add(block)) {
      if (BlockOffset(block) + BlockSize > _image.Length) break;
      var data = Block(block);
      var nextBlock = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2, 2));

      var slotsHere = perBlock;
      for (var i = 0; i < slotsHere; i++) {
        // First entry in block 2 is Volume Directory Header (skip). First entry in a
        // subdirectory's first block is the Subdirectory Header (skip).
        if (firstBlock && i == 0) continue;

        var eo = EntryOffsetInBlock(firstBlock, i);

        var storageNibble = (data[eo + 0] >> 4) & 0x0F;
        var nameLen = data[eo + 0] & 0x0F;

        // Storage type 0 = deleted/empty. Skip.
        if (storageNibble == 0 || nameLen == 0) continue;

        var name = Encoding.ASCII.GetString(data.Slice(eo + 1, nameLen));
        var fileType = data[eo + 0x10];
        var keyPointer = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(eo + 0x11, 2));
        var blocksUsed = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(eo + 0x13, 2));
        // EOF is 24-bit LE at offset 0x15-0x17.
        var eof = data[eo + 0x15] | (data[eo + 0x16] << 8) | (data[eo + 0x17] << 16);

        var fullPath = parentPath.Length == 0 ? name : parentPath + "/" + name;
        var isDir = storageNibble == 0x0D;

        _entries.Add(new ProDosEntry {
          Name = name,
          FullPath = fullPath,
          Size = isDir ? 0 : eof,
          IsDirectory = isDir,
          StorageType = (byte)storageNibble,
          FileType = fileType,
          KeyPointer = keyPointer,
          BlocksUsed = blocksUsed,
        });

        // Recurse into subdirectory. Key pointer is the first block of the subdir.
        if (isDir)
          ReadDirectory(keyPointer, fullPath);
      }

      firstBlock = false;
      block = nextBlock;
    }
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(ProDosEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.Size == 0) return [];

    var bytesNeeded = (int)entry.Size;
    var buf = new byte[bytesNeeded];
    var bytesWritten = 0;

    switch (entry.StorageType) {
      case 1: // Seedling — key points at the one data block.
        CopyBlockBytes(entry.KeyPointer, buf, 0, bytesNeeded);
        return buf;

      case 2: // Sapling — key points at an index block of up to 256 pointers.
        CopyFromIndexBlock(entry.KeyPointer, buf, ref bytesWritten, bytesNeeded);
        return buf;

      case 3: { // Tree — key points at master index block. Each pointer points at an index block.
        var master = Block(entry.KeyPointer);
        var lowBytes = master.Slice(0, 256);
        var highBytes = master.Slice(256, 256);
        for (var i = 0; i < 256 && bytesWritten < bytesNeeded; i++) {
          var idx = lowBytes[i] | (highBytes[i] << 8);
          if (idx == 0) {
            // Sparse master entry. Skip past the 128 KB this index block would cover.
            var skip = Math.Min(128 * 1024, bytesNeeded - bytesWritten);
            bytesWritten += skip;
            continue;
          }
          CopyFromIndexBlock(idx, buf, ref bytesWritten, bytesNeeded);
        }
        return buf;
      }

      default:
        throw new InvalidDataException($"ProDOS: unsupported storage type 0x{entry.StorageType:X}.");
    }
  }

  private void CopyFromIndexBlock(int indexBlock, byte[] buf, ref int bytesWritten, int bytesNeeded) {
    if (indexBlock == 0) return;
    var idx = Block(indexBlock);
    var lowBytes = idx.Slice(0, 256);
    var highBytes = idx.Slice(256, 256);
    for (var i = 0; i < 256 && bytesWritten < bytesNeeded; i++) {
      var dataBlock = lowBytes[i] | (highBytes[i] << 8);
      var remaining = bytesNeeded - bytesWritten;
      var take = Math.Min(BlockSize, remaining);
      if (dataBlock == 0) {
        // Sparse block — treat as zero. `buf` is already zero-initialised, so just advance.
        bytesWritten += take;
      } else {
        CopyBlockBytes(dataBlock, buf, bytesWritten, take);
        bytesWritten += take;
      }
    }
  }

  private void CopyBlockBytes(int block, byte[] dst, int dstOffset, int count) {
    var src = Block(block);
    src.Slice(0, count).CopyTo(dst.AsSpan(dstOffset, count));
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}
