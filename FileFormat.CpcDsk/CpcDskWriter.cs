#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.CpcDsk;

/// <summary>
/// Creates Standard CPC DSK disk images.
/// Files are stored sequentially across sectors starting from track 1;
/// track 0 holds a minimal CP/M-style directory.
/// </summary>
public sealed class CpcDskWriter : IDisposable {
  // Standard DSK geometry constants
  private const int DiskInfoSize  = 256;
  private const int TrackInfoSize = 256;

  // CP/M directory entry size (32 bytes per dirent)
  private const int DirEntrySize = 32;
  // Number of directory entries that fit in one sector (512 / 32 = 16)
  private const int DirEntriesPerSector = 16;

  private readonly Stream _stream;
  private readonly bool   _leaveOpen;
  private readonly int    _tracks;
  private readonly int    _sides;
  private readonly int    _sectorsPerTrack;
  private readonly int    _sectorSize;
  private readonly List<(string Name, byte[] Data)> _files = [];
  private bool _finished;
  private bool _disposed;

  /// <param name="stream">Target stream to write the DSK image into.</param>
  /// <param name="leaveOpen">When <c>true</c> the stream is not disposed by this writer.</param>
  /// <param name="tracks">Number of tracks (default 40).</param>
  /// <param name="sides">Number of sides (default 1).</param>
  /// <param name="sectorsPerTrack">Sectors per track (default 9).</param>
  /// <param name="sectorSize">Bytes per sector (default 512, size code 2).</param>
  public CpcDskWriter(Stream stream, bool leaveOpen = false,
      int tracks = 40, int sides = 1, int sectorsPerTrack = 9, int sectorSize = 512) {
    ArgumentNullException.ThrowIfNull(stream);
    _stream          = stream;
    _leaveOpen       = leaveOpen;
    _tracks          = tracks;
    _sides           = sides;
    _sectorsPerTrack = sectorsPerTrack;
    _sectorSize      = sectorSize;
  }

  /// <summary>Queues a file to be written to the disk image.</summary>
  public void AddFile(string name, byte[] data) {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (_finished) throw new InvalidOperationException("CpcDskWriter: already finished.");
    _files.Add((name, data));
  }

  /// <summary>
  /// Writes the complete Standard CPC DSK image to the stream.
  /// May only be called once.
  /// </summary>
  public void Finish() {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (_finished) throw new InvalidOperationException("CpcDskWriter: already finished.");
    _finished = true;

    // Size code: 128 << code = sectorSize  =>  code = log2(sectorSize/128)
    var sizeCode = SizeCode(_sectorSize);
    // Uniform track size = TrackInfoSize + sectors * sectorSize
    var trackDataSize = _sectorsPerTrack * _sectorSize;
    var trackBlockSize = TrackInfoSize + trackDataSize;

    // Total tracks in image = tracks * sides
    var totalTrackSlots = _tracks * _sides;

    // ── Disk Info Block (256 bytes) ──────────────────────────────────────
    var diskInfo = new byte[DiskInfoSize];
    // Magic: "MV - CPCEMU Disk-File\r\nDisk-Info\r\n" (34 bytes)
    var magicStr = "MV - CPCEMU Disk-File\r\nDisk-Info\r\n";
    Encoding.ASCII.GetBytes(magicStr).CopyTo(diskInfo, 0);
    // Creator (14 bytes, offset 34)
    Encoding.ASCII.GetBytes("CW-Writer     ").CopyTo(diskInfo, 34);
    diskInfo[48] = (byte)_tracks;
    diskInfo[49] = (byte)_sides;
    // Track size (LE uint16, offset 50)
    BinaryPrimitives.WriteUInt16LittleEndian(diskInfo.AsSpan(50), (ushort)trackBlockSize);

    _stream.Write(diskInfo);

    // ── Prepare sector data for every track ─────────────────────────────
    // Track 0 side 0: CP/M directory sectors
    // Remaining tracks: file data sectors
    //
    // Build an allocation table: sectors[track][side][sector] = byte[sectorSize]
    var sectors = new byte[_tracks][][];
    for (var t = 0; t < _tracks; t++) {
      sectors[t] = new byte[_sides][];
      for (var s = 0; s < _sides; s++)
        sectors[t][s] = new byte[_sectorsPerTrack * _sectorSize]; // zero = empty
    }

    // ── Lay out file data starting from track 1, side 0, sector 0 ───────
    // Each file occupies as many consecutive sectors as needed.
    // We track (currentTrack, currentSide, currentSector) as the write head.
    var wTrack  = 1;
    var wSide   = 0;
    var wSector = 0;

    // Also build a simple CP/M-style directory in track 0
    // Max directory entries that fit in the directory area (all sectors of track 0):
    //   _sectorsPerTrack * _sectorSize / 32
    var maxDirEntries = _sectorsPerTrack * _sectorSize / DirEntrySize;
    var dirData = new byte[_sectorsPerTrack * _sectorSize];
    // Initialise directory to 0xE5 (CP/M "unused" marker)
    Array.Fill(dirData, (byte)0xE5);
    var dirEntryIndex = 0;

    foreach (var (name, data) in _files) {
      // Write directory entry (32-byte CP/M dirent, extent 0)
      if (dirEntryIndex < maxDirEntries) {
        var de = dirEntryIndex * DirEntrySize;
        dirData[de] = 0x00; // user number
        // 8.3 name: split on last dot
        var dotPos = name.LastIndexOf('.');
        var basePart = dotPos >= 0 ? name[..dotPos] : name;
        var extPart  = dotPos >= 0 ? name[(dotPos + 1)..] : "";
        // Pad / truncate to 8+3
        var shortBase = basePart.ToUpperInvariant().PadRight(8)[..Math.Min(8, basePart.Length)].PadRight(8);
        var shortExt  = extPart.ToUpperInvariant().PadRight(3)[..Math.Min(3, extPart.Length)].PadRight(3);
        Encoding.ASCII.GetBytes(shortBase).CopyTo(dirData, de + 1);
        Encoding.ASCII.GetBytes(shortExt).CopyTo(dirData, de + 9);
        // EX (extent number), S1, S2
        dirData[de + 12] = 0; // EX
        dirData[de + 13] = 0; // S1
        dirData[de + 14] = 0; // S2
        // RC (record count within extent) — records are 128 bytes
        dirData[de + 15] = (byte)Math.Min(128, (data.Length + 127) / 128);
        // AL (allocation blocks): first block number starts at 2 to skip system tracks
        // For simplicity encode the track/sector as a flat block number (1 block = 1 sector)
        var startBlock = wTrack * _sides * _sectorsPerTrack + wSide * _sectorsPerTrack + wSector;
        var blocksNeeded = Math.Max(1, (data.Length + _sectorSize - 1) / _sectorSize);
        for (var b = 0; b < Math.Min(16, blocksNeeded); b++)
          dirData[de + 16 + b] = (byte)((startBlock + b) & 0xFF);
        dirEntryIndex++;
      }

      // Write file data into sector area
      var remaining = data.Length;
      var srcOffset = 0;
      while (remaining > 0 && wTrack < _tracks) {
        var chunk = Math.Min(remaining, _sectorSize);
        var sectorBase = wSector * _sectorSize;
        data.AsSpan(srcOffset, chunk).CopyTo(sectors[wTrack][wSide].AsSpan(sectorBase));
        srcOffset += chunk;
        remaining -= chunk;
        AdvanceSector(ref wTrack, ref wSide, ref wSector);
      }
    }

    // Copy directory data into track 0 sectors
    dirData.CopyTo(sectors[0][0], 0);

    // ── Write each track block ───────────────────────────────────────────
    for (var t = 0; t < _tracks; t++) {
      for (var s = 0; s < _sides; s++) {
        // Track Info Block (256 bytes)
        var tib = new byte[TrackInfoSize];
        // "Track-Info\r\n\0" (13 bytes)
        Encoding.ASCII.GetBytes("Track-Info\r\n").CopyTo(tib, 0);
        tib[12] = 0x00; // null terminator per spec
        // 3 unused bytes at 13-15
        tib[16] = (byte)t; // track number
        tib[17] = (byte)s; // side number
        // 2 unused bytes at 18-19
        tib[20] = (byte)sizeCode;          // sector size code
        tib[21] = (byte)_sectorsPerTrack;  // number of sectors
        tib[22] = 0x4E;                    // GAP3 length (standard 3.5" value)
        tib[23] = 0xE5;                    // filler byte

        // Sector info table: 8 bytes per sector, starting at offset 24
        for (var i = 0; i < _sectorsPerTrack; i++) {
          var si = 24 + i * 8;
          tib[si + 0] = (byte)t;           // C (cylinder)
          tib[si + 1] = (byte)s;           // H (head)
          tib[si + 2] = (byte)(0xC1 + i);  // R (sector ID, 1-based, IBM convention 0xC1)
          tib[si + 3] = (byte)sizeCode;    // N (size code)
          tib[si + 4] = 0;                 // ST1
          tib[si + 5] = 0;                 // ST2
          // bytes 6-7: unused in standard format (0)
        }

        _stream.Write(tib);
        _stream.Write(sectors[t][s]);
      }
    }
  }

  private void AdvanceSector(ref int track, ref int side, ref int sector) {
    sector++;
    if (sector >= _sectorsPerTrack) {
      sector = 0;
      side++;
      if (side >= _sides) {
        side = 0;
        track++;
      }
    }
  }

  private static int SizeCode(int sectorSize) {
    var code = 0;
    var sz = 128;
    while (sz < sectorSize && code < 7) { sz <<= 1; code++; }
    return code;
  }

  /// <inheritdoc/>
  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    if (!_leaveOpen) _stream.Dispose();
  }
}
