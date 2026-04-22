#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ProDos;

/// <summary>
/// Reader for Apple ProDOS <c>.po</c> (block-ordered) and <c>.2mg</c> images.
/// </summary>
/// <remarks>
/// ProDOS is block-based (512-byte blocks). The volume directory starts at block 2
/// and chains through adjacent blocks via a prev/next pointer pair at the start of
/// each directory block. Each directory block holds thirteen 39-byte entries.
/// File storage tiers: seedling (1 block, up to 512 bytes), sapling (index block of
/// block pointers, up to 128 KB), tree (master index -> index blocks -> data).
/// </remarks>
public sealed class ProDosReader : IDisposable {

  public const int BlockSize = 512;
  public const int VolumeDirStartBlock = 2;
  public const int EntriesPerBlock = 13;
  public const int EntrySize = 39;

  /// <summary>.2mg header magic ("2IMG") at offset 0.</summary>
  private static readonly byte[] TwoImgMagic = "2IMG"u8.ToArray();

  private readonly byte[] _image;
  private readonly int _imageStart;  // Offset into _image where block 0 starts (0 for .po, 64 for .2mg).
  private readonly List<ProDosEntry> _entries = [];

  public IReadOnlyList<ProDosEntry> Entries => _entries;
  public string VolumeName { get; private set; } = "";

  public ProDosReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _image = ms.ToArray();
    _imageStart = DetectImageStart(_image);
    Parse();
  }

  public ProDosReader(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    _image = data;
    _imageStart = DetectImageStart(_image);
    Parse();
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

    while (block != 0 && visited.Add(block)) {
      if (BlockOffset(block) + BlockSize > _image.Length) break;
      var data = Block(block);
      var nextBlock = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2, 2));

      for (var i = 0; i < EntriesPerBlock; i++) {
        // Entries start at offset 4 within the block.
        var eo = 4 + i * EntrySize;

        // First entry in block 2 is Volume Directory Header (skip). First entry in a
        // subdirectory's first block is the Subdirectory Header (skip).
        if (firstBlock && i == 0) continue;

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

  public void Dispose() { }
}
