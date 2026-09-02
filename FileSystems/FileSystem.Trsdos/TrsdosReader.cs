#pragma warning disable CS1591
using System.Globalization;
using System.Text;

namespace FileSystem.Trsdos;

/// <summary>
/// Reads TRSDOS / LDOS disk images (Radio Shack TRS-80 Model I/III/4).
/// TRSDOS organises a fixed 35-track / 40-track / 80-track disk into
/// "granules" (groups of sectors) tracked by the Granule Allocation
/// Table (GAT) and an associated Hash Index Table (HIT) at track 17.
/// <para>
/// Per the TRSDOS specification:
///   - Track 17 is the directory track. Sector 0 of track 17 holds the
///     GAT; sector 1 holds the HIT.
///   - GAT byte at offset 0xCD = 0xFE identifies a TRSDOS-formatted disk.
///   - Sectors 2..N of track 17 hold 32-byte directory records. Each
///     record begins with an attribute byte; 0x00 = unused, 0x10 = system,
///     0x40 = invisible, 0x80 = killed.
///   - Filename is 8 ASCII characters (offset 5..12), extension is 3
///     ASCII characters (offset 13..15). End-of-file byte count is at
///     offset 30 (high byte) + offset 27 (low byte); sector count at
///     offset 28..29 (little-endian).
/// </para>
/// <para>
/// Sector size is 256 bytes; sectors-per-track defaults to 10 (DD)
/// but JV3/DMK images may report 18 SD or 36 DD. We assume 256-byte
/// sectors with 18 sectors/track DD geometry (track 17 starts at
/// file offset 17 * 18 * 256 = 78336) which matches the most common
/// Model III/4 disks.
/// </para>
/// </summary>
public sealed class TrsdosReader : IDisposable {
    /// <summary>
  /// Defines the sector size constant value.
  /// </summary>
public const int SectorSize = 256;
    /// <summary>
  /// Defines the directory track constant value.
  /// </summary>
public const int DirectoryTrack = 17;
    /// <summary>
  /// Defines the sectors per track default constant value.
  /// </summary>
public const int SectorsPerTrackDefault = 18;
    /// <summary>
  /// Defines the directory entry size constant value.
  /// </summary>
public const int DirectoryEntrySize = 32;

  private readonly byte[] _data;
  private readonly List<TrsdosEntry> _entries = [];

    /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<TrsdosEntry> Entries => _entries;
    /// <summary>
  /// Gets a value indicating whether valid volume.
  /// </summary>
public bool ValidVolume { get; private set; }
    /// <summary>
  /// Gets or sets the directory track offset.
  /// </summary>
public int DirectoryTrackOffset { get; private set; }
    /// <summary>
  /// Gets or sets the sectors per track.
  /// </summary>
public int SectorsPerTrack { get; private set; }

    /// <summary>
  /// Initializes a new instance of <see cref="TrsdosReader"/>.
  /// </summary>
public TrsdosReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    // Try the canonical Model III/4 DD geometry first.
    foreach (var spt in new[] { 18, 10, 9, 26 }) {
      var trackOffset = DirectoryTrack * spt * SectorSize;
      if (trackOffset + SectorSize * 2 + DirectoryEntrySize > _data.Length) continue;
      // GAT signature: byte at offset 0xCD inside sector 0 of track 17 = 0xFE.
      if (_data[trackOffset + 0xCD] != 0xFE) continue;
      this.DirectoryTrackOffset = trackOffset;
      this.SectorsPerTrack = spt;
      this.ValidVolume = true;
      break;
    }
    if (!this.ValidVolume) return;

    // Walk sectors 2..(spt-1) of the directory track scanning 32-byte records.
    var dirEntriesStart = this.DirectoryTrackOffset + SectorSize * 2;
    var maxDirBytes = (this.SectorsPerTrack - 2) * SectorSize;
    var maxEntries = maxDirBytes / DirectoryEntrySize;
    for (var i = 0; i < maxEntries; i++) {
      var off = dirEntriesStart + i * DirectoryEntrySize;
      if (off + DirectoryEntrySize > _data.Length) break;
      var rec = _data.AsSpan(off, DirectoryEntrySize);
      var attr = rec[0];
      // 0x00 = empty, 0x80 = killed/deleted; skip.
      if (attr == 0x00 || (attr & 0x80) != 0) continue;

      var name = ReadAsciiTrim(rec.Slice(5, 8));
      var ext = ReadAsciiTrim(rec.Slice(13, 3));
      if (string.IsNullOrEmpty(name)) continue;
      var fullName = string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";

      var eofLow = rec[27];
      var sectorCount = rec[28] | (rec[29] << 8);
      var eofHigh = rec[30];
      var sizeBytes = (long)sectorCount * SectorSize;
      if (eofLow != 0 || eofHigh != 0)
        sizeBytes = (eofHigh << 8) | eofLow;

      // First-sector pointer is in the extent map at offset 24..25 (granule pair),
      // converted to sector via granule*5 (Model III TRSDOS uses 5-sector granules).
      var firstGranule = rec[24];
      var firstSector = firstGranule * 5;

      _entries.Add(new TrsdosEntry {
        Name = fullName,
        Size = sizeBytes,
        IsDirectory = false,
        FirstSector = firstSector,
        SectorCount = sectorCount,
        Attributes = attr,
      });
    }
  }

  private static string ReadAsciiTrim(ReadOnlySpan<byte> span) {
    Span<char> chars = stackalloc char[span.Length];
    var len = 0;
    foreach (var b in span) {
      var c = (byte)(b & 0x7F);
      if (c is 0 or 0x20) {
        if (len == 0) continue; // skip leading spaces
        break;
      }
      chars[len++] = (char)c;
    }
    return new string(chars[..len]);
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(TrsdosEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    var offset = entry.FirstSector * SectorSize;
    if (offset < 0 || offset >= _data.Length) return [];
    var size = (int)Math.Min(entry.Size, _data.Length - offset);
    return size <= 0 ? [] : _data.AsSpan(offset, size).ToArray();
  }

    /// <summary>
  /// Performs the build surface metadata operation.
  /// </summary>
public byte[] BuildSurfaceMetadata() {
    var b = new StringBuilder();
    b.Append("parse_status=").Append(this.ValidVolume ? "ok" : "invalid").Append('\n');
    b.Append("format=TRSDOS / LDOS\n");
    b.Append(CultureInfo.InvariantCulture, $"directory_track_offset={this.DirectoryTrackOffset}\n");
    b.Append(CultureInfo.InvariantCulture, $"sectors_per_track={this.SectorsPerTrack}\n");
    b.Append(CultureInfo.InvariantCulture, $"file_count={this.Entries.Count}\n");
    return Encoding.UTF8.GetBytes(b.ToString());
  }

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }
}
