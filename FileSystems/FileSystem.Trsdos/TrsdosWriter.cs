#pragma warning disable CS1591
using System.Text;

namespace FileSystem.Trsdos;

/// <summary>
/// Builds a fresh TRSDOS / LDOS disk image from scratch (Write-Once,
/// Read-Many). The format places the GAT (Granule Allocation Table) at
/// track 17, sector 0; the HIT (Hash Index Table) at track 17, sector 1;
/// and 32-byte directory records at sectors 2..N of track 17. Tracks
/// outside 17 hold file data, allocated in 5-sector "granules" per the
/// Model III/4 convention.
/// </summary>
/// <remarks>
/// <para>This writer produces the canonical Model III/4 18-sectors/track
/// double-density geometry (256-byte sectors). Track count and density
/// drive the total image size. The directory holds at most
/// <see cref="MaxDirectoryRecords"/> entries.</para>
/// <para>Each directory record carries:
/// <list type="bullet">
/// <item>byte 0: attribute (0x10 = non-system, low byte never zero or 0x80)</item>
/// <item>bytes 1..4: dates / reserved (zero here)</item>
/// <item>bytes 5..12: 8-char filename</item>
/// <item>bytes 13..15: 3-char extension</item>
/// <item>bytes 16..23: extent map / reserved (zero here for first-fit single-extent files)</item>
/// <item>bytes 24..25: granule allocation entries (low 5 bits = granule number)</item>
/// <item>bytes 26: reserved</item>
/// <item>byte 27: EOF byte count (low)</item>
/// <item>bytes 28..29: sector count (LE)</item>
/// <item>byte 30: EOF byte count (high) — actually used by reader as low + high combined</item>
/// <item>byte 31: reserved</item>
/// </list>
/// </para>
/// </remarks>
public sealed class TrsdosWriter {

  private const int SectorSize = TrsdosReader.SectorSize;          // 256
  private const int DirectoryTrack = TrsdosReader.DirectoryTrack;  // 17
  private const int DirectoryEntrySize = TrsdosReader.DirectoryEntrySize; // 32
  private const int GranuleSize = 5; // sectors per granule (Model III/4)

  /// <summary>Maximum directory records the writer allows.
  /// At 18 spt, sectors 2..17 of track 17 hold 16 × 256 / 32 = 128 slots.</summary>
  public const int MaxDirectoryRecords = 128;

  private readonly List<(string Name, byte[] Data)> _files = [];
  private int _tracks = 40;
  private int _sectorsPerTrack = 18;
  private string _diskName = "WORM";
  private string _date = "01/01/26";

  /// <summary>Sets geometry. Default: 40 tracks × 18 spt × 256 B = 184 320 B (Model III/4 DD).</summary>
  public void SetGeometry(int tracks, int sectorsPerTrack) {
    if (tracks <= 0) throw new ArgumentOutOfRangeException(nameof(tracks));
    if (sectorsPerTrack <= 0) throw new ArgumentOutOfRangeException(nameof(sectorsPerTrack));
    this._tracks = tracks;
    this._sectorsPerTrack = sectorsPerTrack;
  }

  /// <summary>Sets the 8-character disk name written to GAT bytes 0xD0..0xD7.</summary>
  public void SetDiskName(string? name) {
    if (!string.IsNullOrWhiteSpace(name)) this._diskName = name;
  }

  /// <summary>Sets the 8-character format date written to GAT bytes 0xD8..0xDF (MM/DD/YY).</summary>
  public void SetDate(string? date) {
    if (!string.IsNullOrWhiteSpace(date)) this._date = date;
  }

  /// <summary>Adds one file. Names are 8.3 ASCII, upper-cased.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, data));
  }

  /// <summary>Total image size = tracks × sectorsPerTrack × 256.</summary>
  public int TotalSize => this._tracks * this._sectorsPerTrack * SectorSize;

  /// <summary>Builds the disk image. Throws if files don't fit or directory overflows.</summary>
  public byte[] Build() {
    if (this._files.Count > MaxDirectoryRecords)
      throw new InvalidOperationException(
        $"TRSDOS: directory holds at most {MaxDirectoryRecords} records; tried {this._files.Count}.");

    var image = new byte[this.TotalSize];

    // GAT at track 17, sector 0.
    var gatOff = this.SectorOffset(DirectoryTrack, 0);
    // Mark the 0xFE signature byte at GAT offset 0xCD.
    image[gatOff + 0xCD] = 0xFE;
    // Disk name + date in the reserved trailing portion.
    WriteFixedAscii(image.AsSpan(gatOff + 0xD0, 8), this._diskName);
    WriteFixedAscii(image.AsSpan(gatOff + 0xD8, 8), this._date);

    // HIT at track 17, sector 1 — all zero by default.
    // (Real TRSDOS uses HIT bytes as filename-hash indices; zero is
    // acceptable for a minimal WORM image since the reader walks records
    // sequentially without consulting the HIT.)

    // Allocate granules outside track 17 starting at track 0 granule 0.
    // Granule g starts at sector g * GranuleSize, counted straight through the
    // volume — so granules do not line up with tracks unless a track's sector
    // count is a multiple of five, and the directory track has to be reserved
    // by the sectors it covers rather than by its track number. Reserving
    // granule 17*granulesPerTrack instead protects the wrong part of the disk
    // and leaves the directory free to be allocated to a file.
    var totalSectors = this._tracks * this._sectorsPerTrack;
    var totalGranules = Math.Min(totalSectors / GranuleSize, byte.MaxValue + 1);
    var used = new bool[totalGranules];
    var directoryFirstSector = DirectoryTrack * this._sectorsPerTrack;
    var directoryLastSector = directoryFirstSector + this._sectorsPerTrack - 1;
    for (var g = 0; g < totalGranules; g++) {
      var firstOfGranule = g * GranuleSize;
      var lastOfGranule = firstOfGranule + GranuleSize - 1;
      if (lastOfGranule >= directoryFirstSector && firstOfGranule <= directoryLastSector)
        used[g] = true;
    }

    var dirSectorBase = this.SectorOffset(DirectoryTrack, 2);
    var maxDirBytes = (this._sectorsPerTrack - 2) * SectorSize;

    var recordIdx = 0;
    foreach (var (rawName, data) in this._files) {
      var (name, ext) = SplitName(rawName);
      var sectorsNeeded = Math.Max(1, (data.Length + SectorSize - 1) / SectorSize);
      var granulesNeeded = Math.Max(1, (sectorsNeeded + GranuleSize - 1) / GranuleSize);
      var startGranule = -1;
      for (var g = 0; g < totalGranules - granulesNeeded + 1; g++) {
        var ok = true;
        for (var k = 0; k < granulesNeeded; k++) {
          if (used[g + k]) { ok = false; break; }
        }
        if (ok) { startGranule = g; break; }
      }
      if (startGranule < 0)
        throw new InvalidOperationException(
          $"TRSDOS: out of space allocating {granulesNeeded} granule(s) for '{rawName}'.");
      for (var k = 0; k < granulesNeeded; k++) used[startGranule + k] = true;
      var firstSector = startGranule * GranuleSize;
      var dataOffset = firstSector * SectorSize;
      Array.Copy(data, 0, image, dataOffset, Math.Min(data.Length, image.Length - dataOffset));

      // Write directory record.
      var recOff = dirSectorBase + recordIdx * DirectoryEntrySize;
      if (recOff + DirectoryEntrySize > dirSectorBase + maxDirBytes)
        throw new InvalidOperationException("TRSDOS: directory area overflow.");
      image[recOff] = 0x10; // attribute = non-system, visible.
      WriteFixedAscii(image.AsSpan(recOff + 5, 8), name);
      WriteFixedAscii(image.AsSpan(recOff + 13, 3), ext);
      image[recOff + 24] = (byte)startGranule; // first granule
      image[recOff + 28] = (byte)(sectorsNeeded & 0xFF);
      image[recOff + 29] = (byte)((sectorsNeeded >> 8) & 0xFF);
      // EOF byte count: store the exact file length in the low/high bytes.
      var eofLo = (byte)(data.Length & 0xFF);
      var eofHi = (byte)((data.Length >> 8) & 0xFF);
      image[recOff + 27] = eofLo;
      image[recOff + 30] = eofHi;
      ++recordIdx;
    }

    return image;
  }

  private int SectorOffset(int track, int sector) =>
    track * this._sectorsPerTrack * SectorSize + sector * SectorSize;

  private static (string Name, string Ext) SplitName(string raw) {
    var safe = raw.Replace('\\', '/');
    var slash = safe.LastIndexOf('/');
    if (slash >= 0) safe = safe[(slash + 1)..];
    safe = safe.ToUpperInvariant();
    var dot = safe.LastIndexOf('.');
    string name;
    string ext;
    if (dot > 0) {
      name = safe[..dot];
      ext = safe[(dot + 1)..];
    } else {
      name = safe;
      ext = "";
    }
    if (name.Length > 8) name = name[..8];
    if (ext.Length > 3) ext = ext[..3];
    return (name, ext);
  }

  private static void WriteFixedAscii(Span<byte> dst, string value) {
    dst.Fill(0x20);
    var n = Math.Min(value.Length, dst.Length);
    for (var i = 0; i < n; i++) {
      var c = value[i];
      dst[i] = c < 0x80 ? (byte)c : (byte)'?';
    }
  }
}
