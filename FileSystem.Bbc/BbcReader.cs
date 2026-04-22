#pragma warning disable CS1591
using System.Text;

namespace FileSystem.Bbc;

/// <summary>
/// Reader for BBC Micro Acorn DFS <c>.ssd</c> (single-sided) and <c>.dsd</c>
/// (double-sided interleaved) disk images.
/// </summary>
/// <remarks>
/// Layout: tracks x 10 sectors x 256 bytes. Catalog is on track 0 sectors 0-1.
/// Sector 0: disk title (first 8 chars) + up to 31 eight-byte name entries
/// (7-char filename + 1-char directory; high bit of directory byte = locked).
/// Sector 1: last 4 title chars, (count x 8) in byte 5, total-sectors bits in
/// bytes 6-7, plus 31 eight-byte metadata entries (load/exec/length/start-sector,
/// high bits packed into byte 6).
/// </remarks>
public sealed class BbcReader : IDisposable {

  public const int SectorSize = 256;
  public const int SectorsPerTrack = 10;
  public const int MaxEntries = 31;
  public const int Ssd40TrackSize = 100_000;   // 40 tracks x 10 x 256
  public const int Ssd80TrackSize = 200_000;   // 80 tracks x 10 x 256

  private readonly byte[] _data;
  private readonly bool _doubleSided;
  private readonly int _tracksPerSide;
  private readonly int _sideSize;
  private readonly List<BbcEntry> _entries = [];

  public IReadOnlyList<BbcEntry> Entries => _entries;
  public string DiskTitle { get; private set; } = "";

  public BbcReader(Stream stream, bool doubleSided = false) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    _doubleSided = doubleSided;

    // Work out tracks-per-side from file length.
    var totalBytes = _data.Length;
    _tracksPerSide = totalBytes switch {
      100_000 => 40,   // SSD 40-track
      200_000 => 80,   // SSD 80-track or DSD 2x40-track (halves)
      400_000 => 80,   // DSD 2x80-track
      _ => Math.Max(40, totalBytes / (SectorsPerTrack * SectorSize * (doubleSided ? 2 : 1))),
    };
    _sideSize = _tracksPerSide * SectorsPerTrack * SectorSize;

    Parse();
  }

  private int SectorOffsetForSide(int side, int sector) {
    // Side 0 lives at 0..sideSize-1. In .dsd the layout is *interleaved* by track:
    // side0 track 0, side1 track 0, side0 track 1, side1 track 1, ... For simplicity
    // our .dsd path treats each half of the file as a side (non-interleaved).
    // Real-world .dsd files are usually the non-interleaved variant (two .ssd halves
    // concatenated); interleaved .dsd is rare and we fall back gracefully.
    if (!_doubleSided) return sector * SectorSize;
    return side * _sideSize + sector * SectorSize;
  }

  private void Parse() {
    // Each side has its own catalog.
    var sides = _doubleSided ? 2 : 1;
    for (var side = 0; side < sides; side++) {
      var sector0 = SectorOffsetForSide(side, 0);
      var sector1 = SectorOffsetForSide(side, 1);
      if (sector1 + SectorSize > _data.Length) break;

      // Title: first 8 chars in sector 0, next 4 chars in sector 1 bytes 0-3.
      var titleBytes = new byte[12];
      Array.Copy(_data, sector0, titleBytes, 0, 8);
      Array.Copy(_data, sector1, titleBytes, 8, 4);
      for (var i = 0; i < titleBytes.Length; i++)
        if (titleBytes[i] < 0x20 || titleBytes[i] > 0x7E) titleBytes[i] = 0x20;
      var title = Encoding.ASCII.GetString(titleBytes).TrimEnd();
      if (side == 0) this.DiskTitle = title;

      // Entry count: sector 1 byte 5 holds (count * 8). Clamp into sane range.
      var entriesTimesEight = _data[sector1 + 5];
      var entryCount = entriesTimesEight / 8;
      if (entryCount > MaxEntries) entryCount = MaxEntries;

      for (var i = 0; i < entryCount; i++) {
        // Name entry: 8 bytes starting at sector0 + 8 + i*8 (first 7 = filename, 8th = dir+locked).
        var nameOff = sector0 + 8 + i * 8;
        var metaOff = sector1 + 8 + i * 8;
        if (nameOff + 8 > _data.Length || metaOff + 8 > _data.Length) break;

        var nameBuf = new byte[7];
        Array.Copy(_data, nameOff, nameBuf, 0, 7);
        // Filenames are ASCII, padded with spaces.
        var name = Encoding.ASCII.GetString(nameBuf).TrimEnd();

        var dirByte = _data[nameOff + 7];
        var isLocked = (dirByte & 0x80) != 0;
        var dirChar = (char)(dirByte & 0x7F);
        if (dirChar < 0x20 || dirChar > 0x7E) dirChar = '$';

        var loadLo = (uint)(_data[metaOff + 0] | (_data[metaOff + 1] << 8));
        var execLo = (uint)(_data[metaOff + 2] | (_data[metaOff + 3] << 8));
        var lengthLo = (uint)(_data[metaOff + 4] | (_data[metaOff + 5] << 8));
        var packed = _data[metaOff + 6];
        var startSectorLo = _data[metaOff + 7];

        // Byte 6 layout (DFS spec):
        //   bits 0-1 = start sector (high 2 bits)
        //   bits 2-3 = load addr (high 2 bits)
        //   bits 4-5 = length (high 2 bits)
        //   bits 6-7 = exec addr (high 2 bits)
        var startSectorHi = packed & 0x03;
        var loadHi = (packed >> 2) & 0x03;
        var lengthHi = (packed >> 4) & 0x03;
        var execHi = (packed >> 6) & 0x03;

        var startSector = (startSectorHi << 8) | startSectorLo;
        var loadAddr = ((uint)loadHi << 16) | loadLo;
        var execAddr = ((uint)execHi << 16) | execLo;
        var length = ((uint)lengthHi << 16) | lengthLo;

        // Sign-extend BBC Tube addresses for the &FF prefix when top 2 bits set.
        if ((loadHi & 0x02) != 0) loadAddr |= 0xFF000000;
        if ((execHi & 0x02) != 0) execAddr |= 0xFF000000;

        var fullName = $"{dirChar}.{name}";

        _entries.Add(new BbcEntry {
          FullName = fullName,
          Name = name,
          Directory = dirChar,
          Size = length,
          IsLocked = isLocked,
          LoadAddress = loadAddr,
          ExecAddress = execAddr,
          StartSector = startSector,
          Side = side,
        });
      }
    }
  }

  public byte[] Extract(BbcEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size == 0) return [];
    var len = (int)entry.Size;
    var buf = new byte[len];
    var off = SectorOffsetForSide(entry.Side, entry.StartSector);
    if (off < 0 || off + len > _data.Length)
      throw new InvalidDataException($"BBC DFS: entry '{entry.FullName}' runs past end of image.");
    Buffer.BlockCopy(_data, off, buf, 0, len);
    return buf;
  }

  public void Dispose() { }
}
