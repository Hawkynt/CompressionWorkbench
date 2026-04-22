#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Atari8;

/// <summary>
/// Builds a fresh Atari 8-bit AtariDOS 2.x <c>.atr</c> disk image from scratch (WORM).
/// </summary>
/// <remarks>
/// <para>
/// ATR layout: 16-byte header (magic 0x0296 + paragraph count + sector size) followed by
/// raw sector data. SS/SD (single-sided / single-density) has 720 sectors of 128 bytes each,
/// totaling 92 176 bytes. Sectors are numbered 1-based.
/// </para>
/// <para>
/// AtariDOS 2.0S reserves sector 360 for the VTOC and sectors 361-368 for the directory
/// (8 sectors * 8 directory slots of 16 bytes each = 64 files). File data is allocated
/// from sector 4 onward (sectors 1-3 are boot, 360-368 = directory). Each data sector's
/// last 3 bytes store: [file# top-bits, next-sector-hi] [next-sector-lo] [byte-count].
/// </para>
/// <para>
/// Filenames are 8 chars + 3 chars extension, upper-case ATASCII, space-padded.
/// </para>
/// </remarks>
public sealed class Atari8Writer {

  private const int SectorSize = Atari8Reader.DefaultSectorSize;       // 128
  private const int AtrHeaderSize = Atari8Reader.AtrHeaderSize;        // 16
  private const int TotalSectors = 720;                                // SS/SD
  private const int DataSize = TotalSectors * SectorSize;              // 92 160
  public const int ImageSize = AtrHeaderSize + DataSize;               // 92 176
  private const int VtocSector = 360;
  private const int DirectoryStartSector = Atari8Reader.DirectoryStartSector;  // 361
  private const int DirectorySectorCount = Atari8Reader.DirectorySectorCount;  // 8
  private const int EntriesPerDirectorySector = Atari8Reader.EntriesPerDirectorySector;  // 8
  private const int DirectoryEntrySize = Atari8Reader.DirectoryEntrySize;      // 16
  private const int MaxEntries = DirectorySectorCount * EntriesPerDirectorySector;  // 64
  private const int FirstUsableDataSector = 4;

  private readonly List<(string Name, byte[] Data)> _files = [];

  public void AddFile(string name, byte[] data) => this._files.Add((name, data));

  /// <summary>Builds the complete SS/SD ATR image (92 176 bytes).</summary>
  public byte[] Build() {
    if (this._files.Count > MaxEntries)
      throw new InvalidOperationException(
        $"AtariDOS: {this._files.Count} files exceeds directory limit of {MaxEntries}.");

    // Compute sectors required.
    var totalSectorsNeeded = 0;
    foreach (var (_, data) in this._files) {
      // Per sector payload = SectorSize - 3 (last 3 bytes are chain trailer).
      var sectors = Math.Max(1, (data.Length + SectorSize - 4) / (SectorSize - 3));
      totalSectorsNeeded += sectors;
    }
    // Usable data sectors = 720 - boot(3) - VTOC(1) - directory(8) = 708.
    // But we also avoid sector 720 and stay within our allocation window.
    const int availableDataSectors = TotalSectors - FirstUsableDataSector - (DirectorySectorCount + 1);
    // +1 for VTOC sector 360
    if (totalSectorsNeeded > availableDataSectors)
      throw new InvalidOperationException(
        $"AtariDOS: file data requires {totalSectorsNeeded} sectors; only {availableDataSectors} available.");

    var image = new byte[ImageSize];

    // --- ATR header ---
    image[0] = 0x96;
    image[1] = 0x02;
    // Paragraphs (16-byte units) = image data / 16.
    var paragraphs = DataSize / 16;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(2), (ushort)(paragraphs & 0xFFFF));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(4), SectorSize);
    // High paragraphs at offset 6 (0 for SS/SD — fits in 16 bits).
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(6), (ushort)(paragraphs >> 16));
    // Remaining header bytes stay zero.

    // --- Allocate sectors for each file ---
    // used[i] = true when sector i (1-based) is taken.
    var used = new bool[TotalSectors + 1];
    // Reserve boot (1-3), VTOC (360), directory (361-368).
    used[0] = true;
    for (var s = 1; s <= 3; s++) used[s] = true;
    used[VtocSector] = true;
    for (var s = DirectoryStartSector; s < DirectoryStartSector + DirectorySectorCount; s++)
      used[s] = true;

    var nextFree = FirstUsableDataSector;
    var perFileSectors = new List<int>[this._files.Count];

    for (var fi = 0; fi < this._files.Count; fi++) {
      var data = this._files[fi].Data;
      var payload = SectorSize - 3;
      var count = Math.Max(1, (data.Length + payload - 1) / payload);
      var sectors = new List<int>(count);
      for (var i = 0; i < count; i++) {
        while (nextFree <= TotalSectors && used[nextFree]) nextFree++;
        if (nextFree > TotalSectors)
          throw new InvalidOperationException("AtariDOS: out of free sectors.");
        // AtariDOS 2.0 can't address sector numbers with bits 10+ set — cap at 0x3FF.
        if (nextFree > 0x3FF)
          throw new InvalidOperationException("AtariDOS: sector index exceeds 10-bit limit.");
        used[nextFree] = true;
        sectors.Add(nextFree);
        nextFree++;
      }
      perFileSectors[fi] = sectors;
    }

    // --- Write data sectors + chain trailers ---
    for (var fi = 0; fi < this._files.Count; fi++) {
      var data = this._files[fi].Data;
      var sectors = perFileSectors[fi];
      var payload = SectorSize - 3;
      for (var i = 0; i < sectors.Count; i++) {
        var secOff = SectorDataOffset(sectors[i]);
        var dataStart = i * payload;
        var thisChunk = Math.Min(payload, Math.Max(0, data.Length - dataStart));
        if (thisChunk > 0)
          Buffer.BlockCopy(data, dataStart, image, secOff, thisChunk);

        // Chain trailer in last 3 bytes of the sector.
        var nextSector = (i + 1 < sectors.Count) ? sectors[i + 1] : 0;
        // byte 0: bits 2-7 = file# (low 6 bits of slot index), bits 0-1 = next-sector high 2 bits
        // byte 1: next-sector low 8 bits
        // byte 2: byte count in this sector (7 bits)
        var fileNo = fi & 0x3F;
        var nextHi = (nextSector >> 8) & 0x03;
        image[secOff + SectorSize - 3] = (byte)((fileNo << 2) | nextHi);
        image[secOff + SectorSize - 2] = (byte)(nextSector & 0xFF);
        image[secOff + SectorSize - 1] = (byte)(thisChunk & 0x7F);
      }
    }

    // --- Directory entries at sectors 361-368 ---
    for (var fi = 0; fi < this._files.Count; fi++) {
      var slotSector = DirectoryStartSector + fi / EntriesPerDirectorySector;
      var slotInSector = fi % EntriesPerDirectorySector;
      var entryOff = SectorDataOffset(slotSector) + slotInSector * DirectoryEntrySize;

      var (rawName, data) = this._files[fi];
      var (baseName, ext) = SplitName(rawName);

      // Flags: 0x42 = in-use (0x40) + DOS-2 file (0x02). Real AtariDOS also uses this value.
      image[entryOff + 0] = 0x42;
      // Sector count (LE).
      var secCount = perFileSectors[fi].Count;
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 1), (ushort)secCount);
      // Start sector (LE).
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 3), (ushort)perFileSectors[fi][0]);
      // Name + ext, space-padded.
      for (var i = 0; i < 8; i++)
        image[entryOff + 5 + i] = (byte)(i < baseName.Length ? baseName[i] : ' ');
      for (var i = 0; i < 3; i++)
        image[entryOff + 13 + i] = (byte)(i < ext.Length ? ext[i] : ' ');
    }

    // --- VTOC at sector 360 ---
    WriteVtoc(image, used);

    return image;
  }

  /// <summary>Byte offset (inside the ATR image) of the data payload for a 1-based sector number.</summary>
  private static int SectorDataOffset(int sector1Based) =>
    AtrHeaderSize + (sector1Based - 1) * SectorSize;

  /// <summary>Splits "name.ext" into 8/3 components. Falls back to full-name padding if no dot.</summary>
  private static (string BaseName, string Ext) SplitName(string raw) {
    if (string.IsNullOrEmpty(raw)) return ("UNNAMED", "");
    var file = Path.GetFileName(raw).ToUpperInvariant();
    var dot = file.LastIndexOf('.');
    string baseName, ext;
    if (dot < 0) { baseName = file; ext = ""; }
    else { baseName = file[..dot]; ext = file[(dot + 1)..]; }

    baseName = SanitizeAtascii(baseName);
    ext = SanitizeAtascii(ext);

    // Truncate keeping TAIL to match project-wide convention.
    if (baseName.Length > 8) baseName = baseName[^8..];
    if (ext.Length > 3) ext = ext[..3];
    if (baseName.Length == 0) baseName = "UNNAMED";
    return (baseName, ext);
  }

  private static string SanitizeAtascii(string s) {
    // Atari filenames: uppercase A-Z, 0-9, and must not start with digit.
    var chars = new char[s.Length];
    for (var i = 0; i < s.Length; i++) {
      var c = s[i];
      if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) chars[i] = c;
      else chars[i] = '_';
    }
    var clean = new string(chars).TrimStart('_');
    if (clean.Length > 0 && char.IsDigit(clean[0])) clean = "F" + clean;
    return clean;
  }

  private static void WriteVtoc(byte[] image, bool[] used) {
    // VTOC is at sector 360 (128 bytes). Layout:
    //  byte 0:    DOS type (0x02 for DOS 2.0S)
    //  byte 1-2:  total sectors (707 for DOS 2.0S SS/SD)
    //  byte 3-4:  free sectors
    //  byte 5-9:  reserved
    //  byte 10-99 (0x0A-0x63): 90-byte sector allocation bitmap. Bit is SET when sector is FREE.
    //                          Bit for sector N lives at byte (N/8) + 10, mask = 0x80 >> (N%8).
    var vtocOff = SectorDataOffset(VtocSector);
    image[vtocOff + 0] = 0x02;
    // AtariDOS 2 reports (TotalSectors - 13) = 707 as total "usable" sectors. We report full 707.
    const int reported = TotalSectors - 13;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(vtocOff + 1), reported);

    var free = 0;
    for (var s = 1; s <= TotalSectors; s++) {
      if (!used[s]) {
        free++;
        var byteIdx = s / 8;
        var bitMask = (byte)(0x80 >> (s % 8));
        image[vtocOff + 10 + byteIdx] |= bitMask;
      }
    }
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(vtocOff + 3), (ushort)free);
  }
}
