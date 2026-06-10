#pragma warning disable CS1591
using System.Text;

namespace FileSystem.Mfs1;

/// <summary>
/// Builds Acorn MFS-1 (Master File System v1) disk images. MFS-1 inherits
/// the DFS on-disk catalog: 256-byte sectors, a 2-sector catalog at track 0,
/// up to 31 entries, files stored contiguously from sector 2 onwards.
///
/// <para>
/// Catalog layout (from <see cref="Mfs1Reader"/>):
///   sector 0, bytes 0..7   — disk title (first 8 chars)
///   sector 0, bytes 8..255 — up to 31 × 8-byte name entries
///                            (7 ASCII chars + 1 directory char, high bit = locked)
///   sector 1, bytes 0..3   — disk title (last 4 chars)
///   sector 1, byte 5       — entry count × 8 (i.e. byte offset of the next free slot)
///   sector 1, bytes 8..255 — up to 31 × 8-byte metadata entries:
///                            load_lo(2) + exec_lo(2) + length_lo(2) +
///                            packed_high_bits(1) + start_sector_lo(1)
///   packed_high_bits bits: 0-1 start_sector_hi, 2-3 load_hi,
///                          4-5 length_hi, 6-7 exec_hi.
/// </para>
/// <para>
/// Files are stored contiguously from sector 2 onwards in catalog-insertion
/// order; the catalog itself is sorted by descending start-sector per DFS
/// convention (the most-recently-added file appears first). Total image
/// size defaults to 80 tracks × 10 sectors × 256 bytes = 200 KB (a Master
/// 80-track SSD image); pass <c>totalSectors</c> to <see cref="Build"/> to
/// choose a different geometry.
/// </para>
/// </summary>
public sealed class Mfs1Writer {

  public const int SectorSize = Mfs1Reader.SectorSize;
  public const int MaxEntries = Mfs1Reader.MaxEntries;
  public const int DefaultTotalSectors = 800;    // 80 tracks × 10 sectors

  private readonly List<(string Name, char Dir, byte[] Data, bool Locked, uint LoadAddr, uint ExecAddr)> _files = [];

  /// <summary>Adds a file to the volume.</summary>
  public void AddFile(string name, byte[] data, char directory = '$',
                      bool locked = false, uint loadAddress = 0, uint execAddress = 0) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var (cleanName, cleanDir) = SanitizeName(name, directory);
    this._files.Add((cleanName, cleanDir, data, locked, loadAddress, execAddress));
  }

  /// <summary>
  /// Builds the MFS-1 image. The catalog is laid out per the DFS convention:
  /// entries are sorted by descending start-sector so the most recently
  /// added file appears at index 0.
  /// </summary>
  /// <param name="diskTitle">12-char volume title (default "WORM").</param>
  /// <param name="totalSectors">Total sectors at 256 B each (default 800 = 200 KB).</param>
  public byte[] Build(string diskTitle = "WORM", int totalSectors = DefaultTotalSectors) {
    if (this._files.Count > MaxEntries)
      throw new InvalidOperationException(
        $"MFS-1: catalog holds at most {MaxEntries} entries (got {this._files.Count}).");
    if (totalSectors < 2)
      throw new ArgumentOutOfRangeException(nameof(totalSectors),
        "MFS-1: image must hold at least the 2-sector catalog.");

    // Lay out files contiguously from sector 2 onwards in insertion order.
    var laidOut = new List<(string Name, char Dir, byte[] Data, bool Locked, uint LoadAddr, uint ExecAddr, int StartSector)>();
    var nextSector = 2;
    foreach (var f in this._files) {
      var sectorsNeeded = (f.Data.Length + SectorSize - 1) / SectorSize;
      if (sectorsNeeded == 0) sectorsNeeded = 1;  // a zero-byte file still reserves one sector
      laidOut.Add((f.Name, f.Dir, f.Data, f.Locked, f.LoadAddr, f.ExecAddr, nextSector));
      nextSector += sectorsNeeded;
    }

    if (nextSector > totalSectors)
      throw new InvalidOperationException(
        $"MFS-1: combined file size needs {nextSector - 2} data sectors but only {totalSectors - 2} are available.");

    var image = new byte[totalSectors * SectorSize];

    // Title bytes: first 8 in sector 0 (offset 0), last 4 in sector 1 (offset 0).
    var title = (diskTitle ?? "").PadRight(12, ' ');
    if (title.Length > 12) title = title[..12];
    for (var i = 0; i < 8; i++) image[i] = (byte)title[i];
    for (var i = 0; i < 4; i++) image[SectorSize + i] = (byte)title[8 + i];

    // DFS convention: catalog sorted by descending start-sector so the
    // most recently added file (highest sector) comes first.
    var sorted = laidOut.OrderByDescending(e => e.StartSector).ToList();

    // Sector 1, byte 5 — entry count × 8.
    image[SectorSize + 5] = (byte)(sorted.Count * 8);
    // Sector 1, byte 4 — sometimes used as cycle number; left zero.
    // Sector 1, bytes 6-7 — total sector count in DFS (low byte at +7, high nibble in +6 low nibble).
    // Encode total sectors per DFS spec: bottom 8 bits in byte 7, top 2 bits in low nibble of byte 6.
    var totalLo = (byte)(totalSectors & 0xFF);
    var totalHi = (byte)((totalSectors >> 8) & 0x03);
    image[SectorSize + 6] = totalHi;
    image[SectorSize + 7] = totalLo;

    for (var i = 0; i < sorted.Count; i++) {
      var entry = sorted[i];

      // Sector 0 name slot at byte 8 + i*8.
      var nameOff = 8 + i * 8;
      var nameBytes = Encoding.ASCII.GetBytes(entry.Name.PadRight(7, ' '));
      for (var b = 0; b < 7; b++) image[nameOff + b] = nameBytes[b];
      var dirByte = (byte)(entry.Dir & 0x7F);
      if (entry.Locked) dirByte |= 0x80;
      image[nameOff + 7] = dirByte;

      // Sector 1 metadata slot at byte 8 + i*8 (i.e. SectorSize + 8 + i*8).
      var metaOff = SectorSize + 8 + i * 8;
      var loadLo = (ushort)(entry.LoadAddr & 0xFFFF);
      var loadHi = (byte)((entry.LoadAddr >> 16) & 0x03);
      var execLo = (ushort)(entry.ExecAddr & 0xFFFF);
      var execHi = (byte)((entry.ExecAddr >> 16) & 0x03);
      var length = (uint)entry.Data.Length;
      var lengthLo = (ushort)(length & 0xFFFF);
      var lengthHi = (byte)((length >> 16) & 0x03);
      var startSec = entry.StartSector;
      var startLo = (byte)(startSec & 0xFF);
      var startHi = (byte)((startSec >> 8) & 0x03);

      image[metaOff + 0] = (byte)(loadLo & 0xFF);
      image[metaOff + 1] = (byte)(loadLo >> 8);
      image[metaOff + 2] = (byte)(execLo & 0xFF);
      image[metaOff + 3] = (byte)(execLo >> 8);
      image[metaOff + 4] = (byte)(lengthLo & 0xFF);
      image[metaOff + 5] = (byte)(lengthLo >> 8);
      image[metaOff + 6] = (byte)(startHi | (loadHi << 2) | (lengthHi << 4) | (execHi << 6));
      image[metaOff + 7] = startLo;

      // Copy file payload to its contiguous sector run.
      if (entry.Data.Length > 0) {
        Buffer.BlockCopy(entry.Data, 0, image, entry.StartSector * SectorSize, entry.Data.Length);
      }
    }

    // Sector 0 byte 0 / 1 carries the optional boot pattern 0x00 0x80; we
    // emit it only when no title characters live at those positions yet.
    // (TryExtractLabel reads label from offset 2 onwards.) MFS-1 reader's
    // weak magic looks for these bytes, so when the title is blank we add
    // the boot pattern to stay self-detecting.
    if (image[0] == (byte)' ' && image[1] == (byte)' ') {
      image[0] = 0x00;
      image[1] = 0x80;
    }

    return image;
  }

  /// <summary>
  /// MFS-1 file names are 7 ASCII characters from a printable subset
  /// (32..126) padded with spaces. The directory letter defaults to <c>$</c>;
  /// callers may pass a path of the form "X.NAME" to set the directory.
  /// </summary>
  private static (string Name, char Dir) SanitizeName(string raw, char dir) {
    // Strip any path; MFS-1 has no nesting beyond the 1-char directory letter.
    var s = Path.GetFileName(raw).ToUpperInvariant();
    // Allow the caller to embed the directory in the name via "X.NAME".
    var explicitDir = dir;
    if (s.Length >= 2 && s[1] == '.') {
      var c = s[0];
      if (c >= 0x20 && c <= 0x7E && c != '$' && c != '.') {
        explicitDir = c;
        s = s[2..];
      }
    }
    var sb = new StringBuilder(7);
    foreach (var c in s) {
      if (sb.Length >= 7) break;
      if (c is >= (char)0x20 and <= (char)0x7E && c != '/' && c != '\\') sb.Append(c);
    }
    if (sb.Length == 0) sb.Append('F');
    if (explicitDir < 0x20 || explicitDir > 0x7E) explicitDir = '$';
    return (sb.ToString(), explicitDir);
  }
}
