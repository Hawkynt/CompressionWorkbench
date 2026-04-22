#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.CpcDsk;

/// <summary>
/// Reads Standard ("MV - CPC") and Extended ("EXTENDED") CPC DSK disk image files.
/// Exposes every sector as a <see cref="CpcDskEntry"/> for raw sector-level access.
/// </summary>
public sealed class CpcDskReader {
  // Magic string prefixes (first 8 bytes are sufficient for detection)
  internal const string StandardMagic  = "MV - CPC";
  internal const string ExtendedMagic  = "EXTENDED";

  // Block sizes defined by the format spec
  private const int DiskInfoSize  = 256;
  private const int TrackInfoSize = 256;

  private readonly byte[] _data;
  private readonly List<CpcDskEntry> _entries = [];

  /// <summary>All sectors discovered in the disk image.</summary>
  public IReadOnlyList<CpcDskEntry> Entries => _entries;

  /// <summary>True when the image uses the Extended CPC DSK format.</summary>
  public bool IsExtended { get; private set; }

  /// <summary>Number of tracks recorded in the disk info block.</summary>
  public int Tracks { get; private set; }

  /// <summary>Number of sides recorded in the disk info block.</summary>
  public int Sides { get; private set; }

  public CpcDskReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < DiskInfoSize)
      throw new InvalidDataException("CPC DSK: image too small to contain a disk info block.");

    var magic = Encoding.ASCII.GetString(_data, 0, 8);
    if (magic.StartsWith(ExtendedMagic, StringComparison.Ordinal))
      IsExtended = true;
    else if (magic.StartsWith(StandardMagic, StringComparison.Ordinal))
      IsExtended = false;
    else
      throw new InvalidDataException($"CPC DSK: unrecognised magic '{magic}'.");

    Tracks = _data[48];
    Sides  = _data[49];

    if (!IsExtended) {
      // Standard: uniform track size stored as LE uint16 at offset 50
      var trackSize = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(50));
      ParseStandard(trackSize);
    } else {
      // Extended: per-track size table at offset 52 (high byte only; actual size = value * 256)
      // Table has Tracks * Sides entries in track-then-side order
      ParseExtended();
    }
  }

  private void ParseStandard(int trackSize) {
    var trackOffset = DiskInfoSize;
    for (var t = 0; t < Tracks; t++) {
      for (var s = 0; s < Sides; s++) {
        if (trackOffset + TrackInfoSize > _data.Length) return;
        ReadTrack(trackOffset, t, s, isExtended: false);
        trackOffset += trackSize;
      }
    }
  }

  private void ParseExtended() {
    // Track size table: offset 52, one byte per (track, side) pair, value * 256 = track block size
    // 0 means the track is unformatted
    var tableOffset = 52;
    var trackOffset = DiskInfoSize;

    for (var t = 0; t < Tracks; t++) {
      for (var s = 0; s < Sides; s++) {
        var tableIdx = t * Sides + s;
        if (tableOffset + tableIdx >= _data.Length) return;
        var highByte = _data[tableOffset + tableIdx];
        if (highByte == 0) continue; // unformatted track

        var blockSize = highByte * 256;
        if (trackOffset + TrackInfoSize > _data.Length) return;
        ReadTrack(trackOffset, t, s, isExtended: true);
        trackOffset += blockSize;
      }
    }
  }

  private void ReadTrack(int trackOffset, int track, int side, bool isExtended) {
    // Validate "Track-Info\r\n" marker (13 bytes at start of track info block)
    var marker = Encoding.ASCII.GetString(_data, trackOffset, 10);
    if (!marker.StartsWith("Track-Info", StringComparison.Ordinal)) return;

    var trackNum  = _data[trackOffset + 16];
    var sideNum   = _data[trackOffset + 17];
    var sectorSizeCode = _data[trackOffset + 20]; // 128 << code
    var sectorCount    = _data[trackOffset + 21];

    // Sector info table starts at offset 24 within the Track Info Block, 8 bytes per entry
    // Sector data follows the 256-byte Track Info Block
    var sectorDataOffset = (long)(trackOffset + TrackInfoSize);

    for (var i = 0; i < sectorCount; i++) {
      var infoBase = trackOffset + 24 + i * 8;
      if (infoBase + 8 > _data.Length) break;

      var sectorId = _data[infoBase + 2];
      var sectorCode = _data[infoBase + 3]; // per-sector size code

      int sectorSize;
      if (isExtended) {
        // Extended format stores actual data size as LE uint16 at bytes 6-7 of the sector info
        var actualSize = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(infoBase + 6));
        sectorSize = actualSize > 0 ? actualSize : (128 << sectorCode);
      } else {
        sectorSize = 128 << sectorCode;
        if (sectorSize == 0) sectorSize = 128 << sectorSizeCode;
      }

      if (sectorSize <= 0) sectorSize = 512; // safe fallback

      _entries.Add(new CpcDskEntry {
        Name       = $"T{trackNum:D2}S{sideNum}_{sectorId:X2}",
        Track      = trackNum,
        Side       = sideNum,
        SectorId   = sectorId,
        Size       = sectorSize,
        DataOffset = sectorDataOffset,
      });

      sectorDataOffset += sectorSize;
    }
  }

  /// <summary>
  /// Returns the raw sector data for the given entry.
  /// </summary>
  public byte[] Extract(CpcDskEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size == 0) return [];
    if (entry.DataOffset < 0 || entry.DataOffset + entry.Size > _data.Length)
      throw new InvalidDataException($"CPC DSK: sector data out of range for '{entry.Name}'.");
    return _data.AsSpan((int)entry.DataOffset, entry.Size).ToArray();
  }
}
