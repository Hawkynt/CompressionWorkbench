#pragma warning disable CS1591
using System.Text;

namespace FileSystem.Bbc;

/// <summary>
/// Builds a fresh BBC Micro Acorn DFS <c>.ssd</c> single-sided disk image from scratch (WORM).
/// </summary>
/// <remarks>
/// <para>
/// Layout: N tracks x 10 sectors x 256 bytes. Catalog occupies track 0 sectors 0 (names +
/// directory chars) and 1 (load/exec/length/start metadata). Each sector holds up to 31
/// 8-byte entries. File data lives starting at track 0 sector 2.
/// </para>
/// <para>
/// Writer emits a 40-track SSD (100 000 bytes) by default, matching the historical BBC-B
/// DFS floppy. Filenames are padded/truncated to 7 chars ASCII, directory character
/// defaults to <c>'$'</c> (root).
/// </para>
/// </remarks>
public sealed class BbcWriter {

  public const int SectorSize = BbcReader.SectorSize;        // 256
  public const int SectorsPerTrack = BbcReader.SectorsPerTrack;  // 10
  public const int DefaultTracks = 40;
  public const int TotalSectors40 = DefaultTracks * SectorsPerTrack;   // 400
  public const int DiskSize40 = TotalSectors40 * SectorSize;           // 102 400 (some tools call this 100 KB)
  public const int MaxEntries = BbcReader.MaxEntries;        // 31

  private readonly List<(string Name, char Dir, byte[] Data, uint LoadAddr, uint ExecAddr, bool Locked)> _files = [];

  public void AddFile(string name, byte[] data, char directory = '$', uint loadAddr = 0x1900, uint execAddr = 0x1900, bool locked = false)
    => this._files.Add((name, directory, data, loadAddr, execAddr, locked));

  /// <summary>Builds the complete 40-track SSD image (100 000 bytes).</summary>
  public byte[] Build(string diskTitle = "WORMDISK") {
    if (this._files.Count > MaxEntries)
      throw new InvalidOperationException(
        $"BBC DFS: {this._files.Count} files exceeds catalog limit of {MaxEntries}.");

    var totalBytes = this._files.Sum(f => (long)f.Data.Length);
    const int reservedCatalogSectors = 2;
    const long dataCapacity = (long)(TotalSectors40 - reservedCatalogSectors) * SectorSize;
    if (totalBytes > dataCapacity)
      throw new InvalidOperationException(
        $"BBC DFS: combined file size {totalBytes} bytes exceeds data capacity {dataCapacity} bytes.");

    var disk = new byte[DiskSize40];

    // --- Place disk title (12 chars: 8 in sector 0, 4 in sector 1 bytes 0-3) ---
    var title = (diskTitle ?? "").ToUpperInvariant();
    for (var i = 0; i < 12; i++) {
      var c = i < title.Length ? title[i] : ' ';
      if (c < 0x20 || c > 0x7E) c = ' ';
      if (i < 8) disk[0 + i] = (byte)c;
      else disk[SectorSize + (i - 8)] = (byte)c;
    }

    // --- Reserve catalog sectors in our internal allocator state ---
    var nextSector = reservedCatalogSectors;  // first data sector = sector 2

    // --- Write each file's data and record its start sector ---
    var entryMeta = new (string Name, char Dir, bool Locked, uint LoadAddr, uint ExecAddr, uint Length, int StartSector)[this._files.Count];
    for (var i = 0; i < this._files.Count; i++) {
      var (rawName, dir, data, load, exec, locked) = this._files[i];
      var name = SanitizeName(rawName);
      var sectorsNeeded = (data.Length + SectorSize - 1) / SectorSize;
      if (sectorsNeeded == 0) sectorsNeeded = 1;  // reserve one even for empty file, DFS-style
      if (nextSector + sectorsNeeded > TotalSectors40)
        throw new InvalidOperationException("BBC DFS: data section does not fit in 40-track SSD.");

      if (data.Length > 0)
        Buffer.BlockCopy(data, 0, disk, nextSector * SectorSize, data.Length);

      entryMeta[i] = (name, dir, locked, load, exec, (uint)data.Length, nextSector);
      nextSector += sectorsNeeded;
    }

    // --- Catalog sector 0 (byte layout) ---
    // bytes 0-7: disk title first 8 chars (already written)
    // bytes 8..(8 + entries*8 - 1): 7-char filename + 1-char (dir|locked) per entry
    for (var i = 0; i < entryMeta.Length; i++) {
      var nameOff = 8 + i * 8;
      var namePadded = entryMeta[i].Name.PadRight(7).Substring(0, 7);
      for (var j = 0; j < 7; j++) disk[nameOff + j] = (byte)namePadded[j];
      var dirByte = (byte)(entryMeta[i].Dir & 0x7F);
      if (entryMeta[i].Locked) dirByte |= 0x80;
      disk[nameOff + 7] = dirByte;
    }

    // --- Catalog sector 1 (byte layout) ---
    // bytes 0-3: title chars 8..11 (already written)
    // byte 4: BCD cycle number (we start at 0x00 / "new disk")
    // byte 5: (entry count * 8)
    // byte 6: bits 0-1 = total sectors high; bits 4-5 = boot option (0=none); bits 6-7 = reserved
    // byte 7: total sectors low (LSB)
    // bytes 8+: 8-byte metadata per entry
    disk[SectorSize + 4] = 0x00;                         // cycle
    disk[SectorSize + 5] = (byte)(entryMeta.Length * 8);
    disk[SectorSize + 7] = TotalSectors40 & 0xFF;
    disk[SectorSize + 6] = (byte)((TotalSectors40 >> 8) & 0x03);  // boot option = 0 in bits 4-5

    for (var i = 0; i < entryMeta.Length; i++) {
      var m = SectorSize + 8 + i * 8;
      var e = entryMeta[i];
      disk[m + 0] = (byte)(e.LoadAddr & 0xFF);
      disk[m + 1] = (byte)((e.LoadAddr >> 8) & 0xFF);
      disk[m + 2] = (byte)(e.ExecAddr & 0xFF);
      disk[m + 3] = (byte)((e.ExecAddr >> 8) & 0xFF);
      disk[m + 4] = (byte)(e.Length & 0xFF);
      disk[m + 5] = (byte)((e.Length >> 8) & 0xFF);

      var loadHi = (int)((e.LoadAddr >> 16) & 0x03);
      var execHi = (int)((e.ExecAddr >> 16) & 0x03);
      var lengthHi = (int)((e.Length >> 16) & 0x03);
      var startHi = (e.StartSector >> 8) & 0x03;
      var packed = startHi | (loadHi << 2) | (lengthHi << 4) | (execHi << 6);
      disk[m + 6] = (byte)packed;
      disk[m + 7] = (byte)(e.StartSector & 0xFF);
    }

    return disk;
  }

  private static string SanitizeName(string raw) {
    if (string.IsNullOrEmpty(raw)) return "FILE";
    var s = Path.GetFileNameWithoutExtension(raw).ToUpperInvariant();
    // DFS allows printable ASCII other than 0x22, 0x23 (#), 0x2A (*), 0x2E (.), 0x3A (:),
    // 0x3F (?), plus control chars. Replace with '_'.
    var chars = new char[s.Length];
    for (var i = 0; i < s.Length; i++) {
      var c = s[i];
      chars[i] = (c >= 0x21 && c < 0x7F && c != '"' && c != '#' && c != '*' &&
                   c != '.' && c != ':' && c != '?') ? c : '_';
    }
    var clean = new string(chars);
    if (clean.Length == 0) return "FILE";
    // Preserve TAIL to match the project-wide truncation convention.
    if (clean.Length > 7) clean = clean[^7..];
    return clean;
  }
}
