#pragma warning disable CS1591
using System.Text;

namespace FileSystem.Mfs1;

/// <summary>
/// Reads Acorn MFS-1 (Master File System v1) disk images. MFS-1 is the
/// minor evolution of Acorn DFS used on early Acorn / BBC Master systems —
/// the on-disk catalog layout matches DFS verbatim:
/// <list type="bullet">
///   <item><description>256-byte sectors, 10 sectors per track.</description></item>
///   <item><description>Sector 0 — disk title (first 8 chars) + up to 31 eight-byte name entries: 7-char filename + 1-char directory letter (high bit = locked).</description></item>
///   <item><description>Sector 1 — last 4 title chars + entry-count*8 byte at offset 5 + 31 eight-byte metadata entries: load(2), exec(2), length(2), packed-high-bits(1), start-sector-low(1).</description></item>
///   <item><description>Packed-high-bits byte 6: bits 0-1 = start-sector high, bits 2-3 = load addr high, bits 4-5 = length high, bits 6-7 = exec addr high.</description></item>
/// </list>
/// The reader is intentionally forgiving — Acorn images frequently arrive with
/// padding, optional boot sectors, or non-standard sizes. We parse the catalog
/// best-effort; if the count byte or sector range looks invalid we surface no
/// entries (the descriptor then falls back to the opaque FULL/metadata surface).
/// </summary>
public sealed class Mfs1Reader : IDisposable {

  /// <summary>
  /// Defines the sector size constant value.
  /// </summary>
public const int SectorSize = 256;
  /// <summary>
  /// Defines the sectors per track constant value.
  /// </summary>
public const int SectorsPerTrack = 10;
  /// <summary>
  /// Defines the max entries constant value.
  /// </summary>
public const int MaxEntries = 31;

  private readonly byte[] _data;
  private readonly List<Mfs1Entry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<Mfs1Entry> Entries => this._entries;
  /// <summary>
  /// Gets or sets the disk title.
  /// </summary>
public string DiskTitle { get; private set; } = "";
  /// <summary>
  /// Gets a value indicating whether catalog parsed.
  /// </summary>
public bool CatalogParsed { get; private set; }

  /// <summary>
  /// Initializes a new instance of <see cref="Mfs1Reader"/>.
  /// </summary>
public Mfs1Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    this.Parse();
  }

  /// <summary>
  /// Initializes a new instance of <see cref="Mfs1Reader"/>.
  /// </summary>
public Mfs1Reader(byte[] image) {
    ArgumentNullException.ThrowIfNull(image);
    this._data = image;
    this.Parse();
  }

  private void Parse() {
    // Need at least 2 sectors (catalog).
    if (this._data.Length < 2 * SectorSize) return;

    const int sector0 = 0;
    const int sector1 = SectorSize;

    // Disk title: bytes 0..7 of sector 0 + bytes 0..3 of sector 1.
    var titleBytes = new byte[12];
    Array.Copy(this._data, sector0, titleBytes, 0, 8);
    Array.Copy(this._data, sector1, titleBytes, 8, 4);
    for (var i = 0; i < titleBytes.Length; i++)
      if (titleBytes[i] < 0x20 || titleBytes[i] > 0x7E) titleBytes[i] = 0x20;
    this.DiskTitle = Encoding.ASCII.GetString(titleBytes).TrimEnd();

    // Entry count: sector 1 byte 5 holds count * 8.
    var entriesTimesEight = this._data[sector1 + 5];
    var entryCount = entriesTimesEight / 8;
    if (entryCount <= 0 || entryCount > MaxEntries) return;
    if (entriesTimesEight % 8 != 0) return; // count byte malformed

    var totalSectors = this._data.Length / SectorSize;
    var anyValid = false;

    for (var i = 0; i < entryCount; i++) {
      var nameOff = sector0 + 8 + i * 8;
      var metaOff = sector1 + 8 + i * 8;
      if (nameOff + 8 > this._data.Length || metaOff + 8 > this._data.Length) break;

      // Parse 7-char filename. Skip entries with non-printable filename bytes (corrupt or empty slot).
      var nameBuf = new byte[7];
      Array.Copy(this._data, nameOff, nameBuf, 0, 7);
      var validName = true;
      for (var b = 0; b < 7; b++) {
        if (nameBuf[b] == 0) { Array.Clear(nameBuf, b, 7 - b); break; }
        if (nameBuf[b] is < 0x20 or > 0x7E) { validName = false; break; }
      }
      if (!validName) continue;
      var name = Encoding.ASCII.GetString(nameBuf).TrimEnd();
      if (name.Length == 0) continue;

      var dirByte = this._data[nameOff + 7];
      var isLocked = (dirByte & 0x80) != 0;
      var dirChar = (char)(dirByte & 0x7F);
      if (dirChar < 0x20 || dirChar > 0x7E) dirChar = '$';

      var loadLo = (uint)(this._data[metaOff + 0] | (this._data[metaOff + 1] << 8));
      var execLo = (uint)(this._data[metaOff + 2] | (this._data[metaOff + 3] << 8));
      var lengthLo = (uint)(this._data[metaOff + 4] | (this._data[metaOff + 5] << 8));
      var packed = this._data[metaOff + 6];
      var startSectorLo = this._data[metaOff + 7];

      var startSectorHi = packed & 0x03;
      var loadHi = (packed >> 2) & 0x03;
      var lengthHi = (packed >> 4) & 0x03;
      var execHi = (packed >> 6) & 0x03;

      var startSector = (startSectorHi << 8) | startSectorLo;
      var length = ((uint)lengthHi << 16) | lengthLo;
      var loadAddr = ((uint)loadHi << 16) | loadLo;
      var execAddr = ((uint)execHi << 16) | execLo;

      // Sanity: refuse entries whose extent runs off the disk.
      var endSector = startSector + (int)((length + SectorSize - 1) / SectorSize);
      if (startSector < 2 || endSector > totalSectors + 1) continue;

      this._entries.Add(new Mfs1Entry {
        Name = name,
        Directory = dirChar,
        Size = length,
        LoadAddress = loadAddr,
        ExecAddress = execAddr,
        StartSector = startSector,
        IsLocked = isLocked,
      });
      anyValid = true;
    }

    this.CatalogParsed = anyValid;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(Mfs1Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size == 0) return [];
    var len = (int)entry.Size;
    var off = entry.StartSector * SectorSize;
    if (off < 0 || off + len > this._data.Length)
      throw new InvalidDataException($"MFS-1: entry '{entry.FullName}' runs past end of image.");
    var buf = new byte[len];
    Buffer.BlockCopy(this._data, off, buf, 0, len);
    return buf;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }
}
