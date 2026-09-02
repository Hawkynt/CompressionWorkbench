#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.Ti99;

/// <summary>
/// Reads Texas Instruments TI-99/4A disk images (DSR — Disk Subsystem
/// Resource). Two on-disk wrappers are supported:
/// <list type="number">
///   <item><description><b>Sector dump (.dsk)</b> — Volume Information Block (VIB)
///   at sector 0, File Descriptor Index Record (FDIR) at sector 1, then
///   one File Descriptor Record per file in their FDIR-listed sectors.</description></item>
///   <item><description><b>TIFiles wrapper (.tifd / .tifiles)</b> — 0x80 header
///   bytes followed by the raw file data. Magic = 0x07 + "TIFILES".</description></item>
/// </list>
/// <para>
/// VIB layout (sector 0, big-endian; 256 bytes):
///   0x00 char[10] disk name (padded with spaces)
///   0x0A u16  total sectors
///   0x0C byte sectors per track (typically 9 SSSD, 16/18 DSDD)
///   0x0D char[3] "DSK"
///   0x10 byte protection flag
///   0x11 byte tracks per side
///   0x12 byte sides (1 or 2)
///   0x13 byte density (1=SD, 2=DD)
///   0x38..0xFF bitmap of allocated sectors
/// </para>
/// <para>
/// FDIR layout (sector 1, big-endian; 256 bytes): array of 128 big-endian
/// u16 sector pointers to File Descriptor Records (0 = unused slot).
/// </para>
/// <para>
/// File Descriptor Record (256 bytes; pointed at by FDIR entry):
///   0x00 char[10] filename (padded with spaces)
///   0x0C byte  file-status flag (0x80=variable, 0x40=emulate, 0x20=modified, 0x10=write-protected, 0x02=internal, 0x01=program)
///   0x0D byte  records per sector
///   0x0E u16   total sectors used by file
///   0x10 byte  end-of-file byte offset
///   0x11 byte  logical record length
///   0x12 u16   #records (variable files) or records/sector (fixed)
///   0x1C..0xFF cluster chain (3-byte entries: start-sector + offset-byte)
/// </para>
/// </summary>
public sealed class Ti99Reader : IDisposable {
    /// <summary>
  /// Defines the sector size constant value.
  /// </summary>
public const int SectorSize = 256;
    /// <summary>
  /// Defines the tifiles header size constant value.
  /// </summary>
public const int TifilesHeaderSize = 128;

  private readonly byte[] _data;
  private readonly List<Ti99Entry> _entries = [];

    /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<Ti99Entry> Entries => _entries;
    /// <summary>
  /// Gets a value indicating whether valid volume.
  /// </summary>
public bool ValidVolume { get; private set; }
    /// <summary>
  /// Gets a value indicating whether is tifiles wrapper.
  /// </summary>
public bool IsTifilesWrapper { get; private set; }
    /// <summary>
  /// Gets or sets the volume name.
  /// </summary>
public string VolumeName { get; private set; } = "";
    /// <summary>
  /// Gets or sets the total sectors.
  /// </summary>
public int TotalSectors { get; private set; }
    /// <summary>
  /// Gets or sets the sectors per track.
  /// </summary>
public int SectorsPerTrack { get; private set; }
    /// <summary>
  /// Gets or sets the tracks.
  /// </summary>
public int Tracks { get; private set; }
    /// <summary>
  /// Gets or sets the sides.
  /// </summary>
public int Sides { get; private set; }
    /// <summary>
  /// Gets or sets the density.
  /// </summary>
public int Density { get; private set; }

    /// <summary>
  /// Initializes a new instance of <see cref="Ti99Reader"/>.
  /// </summary>
public Ti99Reader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private static readonly byte[] TifilesMagic = [0x07, 0x54, 0x49, 0x46, 0x49, 0x4C, 0x45, 0x53];

  private void Parse() {
    // TIFiles wrapper: 0x07 'T' 'I' 'F' 'I' 'L' 'E' 'S' at offset 0.
    if (_data.Length >= TifilesMagic.Length &&
        _data.AsSpan(0, TifilesMagic.Length).SequenceEqual(TifilesMagic)) {
      ParseTifiles();
      return;
    }

    // Sector dump: VIB at offset 0, "DSK" at offset 0x0D.
    if (_data.Length < SectorSize * 2) return;
    var vib = _data.AsSpan(0, SectorSize);
    if (vib[0x0D] != (byte)'D' || vib[0x0E] != (byte)'S' || vib[0x0F] != (byte)'K') return;

    this.VolumeName = ReadAscii(vib[..10]).TrimEnd();
    this.TotalSectors = BinaryPrimitives.ReadUInt16BigEndian(vib.Slice(0x0A, 2));
    this.SectorsPerTrack = vib[0x0C];
    this.Tracks = vib[0x11];
    this.Sides = vib[0x12];
    this.Density = vib[0x13];

    if (this.TotalSectors < 2 || this.SectorsPerTrack is < 8 or > 36) return;
    this.ValidVolume = true;

    // FDIR at sector 1: array of 128 BE u16 pointers.
    var fdir = _data.AsSpan(SectorSize, SectorSize);
    for (var i = 0; i < 128; i++) {
      var fdrSector = BinaryPrimitives.ReadUInt16BigEndian(fdir.Slice(i * 2, 2));
      if (fdrSector == 0) continue;
      var fdrOffset = fdrSector * SectorSize;
      if (fdrOffset + SectorSize > _data.Length) continue;
      ParseFdr(_data.AsSpan(fdrOffset, SectorSize));
    }
  }

  private void ParseTifiles() {
    if (_data.Length < TifilesHeaderSize) return;
    var hdr = _data.AsSpan(0, TifilesHeaderSize);
    var fileSize = BinaryPrimitives.ReadUInt16BigEndian(hdr.Slice(8, 2)) * SectorSize;
    var flags = hdr[10];
    var recordsPerSector = hdr[11];
    var nameBytes = hdr.Slice(16, 10);
    var name = ReadAscii(nameBytes).TrimEnd();
    if (string.IsNullOrEmpty(name)) name = "file";
    this.IsTifilesWrapper = true;
    this.ValidVolume = true;
    _entries.Add(new Ti99Entry {
      Name = name,
      Size = Math.Max(0, Math.Min(fileSize, _data.Length - TifilesHeaderSize)),
      IsDirectory = false,
      FirstSector = TifilesHeaderSize / SectorSize, // not strictly a sector — TIFiles is flat.
      SectorCount = fileSize / SectorSize,
      FileFlags = flags,
      RecordsPerSector = recordsPerSector,
    });
  }

  private void ParseFdr(ReadOnlySpan<byte> fdr) {
    var name = ReadAscii(fdr[..10]).TrimEnd();
    if (string.IsNullOrEmpty(name)) return;
    var flags = fdr[0x0C];
    var rps = fdr[0x0D];
    var totalSectors = BinaryPrimitives.ReadUInt16BigEndian(fdr.Slice(0x0E, 2));
    var eofByte = fdr[0x10];
    // Walk cluster chain at offset 0x1C: 3-byte entries (24-bit packed
    // start-sector + offset-in-file). For simplicity we collect the first
    // contiguous run only.
    var firstStart = 0;
    if (totalSectors > 0) {
      // 24-bit little-endian "start sector" packed across 3 bytes per cluster entry:
      //   byte0 = start_sector & 0xFF
      //   byte1 = ((start_sector >> 8) & 0x0F) | ((offset & 0x0F) << 4)
      //   byte2 = offset >> 4
      var b0 = fdr[0x1C];
      var b1 = fdr[0x1D];
      firstStart = b0 | ((b1 & 0x0F) << 8);
    }
    var sizeBytes = totalSectors * SectorSize;
    if (eofByte != 0 && totalSectors > 0) sizeBytes -= SectorSize - eofByte;

    _entries.Add(new Ti99Entry {
      Name = name,
      Size = sizeBytes,
      IsDirectory = false,
      FirstSector = firstStart,
      SectorCount = totalSectors,
      FileFlags = flags,
      RecordsPerSector = rps,
    });
  }

  private static string ReadAscii(ReadOnlySpan<byte> span) {
    Span<char> chars = stackalloc char[span.Length];
    for (var i = 0; i < span.Length; i++) {
      var c = span[i];
      chars[i] = c is >= 0x20 and < 0x7F ? (char)c : ' ';
    }
    return new string(chars);
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(Ti99Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (this.IsTifilesWrapper) {
      // Skip the 128-byte TIFiles header.
      var len = (int)Math.Min(entry.Size, Math.Max(0, _data.Length - TifilesHeaderSize));
      return len <= 0 ? [] : _data.AsSpan(TifilesHeaderSize, len).ToArray();
    }
    // Sector-dump: copy from FirstSector.
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
    b.Append("format=").Append(this.IsTifilesWrapper ? "TIFiles" : "TI-99/4A DSR sector image").Append('\n');
    b.Append(CultureInfo.InvariantCulture, $"volume_name={this.VolumeName}\n");
    b.Append(CultureInfo.InvariantCulture, $"total_sectors={this.TotalSectors}\n");
    b.Append(CultureInfo.InvariantCulture, $"sectors_per_track={this.SectorsPerTrack}\n");
    b.Append(CultureInfo.InvariantCulture, $"tracks={this.Tracks}\n");
    b.Append(CultureInfo.InvariantCulture, $"sides={this.Sides}\n");
    b.Append(CultureInfo.InvariantCulture, $"density={this.Density}\n");
    b.Append(CultureInfo.InvariantCulture, $"file_count={this.Entries.Count}\n");
    return Encoding.UTF8.GetBytes(b.ToString());
  }

    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }
}
