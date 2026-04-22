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
/// This writer emits a single-level (flat) volume directory with seedling / sapling storage
/// types as appropriate, and a volume bit map pointing at block 6. Subdirectories are not
/// supported (unlike full ProDOS which allows hierarchy).
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

    // Per-file allocation state.
    var entries = new List<(string Name, byte FileType, byte StorageType, int KeyPointer,
                            int BlocksUsed, int Eof)>();

    foreach (var (rawName, data, fileType) in this._files) {
      var name = SanitizeName(rawName);

      if (data.Length == 0) {
        // Empty-file: seedling with 1 key block of zeros.
        var key = AllocateBlock(used, ref nextFreeBlock);
        entries.Add((name, fileType, StorageType: (byte)1, KeyPointer: key, BlocksUsed: 1, Eof: 0));
        continue;
      }

      if (data.Length <= BlockSize) {
        // Seedling — single block holds the whole file.
        var key = AllocateBlock(used, ref nextFreeBlock);
        Buffer.BlockCopy(data, 0, image, key * BlockSize, data.Length);
        entries.Add((name, fileType, StorageType: (byte)1, KeyPointer: key,
                     BlocksUsed: 1, Eof: data.Length));
      } else {
        // Sapling — index block + data blocks (up to 256 blocks = 128 KB).
        var dataBlockCount = (data.Length + BlockSize - 1) / BlockSize;
        if (dataBlockCount > 256)
          throw new InvalidOperationException(
            $"ProDOS: file '{name}' exceeds 128 KB sapling capacity (tree storage not implemented).");

        var indexKey = AllocateBlock(used, ref nextFreeBlock);
        var dataBlocks = new int[dataBlockCount];
        for (var i = 0; i < dataBlockCount; i++) {
          dataBlocks[i] = AllocateBlock(used, ref nextFreeBlock);
          var offset = i * BlockSize;
          var take = Math.Min(BlockSize, data.Length - offset);
          Buffer.BlockCopy(data, offset, image, dataBlocks[i] * BlockSize, take);
        }

        // Index block: low bytes at [0..255], high bytes at [256..511].
        var idxOff = indexKey * BlockSize;
        for (var i = 0; i < dataBlockCount; i++) {
          image[idxOff + i] = (byte)(dataBlocks[i] & 0xFF);
          image[idxOff + 256 + i] = (byte)((dataBlocks[i] >> 8) & 0xFF);
        }

        entries.Add((name, fileType, StorageType: (byte)2, KeyPointer: indexKey,
                     BlocksUsed: dataBlockCount + 1, Eof: data.Length));
      }
    }

    WriteVolumeDirectory(image, entries, volumeName, totalBlocks);
    WriteBitmap(image, used, totalBlocks, bitmapBlocks);

    return image;
  }

  private static int AllocateBlock(bool[] used, ref int cursor) {
    while (cursor < used.Length && used[cursor]) cursor++;
    if (cursor >= used.Length)
      throw new InvalidOperationException("ProDOS: out of free blocks.");
    used[cursor] = true;
    return cursor++;
  }

  private static void WriteVolumeDirectory(byte[] image,
      List<(string Name, byte FileType, byte StorageType, int KeyPointer, int BlocksUsed, int Eof)> entries,
      string volumeName,
      int totalBlocks) {

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
    var volName = SanitizeName(volumeName);
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
      (ushort)entries.Count);                          // file_count
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(headerOff + 0x26),
      (ushort)BitmapStartBlock);                       // bit_map_pointer
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(headerOff + 0x28),
      (ushort)totalBlocks);                            // total_blocks

    // Write entries starting at slot 1 of block 2, then continuing into blocks 3/4/5.
    // Block 2 has (EntriesPerBlock - 1) = 12 available slots after the header; subsequent
    // blocks have a full 13 slots each (52 total across 4 blocks - 1 header = 51 files).
    var slotGlobalIndex = 1;  // block 2 slot 0 = header, first real entry is slot 1
    foreach (var entry in entries) {
      var dirBlockIndex = slotGlobalIndex / EntriesPerBlock;
      var slotInBlock = slotGlobalIndex % EntriesPerBlock;
      if (dirBlockIndex >= VolumeDirBlockCount)
        throw new InvalidOperationException(
          $"ProDOS: too many files for the {VolumeDirBlockCount}-block volume directory.");

      var dirBlockNo = VolumeDirStartBlock + dirBlockIndex;
      var entryOff = dirBlockNo * BlockSize + 4 + slotInBlock * EntrySize;

      image[entryOff + 0] = (byte)((entry.StorageType << 4) | (entry.Name.Length & 0x0F));
      for (var i = 0; i < entry.Name.Length && i < 15; i++)
        image[entryOff + 1 + i] = (byte)entry.Name[i];
      image[entryOff + 0x10] = entry.FileType;
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 0x11), (ushort)entry.KeyPointer);
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 0x13), (ushort)entry.BlocksUsed);
      // EOF is 24-bit LE at 0x15-0x17.
      image[entryOff + 0x15] = (byte)(entry.Eof & 0xFF);
      image[entryOff + 0x16] = (byte)((entry.Eof >> 8) & 0xFF);
      image[entryOff + 0x17] = (byte)((entry.Eof >> 16) & 0xFF);
      // creation date/time at 0x18-0x1B = 0.
      image[entryOff + 0x1C] = 0x00;  // version
      image[entryOff + 0x1D] = 0x00;  // min_version
      image[entryOff + 0x1E] = 0xE3;  // access
      // aux_type at 0x1F-0x20 = 0 (load address).
      // last_mod at 0x21-0x24 = 0.
      // header_pointer (parent directory block) at 0x25-0x26.
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 0x25), VolumeDirStartBlock);

      slotGlobalIndex++;
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
    var s = Path.GetFileName(raw).ToUpperInvariant();
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
