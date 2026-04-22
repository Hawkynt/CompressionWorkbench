using System.Text;

namespace FileSystem.Adf;

/// <summary>
/// Reads and extracts files from an Amiga Disk File (.adf) image.
/// Supports both OFS (Original File System) and FFS (Fast File System) disk images.
/// Standard DD ADF images are exactly 901,120 bytes (1760 sectors of 512 bytes).
/// </summary>
public sealed class AdfReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly byte[] _disk;
  private readonly bool _isFfs;
  private bool _disposed;

  private const int SectorSize   = 512;
  private const int RootSector   = 880;
  private const int TotalSectors = 1760;
  private const int DiskSize     = TotalSectors * SectorSize;

  // AmigaDOS block type constants.
  private const uint TypeHeader    = 2;
  private const uint TypeData      = 8;
  private const uint SecTypeRoot   = 1;
  private const uint SecTypeDir    = 2;
  private const int  SecTypeFile   = unchecked((int)0xFFFFFFFD); // -3
  private const uint SecTypeFileFfs = unchecked(0xFFFFFFFD);

  // Hash table and block layout offsets.
  private const int HashTableOffset  = 24;   // 72 × uint32 BE hash table entries
  private const int HashTableCount   = 72;
  private const int FileSizeOffset   = 324;  // uint32 BE file size in both OFS and FFS header
  private const int FirstDataOffset  = 16;   // uint32 BE pointer to first OFS data block
  private const int DataBlockPtrsTop = 308;  // offset of the first (highest-indexed) data block pointer
  private const int HashChainOffset  = 496;  // uint32 BE — next entry in same hash bucket
  private const int ExtBlockOffset   = 496;  // same field used for extension blocks
  private const int NameOffset       = 432;  // first byte = length, then up to 30 ASCII chars
  private const int SecTypeWordOff   = 508;  // uint32 BE secondary type at end of block

  /// <summary>Gets all file and directory entries found in the disk image.</summary>
  public IReadOnlyList<AdfEntry> Entries { get; }

  /// <summary>Gets whether the disk uses FFS (Fast File System).
  /// When <see langword="false"/> the disk uses OFS (Original File System).</summary>
  public bool IsFfs => this._isFfs;

  /// <summary>
  /// Initializes a new <see cref="AdfReader"/> and parses the ADF disk image.
  /// </summary>
  /// <param name="stream">A stream containing the ADF disk image (must be at least 901,120 bytes).</param>
  /// <param name="leaveOpen">
  /// <see langword="true"/> to leave <paramref name="stream"/> open after this reader is disposed;
  /// <see langword="false"/> to close it.
  /// </param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is <see langword="null"/>.</exception>
  /// <exception cref="InvalidDataException">Thrown when the stream is too short or the root block is invalid.</exception>
  public AdfReader(Stream stream, bool leaveOpen = false) {
    this._stream    = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    // Read the full disk image into memory.  We tolerate short images
    // (e.g. partial dumps) by zero-padding the unread portion.
    this._disk = new byte[DiskSize];
    var read = 0;
    while (read < DiskSize) {
      var n = stream.Read(this._disk, read, DiskSize - read);
      if (n == 0) break;
      read += n;
    }

    // Byte 3 of the boot block encodes the file system type:
    //   bit 0 = 0 → OFS,  bit 0 = 1 → FFS.
    this._isFfs = (this._disk[3] & 1) != 0;

    var entries = new List<AdfEntry>();
    WalkDirectory(RootSector, "", entries, isRoot: true);
    this.Entries = entries;
  }

  // ── Directory walking ────────────────────────────────────────────────────

  private void WalkDirectory(int dirBlock, string parentPath, List<AdfEntry> entries, bool isRoot = false) {
    var sector = ReadSector(dirBlock);

    // Validate the primary type (must be T_HEADER = 2).
    var type = ReadUInt32BE(sector, 0);
    if (type != TypeHeader) return;

    // For root block, validate secondary type = ST_ROOT = 1.
    if (isRoot) {
      var secType = ReadUInt32BE(sector, SecTypeWordOff);
      if (secType != SecTypeRoot) return;
    }

    // Iterate the 72-entry hash table.
    for (var i = 0; i < HashTableCount; i++) {
      var firstBlock = ReadUInt32BE(sector, HashTableOffset + i * 4);
      if (firstBlock == 0) continue;
      ProcessHashChain((int)firstBlock, parentPath, entries);
    }
  }

  private void ProcessHashChain(int block, string parentPath, List<AdfEntry> entries) {
    while (block != 0) {
      var sector = ReadSector(block);

      // Secondary type determines whether this is a file or directory.
      var secType = (int)ReadUInt32BE(sector, SecTypeWordOff);

      // Read the entry name (Pascal-style: length byte followed by ASCII chars).
      var nameLen = sector[NameOffset];
      if (nameLen > 30) nameLen = 30;
      var name     = Encoding.ASCII.GetString(sector, NameOffset + 1, nameLen);
      var fullPath = parentPath.Length > 0 ? parentPath + "/" + name : name;

      if (secType == (int)SecTypeDir) {
        // Directory header block.
        entries.Add(new AdfEntry {
          Name        = name,
          FullPath    = fullPath,
          IsDirectory = true,
          HeaderBlock = block,
          Size        = 0,
        });
        WalkDirectory(block, fullPath, entries);
      } else if (secType == SecTypeFile) {
        // File header block.
        var fileSize = (int)ReadUInt32BE(sector, FileSizeOffset);
        entries.Add(new AdfEntry {
          Name        = name,
          FullPath    = fullPath,
          IsDirectory = false,
          HeaderBlock = block,
          Size        = fileSize,
        });
      }

      // Advance along the hash chain (next entry sharing the same hash bucket).
      block = (int)ReadUInt32BE(sector, HashChainOffset);
    }
  }

  // ── Extraction ───────────────────────────────────────────────────────────

  /// <summary>
  /// Extracts and returns the raw byte content of the specified file entry.
  /// </summary>
  /// <param name="entry">The <see cref="AdfEntry"/> to extract.</param>
  /// <returns>
  /// The file data as a byte array, or an empty array if <paramref name="entry"/>
  /// is a directory or has zero size.
  /// </returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is <see langword="null"/>.</exception>
  public byte[] Extract(AdfEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.IsDirectory || entry.Size == 0) return [];

    var fileSize = entry.Size;
    var result   = new byte[fileSize];
    var written  = 0;

    if (this._isFfs) {
      // FFS: each data block is a pure 512-byte sector (no per-block header).
      // Data block pointers are stored in the file header and extension blocks,
      // arranged in reverse order: offset 308 = first block, 304 = second, etc.
      var dataBlocks = new List<int>();
      CollectFfsDataBlocks(entry.HeaderBlock, dataBlocks);

      foreach (var db in dataBlocks) {
        if (written >= fileSize) break;
        var data    = ReadSector(db);
        var toCopy  = Math.Min(SectorSize, fileSize - written);
        Buffer.BlockCopy(data, 0, result, written, toCopy);
        written += toCopy;
      }
    } else {
      // OFS: each data block starts with a 24-byte header; 488 usable bytes follow.
      // The file header holds a pointer to the first data block at offset 16.
      var headerSector = ReadSector(entry.HeaderBlock);
      var nextData     = ReadUInt32BE(headerSector, FirstDataOffset);

      while (nextData != 0 && written < fileSize) {
        var data     = ReadSector((int)nextData);
        var dataSize = (int)ReadUInt32BE(data, 12); // bytes of payload in this block
        dataSize     = Math.Min(dataSize, fileSize - written);
        Buffer.BlockCopy(data, 24, result, written, dataSize);
        written  += dataSize;
        nextData  = ReadUInt32BE(data, 16); // next data block pointer
      }
    }

    return result;
  }

  /// <summary>
  /// Collects the ordered list of FFS data block sector numbers for a file,
  /// following extension blocks as needed.
  /// </summary>
  /// <param name="headerBlock">The sector number of the file header block.</param>
  /// <param name="dataBlocks">The list to append sector numbers to.</param>
  private void CollectFfsDataBlocks(int headerBlock, List<int> dataBlocks) {
    var sector = ReadSector(headerBlock);
    AppendDataBlockPtrs(sector, dataBlocks);

    // Extension blocks share the same layout; the pointer is at offset 496.
    var ext = ReadUInt32BE(sector, ExtBlockOffset);
    while (ext != 0) {
      var extSector = ReadSector((int)ext);
      AppendDataBlockPtrs(extSector, dataBlocks);
      ext = ReadUInt32BE(extSector, ExtBlockOffset);
    }
  }

  /// <summary>
  /// Reads the 72 data block pointers from a file header or extension block sector
  /// (stored in reverse order starting at offset 308) and appends valid ones to
  /// <paramref name="dataBlocks"/> in forward (file) order.
  /// </summary>
  /// <param name="sector">The 512-byte sector buffer.</param>
  /// <param name="dataBlocks">Destination list.</param>
  private static void AppendDataBlockPtrs(byte[] sector, List<int> dataBlocks) {
    // Pointers are packed from offset 308 downward: offset 308 = block 1,
    // offset 304 = block 2, … offset 24 = block 72.
    var ptrs = new List<int>(HashTableCount);
    for (var i = 0; i < HashTableCount; i++) {
      var p = ReadUInt32BE(sector, DataBlockPtrsTop - i * 4);
      if (p != 0) ptrs.Add((int)p);
    }
    dataBlocks.AddRange(ptrs);
  }

  // ── Low-level helpers ────────────────────────────────────────────────────

  /// <summary>
  /// Returns a 512-byte copy of the sector at the given sector number,
  /// or a zero-filled buffer if the sector number is out of range.
  /// </summary>
  /// <param name="sectorNum">Zero-based sector index.</param>
  /// <returns>A 512-byte array containing the sector data.</returns>
  private byte[] ReadSector(int sectorNum) {
    var result = new byte[SectorSize];
    var offset = sectorNum * SectorSize;
    if (offset >= 0 && offset + SectorSize <= this._disk.Length)
      Buffer.BlockCopy(this._disk, offset, result, 0, SectorSize);
    return result;
  }

  /// <summary>Reads a big-endian unsigned 32-bit integer from <paramref name="data"/> at <paramref name="offset"/>.</summary>
  /// <param name="data">The byte array to read from.</param>
  /// <param name="offset">The byte offset of the first (most-significant) byte.</param>
  /// <returns>The decoded value.</returns>
  private static uint ReadUInt32BE(byte[] data, int offset) =>
    (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
