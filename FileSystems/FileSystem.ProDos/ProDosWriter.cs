#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ProDos;

/// <summary>
/// Builds a fresh Apple ProDOS block-ordered disk image (<c>.po</c>) from scratch (WORM).
/// </summary>
/// <remarks>
/// <para>
/// Layout: 512-byte blocks. Canonical sizes are 280 blocks (143 360 B — 5.25" floppy) and
/// 1 600 blocks (819 200 B — 800 KB Mac-format 3.5" floppy). The volume directory starts
/// at block 2 and chains through blocks 2..5 (4 blocks total in this writer). Each directory
/// block holds thirteen 39-byte entries at offset 4.
/// </para>
/// <para>
/// This writer emits a hierarchical volume directory: files whose name contains '/'
/// separators are placed inside real ProDOS subdirectories (storage type 0xD pointing at a
/// 0xE subdirectory header) rather than flattened into the volume root. File data is stored
/// with seedling / sapling / tree storage types as appropriate.
/// </para>
/// <para>
/// Subdirectories occupy a single 512-byte block in this writer, so a subdirectory holds at
/// most twelve children (one slot is the subdirectory header). The volume directory keeps its
/// 4-block chain (51 root children). Deeper or wider trees that exceed a single subdirectory
/// block are rejected with a clear error rather than silently corrupting the image.
/// </para>
/// </remarks>
public sealed class ProDosWriter {

  private const int BlockSize = ProDosReader.BlockSize;   // 512
  public const int FloppyTotalBlocks = 280;               // 143 360 bytes
  public const int Disk800KTotalBlocks = 1600;            // 819 200 bytes
  private const int VolumeDirStartBlock = ProDosReader.VolumeDirStartBlock;  // 2
  private const int VolumeDirBlockCount = 4;              // blocks 2..5
  private const int BitmapStartBlock = 6;                 // block 6 = volume bit map
  private const int EntriesPerBlock = ProDosReader.EntriesPerBlock;  // 13
  private const int EntrySize = ProDosReader.EntrySize;   // 39

  private readonly List<(string Name, byte[] Data, byte FileType)> _files = [];

  /// <summary>Adds a file (default file_type = BIN 0x06).</summary>
  public void AddFile(string name, byte[] data) => this._files.Add((name, data, FileType: (byte)0x06));

  public void AddFile(string name, byte fileType, byte[] data) => this._files.Add((name, data, fileType));

  /// <summary>A file resolved to its data storage (key block already written to the image).</summary>
  private sealed class FileNode {
    public required string Name;
    public byte FileType;
    public byte StorageType;
    public int KeyPointer;
    public int BlocksUsed;
    public int Eof;
  }

  /// <summary>A directory node in the tree being assembled before it is serialised to blocks.</summary>
  private sealed class DirNode {
    public required string Name;
    public readonly List<FileNode> Files = [];
    public readonly Dictionary<string, DirNode> SubDirs = new(StringComparer.Ordinal);
    public readonly List<DirNode> SubDirOrder = [];

    /// <summary>Block that holds this directory's first (only, in this writer) block.</summary>
    public int FirstBlock;
    /// <summary>Parent directory's block where this directory's 0xD entry lives.</summary>
    public int ParentBlock;
    /// <summary>1-based slot index of this directory's 0xD entry within the parent block.</summary>
    public int ParentEntryNumber;

    /// <summary>Total children (files + subdirs) — written to the directory header's file_count.</summary>
    public int ChildCount => this.Files.Count + this.SubDirOrder.Count;

    public DirNode GetOrAddSubDir(string rawName) {
      var name = SanitizeName(rawName);
      if (this.SubDirs.TryGetValue(name, out var existing))
        return existing;
      var dir = new DirNode { Name = name };
      this.SubDirs[name] = dir;
      this.SubDirOrder.Add(dir);
      return dir;
    }
  }

  /// <summary>Builds a canonical 143 360-byte (floppy) ProDOS image by default.</summary>
  public byte[] Build(string volumeName = "WORM", int totalBlocks = FloppyTotalBlocks) {
    if (totalBlocks is not (FloppyTotalBlocks or Disk800KTotalBlocks))
      throw new ArgumentException(
        $"ProDOS: unsupported total-block count {totalBlocks}; expected 280 or 1600.",
        nameof(totalBlocks));

    var image = new byte[totalBlocks * BlockSize];
    var used = new bool[totalBlocks];

    // Reserve: blocks 0-1 (boot), 2-5 (volume directory), bitmap block(s).
    for (var b = 0; b < VolumeDirStartBlock + VolumeDirBlockCount; b++) used[b] = true;
    var bitmapBlocks = (totalBlocks + (BlockSize * 8) - 1) / (BlockSize * 8);
    for (var b = 0; b < bitmapBlocks; b++) used[BitmapStartBlock + b] = true;

    var nextFreeBlock = BitmapStartBlock + bitmapBlocks;

    // 1. Assemble the directory tree from the (possibly nested) file names, storing each
    //    file's data into the image as we go.
    var root = new DirNode { Name = SanitizeName(volumeName) };
    foreach (var (rawName, data, fileType) in this._files) {
      var (dir, leafName) = ResolvePath(root, rawName);
      var file = StoreFileData(image, used, ref nextFreeBlock, leafName, fileType, data);
      dir.Files.Add(file);
    }

    // 2. Allocate one block per subdirectory (the volume directory uses its fixed 4-block chain).
    AllocateSubDirBlocks(root, used, ref nextFreeBlock);

    // 3. Serialise the volume directory and every subdirectory into their blocks.
    WriteVolumeDirectory(image, root, totalBlocks);
    WriteSubDirectories(image, root);

    WriteBitmap(image, used, totalBlocks, bitmapBlocks);

    return image;
  }

  /// <summary>
  /// Walks/creates the subdirectory chain for <paramref name="rawName"/> and returns the owning
  /// directory plus the sanitised leaf (file) name.
  /// </summary>
  private static (DirNode Dir, string LeafName) ResolvePath(DirNode root, string rawName) {
    var parts = (rawName ?? "")
      .Replace('\\', '/')
      .Split('/', StringSplitOptions.RemoveEmptyEntries);

    if (parts.Length == 0)
      return (root, SanitizeName(rawName ?? ""));

    var dir = root;
    for (var i = 0; i < parts.Length - 1; i++)
      dir = dir.GetOrAddSubDir(parts[i]);

    return (dir, SanitizeName(parts[^1]));
  }

  /// <summary>Writes a file's data blocks into the image and returns its directory-entry shape.</summary>
  private static FileNode StoreFileData(byte[] image, bool[] used, ref int nextFreeBlock,
      string name, byte fileType, byte[] data) {

    if (data.Length == 0) {
      // Empty-file: seedling with 1 key block of zeros.
      var key = AllocateBlock(used, ref nextFreeBlock);
      return new FileNode { Name = name, FileType = fileType, StorageType = 1, KeyPointer = key, BlocksUsed = 1, Eof = 0 };
    }

    if (data.Length <= BlockSize) {
      // Seedling — single block holds the whole file.
      var key = AllocateBlock(used, ref nextFreeBlock);
      Buffer.BlockCopy(data, 0, image, key * BlockSize, data.Length);
      return new FileNode { Name = name, FileType = fileType, StorageType = 1, KeyPointer = key, BlocksUsed = 1, Eof = data.Length };
    }

    var dataBlockCount = (data.Length + BlockSize - 1) / BlockSize;

    if (dataBlockCount <= 256) {
      // Sapling — one index block + up to 256 data blocks (128 KB).
      var indexKey = AllocateBlock(used, ref nextFreeBlock);
      var dataBlocks = new int[dataBlockCount];
      for (var i = 0; i < dataBlockCount; i++) {
        dataBlocks[i] = AllocateBlock(used, ref nextFreeBlock);
        var offset = i * BlockSize;
        var take = Math.Min(BlockSize, data.Length - offset);
        Buffer.BlockCopy(data, offset, image, dataBlocks[i] * BlockSize, take);
      }

      var idxOff = indexKey * BlockSize;
      for (var i = 0; i < dataBlockCount; i++) {
        image[idxOff + i] = (byte)(dataBlocks[i] & 0xFF);
        image[idxOff + 256 + i] = (byte)((dataBlocks[i] >> 8) & 0xFF);
      }

      return new FileNode { Name = name, FileType = fileType, StorageType = 2, KeyPointer = indexKey, BlocksUsed = dataBlockCount + 1, Eof = data.Length };
    }

    // Tree — master index block + up to 256 subordinate index blocks,
    // each pointing at up to 256 data blocks (32 MB max).
    if (dataBlockCount > 256 * 256)
      throw new InvalidOperationException(
        $"ProDOS: file '{name}' exceeds 32 MB tree capacity (65536 data blocks).");

    var indexBlockCount = (dataBlockCount + 255) / 256;
    var masterKey = AllocateBlock(used, ref nextFreeBlock);
    var subIndexBlocks = new int[indexBlockCount];
    var blocksUsed = 1 + indexBlockCount + dataBlockCount;

    var dataIdx = 0;
    for (var si = 0; si < indexBlockCount; si++) {
      subIndexBlocks[si] = AllocateBlock(used, ref nextFreeBlock);
      var subOff = subIndexBlocks[si] * BlockSize;
      var remainingInSub = Math.Min(256, dataBlockCount - dataIdx);
      for (var di = 0; di < remainingInSub; di++) {
        var dataBlock = AllocateBlock(used, ref nextFreeBlock);
        var byteOffset = dataIdx * BlockSize;
        var take = Math.Min(BlockSize, data.Length - byteOffset);
        Buffer.BlockCopy(data, byteOffset, image, dataBlock * BlockSize, take);
        image[subOff + di] = (byte)(dataBlock & 0xFF);
        image[subOff + 256 + di] = (byte)((dataBlock >> 8) & 0xFF);
        dataIdx++;
      }
    }

    // Master index: low-bytes at [0..255], high-bytes at [256..511].
    var masterOff = masterKey * BlockSize;
    for (var si = 0; si < indexBlockCount; si++) {
      image[masterOff + si] = (byte)(subIndexBlocks[si] & 0xFF);
      image[masterOff + 256 + si] = (byte)((subIndexBlocks[si] >> 8) & 0xFF);
    }

    return new FileNode { Name = name, FileType = fileType, StorageType = 3, KeyPointer = masterKey, BlocksUsed = blocksUsed, Eof = data.Length };
  }

  /// <summary>Allocates one block for each subdirectory (depth-first), recording its first block.</summary>
  private static void AllocateSubDirBlocks(DirNode dir, bool[] used, ref int nextFreeBlock) {
    foreach (var sub in dir.SubDirOrder) {
      sub.FirstBlock = AllocateBlock(used, ref nextFreeBlock);
      AllocateSubDirBlocks(sub, used, ref nextFreeBlock);
    }
  }

  private static int AllocateBlock(bool[] used, ref int cursor) {
    while (cursor < used.Length && used[cursor]) cursor++;
    if (cursor >= used.Length)
      throw new InvalidOperationException("ProDOS: out of free blocks.");
    used[cursor] = true;
    return cursor++;
  }

  private static void WriteVolumeDirectory(byte[] image, DirNode root, int totalBlocks) {
    // Write directory block link chain: block 2 <-> 3 <-> 4 <-> 5.
    for (var i = 0; i < VolumeDirBlockCount; i++) {
      var blockNo = VolumeDirStartBlock + i;
      var off = blockNo * BlockSize;
      // prev pointer.
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0),
        (ushort)(i == 0 ? 0 : blockNo - 1));
      // next pointer.
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 2),
        (ushort)(i == VolumeDirBlockCount - 1 ? 0 : blockNo + 1));
    }

    // Volume directory header at block 2, entry 0.
    var blockStart = VolumeDirStartBlock * BlockSize;
    var headerOff = blockStart + 4;
    var volName = root.Name;
    if (volName.Length > 15) volName = volName[..15];
    // storage_type_nibble = 0xF (volume dir header). name_length = lower nibble.
    image[headerOff + 0] = (byte)((0xF << 4) | (volName.Length & 0x0F));
    for (var i = 0; i < volName.Length; i++) image[headerOff + 1 + i] = (byte)volName[i];
    // ProDOS timestamps (creation date/time) at offset 0x18 — left zero.
    image[headerOff + 0x1F] = 0x00;  // version
    image[headerOff + 0x20] = 0x00;  // min_version
    image[headerOff + 0x21] = 0xE3;  // access: destroy/rename/write/read bits
    image[headerOff + 0x22] = (byte)EntrySize;         // entry_length
    image[headerOff + 0x23] = (byte)EntriesPerBlock;   // entries_per_block
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(headerOff + 0x24),
      (ushort)root.ChildCount);                        // file_count
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(headerOff + 0x26),
      (ushort)BitmapStartBlock);                       // bit_map_pointer
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(headerOff + 0x28),
      (ushort)totalBlocks);                            // total_blocks

    // Write children starting at slot 1 of block 2 (slot 0 is the header), then into blocks 3/4/5.
    WriteDirectoryChildren(image, root, VolumeDirStartBlock, VolumeDirBlockCount, firstSlot: 1);
  }

  /// <summary>
  /// Writes a directory's file and subdirectory entries into its block chain starting at
  /// <paramref name="firstSlot"/>. Subdirectory entries (0xD) record their first block, and the
  /// subdirectory node remembers the parent block + slot so its 0xE header can back-reference them.
  /// </summary>
  private static void WriteDirectoryChildren(byte[] image, DirNode dir,
      int firstBlock, int blockCount, int firstSlot) {

    var slot = firstSlot;

    int PlaceEntry() {
      var dirBlockIndex = slot / EntriesPerBlock;
      var slotInBlock = slot % EntriesPerBlock;
      if (dirBlockIndex >= blockCount)
        throw new InvalidOperationException(
          $"ProDOS: directory '{dir.Name}' has too many entries for its {blockCount}-block capacity " +
          $"({EntriesPerBlock * blockCount - 1} max). Wider directory growth is not supported.");

      var entryOff = (firstBlock + dirBlockIndex) * BlockSize + 4 + slotInBlock * EntrySize;
      return entryOff;
    }

    // Files first, then subdirectories — order is not semantically significant.
    foreach (var file in dir.Files) {
      WriteFileEntry(image, PlaceEntry(), file, firstBlock);
      slot++;
    }

    foreach (var sub in dir.SubDirOrder) {
      WriteSubDirEntry(image, PlaceEntry(), sub, firstBlock);
      // The consumed slot pins the parent block + 1-based entry number for the 0xE header.
      sub.ParentBlock = firstBlock + slot / EntriesPerBlock;
      sub.ParentEntryNumber = (slot % EntriesPerBlock) + 1;
      slot++;
    }
  }

  private static void WriteFileEntry(byte[] image, int entryOff, FileNode file, int headerBlock) {
    image[entryOff + 0] = (byte)((file.StorageType << 4) | (file.Name.Length & 0x0F));
    for (var i = 0; i < file.Name.Length && i < 15; i++)
      image[entryOff + 1 + i] = (byte)file.Name[i];
    image[entryOff + 0x10] = file.FileType;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 0x11), (ushort)file.KeyPointer);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 0x13), (ushort)file.BlocksUsed);
    // EOF is 24-bit LE at 0x15-0x17.
    image[entryOff + 0x15] = (byte)(file.Eof & 0xFF);
    image[entryOff + 0x16] = (byte)((file.Eof >> 8) & 0xFF);
    image[entryOff + 0x17] = (byte)((file.Eof >> 16) & 0xFF);
    // creation date/time at 0x18-0x1B = 0.
    image[entryOff + 0x1C] = 0x00;  // version
    image[entryOff + 0x1D] = 0x00;  // min_version
    image[entryOff + 0x1E] = 0xE3;  // access
    // aux_type at 0x1F-0x20 = 0 (load address).
    // last_mod at 0x21-0x24 = 0.
    // header_pointer (this directory's first block) at 0x25-0x26.
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 0x25), (ushort)headerBlock);
  }

  /// <summary>Writes a subdirectory entry (storage type 0xD) into the parent directory block.</summary>
  private static void WriteSubDirEntry(byte[] image, int entryOff, DirNode sub, int headerBlock) {
    image[entryOff + 0] = (byte)((0x0D << 4) | (sub.Name.Length & 0x0F));
    for (var i = 0; i < sub.Name.Length && i < 15; i++)
      image[entryOff + 1 + i] = (byte)sub.Name[i];
    image[entryOff + 0x10] = 0x0F;  // file_type DIR ($0F)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 0x11), (ushort)sub.FirstBlock);  // key_pointer
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 0x13), 1);   // blocks_used (single-block dir)
    // EOF = directory size in bytes = blocks * 512.
    var eof = BlockSize;
    image[entryOff + 0x15] = (byte)(eof & 0xFF);
    image[entryOff + 0x16] = (byte)((eof >> 8) & 0xFF);
    image[entryOff + 0x17] = (byte)((eof >> 16) & 0xFF);
    image[entryOff + 0x1C] = 0x00;  // version
    image[entryOff + 0x1D] = 0x00;  // min_version
    image[entryOff + 0x1E] = 0xE3;  // access
    // header_pointer (parent directory's first block) at 0x25-0x26.
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 0x25), (ushort)headerBlock);
  }

  /// <summary>
  /// Recursively serialises each subdirectory's own block: a 0xE subdirectory header followed by
  /// its file and subdirectory entries. Must run after <see cref="WriteDirectoryChildren"/> has
  /// populated each node's parent-block/parent-entry back-references.
  /// </summary>
  private static void WriteSubDirectories(byte[] image, DirNode dir) {
    foreach (var sub in dir.SubDirOrder) {
      var off = sub.FirstBlock * BlockSize;
      // Single-block directory: prev = next = 0.
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0), 0);
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 2), 0);

      // Subdirectory header at entry 0 (offset 4): storage_type nibble 0xE.
      var hdr = off + 4;
      image[hdr + 0] = (byte)((0x0E << 4) | (sub.Name.Length & 0x0F));
      for (var i = 0; i < sub.Name.Length && i < 15; i++) image[hdr + 1 + i] = (byte)sub.Name[i];
      image[hdr + 0x10] = 0x75;  // reserved magic byte ($75) ProDOS expects in subdir headers
      // creation date/time at 0x18-0x1B = 0.
      image[hdr + 0x1F] = 0x00;  // version
      image[hdr + 0x20] = 0x00;  // min_version
      image[hdr + 0x21] = 0xE3;  // access
      image[hdr + 0x22] = (byte)EntrySize;         // entry_length
      image[hdr + 0x23] = (byte)EntriesPerBlock;   // entries_per_block
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(hdr + 0x24), (ushort)sub.ChildCount);  // file_count
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(hdr + 0x26), (ushort)sub.ParentBlock);  // parent_pointer
      image[hdr + 0x28] = (byte)sub.ParentEntryNumber;  // parent_entry_number
      image[hdr + 0x29] = (byte)EntrySize;              // parent_entry_length

      // Write this subdirectory's children into its single block (slot 0 is the header).
      WriteDirectoryChildren(image, sub, sub.FirstBlock, blockCount: 1, firstSlot: 1);

      // Recurse into deeper subdirectories.
      WriteSubDirectories(image, sub);
    }
  }

  private static void WriteBitmap(byte[] image, bool[] used, int totalBlocks, int bitmapBlocks) {
    // ProDOS convention: bit 7 of byte 0 = block 0; bit 0 of byte 0 = block 7; etc.
    // Bit SET = free. Block N's bit lives at byte N/8, bit 7-(N%8).
    for (var blk = 0; blk < bitmapBlocks; blk++) {
      var off = (BitmapStartBlock + blk) * BlockSize;
      for (var b = 0; b < BlockSize; b++) {
        byte mask = 0;
        for (var bit = 0; bit < 8; bit++) {
          var globalBlock = blk * BlockSize * 8 + b * 8 + bit;
          if (globalBlock >= totalBlocks) continue;   // past disk: leave zero
          if (!used[globalBlock]) mask |= (byte)(0x80 >> bit);
        }
        image[off + b] = mask;
      }
    }
  }

  private static string SanitizeName(string raw) {
    if (string.IsNullOrEmpty(raw)) return "UNNAMED";
    var s = raw.ToUpperInvariant();
    // ProDOS name rules: letters, digits, '.', must start with letter.
    var sb = new StringBuilder(s.Length);
    foreach (var c in s) {
      if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.')
        sb.Append(c);
      else
        sb.Append('.');
    }
    var clean = sb.ToString().TrimStart('.', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
    if (clean.Length == 0) clean = "F" + sb;
    // Max 15 chars. Preserve TAIL to match the user's truncation convention.
    if (clean.Length > 15) clean = clean[^15..];
    // Must start with a letter.
    if (clean.Length == 0 || !(clean[0] >= 'A' && clean[0] <= 'Z'))
      clean = "F" + (clean.Length > 14 ? clean[^14..] : clean);
    if (clean.Length > 15) clean = clean[..15];
    return clean;
  }
}
