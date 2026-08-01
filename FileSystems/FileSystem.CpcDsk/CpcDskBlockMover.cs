#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.CpcDsk;

/// <summary>
/// In-place CPC DSK block mover. Moves sector-aligned extents within a CPC DSK
/// image and patches the AMSDOS directory entries' block-pointer lists so files
/// remain reachable at their new locations.
///
/// <para>CPC DSK has a two-level structure: the DSK container (disk-info header +
/// per-track blocks with Track Info headers) and the AMSDOS filesystem (CP/M-style
/// directory entries on track 0, file data on tracks 1+). This mover operates at
/// the raw byte level within the DSK container and patches the AMSDOS directory
/// block pointers.</para>
/// </summary>
public sealed class CpcDskBlockMover : IFilesystemBlockMover {

  private const int DirEntrySize = 32;
  private const byte DirUnusedMarker = 0xE5;
  private const int DiskInfoSize = 256;
  private const int TrackInfoSize = 256;

  private int _tracks;
  private int _sides;
  private int _sectorsPerTrack;
  private int _sectorSize;
  private long[] _trackBlockOffsets = [];
  private int _totalBlocks;
  private int _firstDataBlock;

  /// <summary>
  /// Initialises the mover by parsing the DSK header geometry.
  /// Must be called before any move operations.
  /// </summary>
  public void Init(Stream image) {
    var diskInfo = new byte[DiskInfoSize];
    image.Position = 0;
    image.ReadExactly(diskInfo);

    var magic = Encoding.ASCII.GetString(diskInfo, 0, 8);
    bool isExtended;
    if (magic.StartsWith("EXTENDED", StringComparison.Ordinal)) isExtended = true;
    else if (magic.StartsWith("MV - CPC", StringComparison.Ordinal)) isExtended = false;
    else throw new InvalidDataException($"CPC DSK: unrecognised magic '{magic}'.");

    _tracks = diskInfo[48];
    _sides = diskInfo[49];
    _trackBlockOffsets = new long[_tracks * _sides];
    var current = (long)DiskInfoSize;

    if (!isExtended) {
      var trackSize = BinaryPrimitives.ReadUInt16LittleEndian(diskInfo.AsSpan(50));
      for (var t = 0; t < _tracks; t++) {
        for (var s = 0; s < _sides; s++) {
          _trackBlockOffsets[t * _sides + s] = current;
          if (_sectorsPerTrack == 0) ReadTrackGeometry(image, current);
          current += trackSize;
        }
      }
    } else {
      for (var t = 0; t < _tracks; t++) {
        for (var s = 0; s < _sides; s++) {
          var idx = t * _sides + s;
          var highByte = diskInfo[52 + idx];
          if (highByte == 0) { _trackBlockOffsets[idx] = -1; continue; }
          _trackBlockOffsets[idx] = current;
          if (_sectorsPerTrack == 0) ReadTrackGeometry(image, current);
          current += highByte * 256;
        }
      }
    }

    if (_sectorsPerTrack == 0)
      throw new InvalidDataException("CPC DSK: no formatted tracks.");

    _totalBlocks = _tracks * _sides * _sectorsPerTrack;
    _firstDataBlock = _sectorsPerTrack * _sides; // track 0 is directory
  }

  private void ReadTrackGeometry(Stream image, long offset) {
    var tib = new byte[TrackInfoSize];
    image.Position = offset;
    image.ReadExactly(tib);
    var sizeCode = tib[20];
    _sectorsPerTrack = tib[21] == 0 ? 9 : tib[21];
    _sectorSize = 128 << sizeCode;
    if (_sectorSize < 128 || _sectorSize > 8192) _sectorSize = 512;
  }

  /// <summary>Sector size in bytes.</summary>
  public int SectorSize => _sectorSize;

  /// <summary>Total flat block count.</summary>
  public int TotalBlocks => _totalBlocks;

  /// <summary>First data block index (past directory area).</summary>
  public int FirstDataBlock => _firstDataBlock;

  /// <summary>Gets the file offset for a given flat block number.</summary>
  public long BlockToOffset(int blockNum) {
    var blocksPerTrack = _sectorsPerTrack * _sides;
    var track = blockNum / blocksPerTrack;
    var withinTrack = blockNum % blocksPerTrack;
    var side = withinTrack / _sectorsPerTrack;
    var sector = withinTrack % _sectorsPerTrack;
    if (track < 0 || track >= _tracks || side < 0 || side >= _sides) return -1;
    var trackBase = _trackBlockOffsets[track * _sides + side];
    if (trackBase < 0) return -1;
    return trackBase + TrackInfoSize + (long)sector * _sectorSize;
  }

  /// <summary>Converts a file offset back to a flat block number.</summary>
  public int OffsetToBlock(long offset) {
    for (var t = 0; t < _tracks; t++) {
      for (var s = 0; s < _sides; s++) {
        var trackBase = _trackBlockOffsets[t * _sides + s];
        if (trackBase < 0) continue;
        var dataStart = trackBase + TrackInfoSize;
        var dataEnd = dataStart + (long)_sectorsPerTrack * _sectorSize;
        if (offset >= dataStart && offset < dataEnd) {
          var sector = (int)((offset - dataStart) / _sectorSize);
          var blocksPerTrack = _sectorsPerTrack * _sides;
          return t * blocksPerTrack + s * _sectorsPerTrack + sector;
        }
      }
    }
    return -1;
  }

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var oldBlock = OffsetToBlock(oldOffset);
    var newBlock = OffsetToBlock(newOffset);
    if (oldBlock < 0 || newBlock < 0) return;

    var blockCount = (int)((length + _sectorSize - 1) / _sectorSize);
    var mapping = new Dictionary<int, int>(blockCount);
    for (var i = 0; i < blockCount; i++)
      mapping[oldBlock + i] = newBlock + i;

    // Read directory area (track 0, all sectors).
    var dirSize = _sectorsPerTrack * _sectorSize;
    var dir = new byte[dirSize];
    for (var s = 0; s < _sectorsPerTrack; s++) {
      var off = BlockToOffset(s); // track 0, side 0, sector s
      if (off < 0) continue;
      var sectorBuf = new byte[_sectorSize];
      image.Position = off;
      image.ReadExactly(sectorBuf);
      sectorBuf.CopyTo(dir, s * _sectorSize);
    }

    // Split target filename.
    var (targetBase, targetExt) = SplitName(fileName);

    var dirty = false;
    var maxEntries = dir.Length / DirEntrySize;
    for (var slot = 0; slot < maxEntries; slot++) {
      var off = slot * DirEntrySize;
      if (dir[off] == DirUnusedMarker) continue;
      if (dir[off] > 0x0F) continue;

      var entryBase = StripHighBits(Encoding.ASCII.GetString(dir, off + 1, 8)).TrimEnd();
      var entryExt = StripHighBits(Encoding.ASCII.GetString(dir, off + 9, 3)).TrimEnd();

      if (!entryBase.Equals(targetBase, StringComparison.OrdinalIgnoreCase)) continue;
      if (!entryExt.Equals(targetExt, StringComparison.OrdinalIgnoreCase)) continue;

      for (var b = 0; b < 16; b++) {
        var blk = dir[off + 16 + b];
        if (blk == 0) continue;
        if (mapping.TryGetValue(blk, out var newBlk)) {
          dir[off + 16 + b] = (byte)newBlk;
          dirty = true;
        }
      }
    }

    if (dirty) {
      for (var s = 0; s < _sectorsPerTrack; s++) {
        var off = BlockToOffset(s);
        if (off < 0) continue;
        image.Position = off;
        image.Write(dir, s * _sectorSize, _sectorSize);
      }
      // Crash barrier: metadata commit durable before return.
      image.Flush();
    }
  }

  private static (string Base, string Ext) SplitName(string name) {
    var fileName = Path.GetFileName(name);
    var dot = fileName.LastIndexOf('.');
    var basePart = dot >= 0 ? fileName[..dot] : fileName;
    var extPart = dot >= 0 ? fileName[(dot + 1)..] : "";
    if (basePart.Length > 8) basePart = basePart[..8];
    if (extPart.Length > 3) extPart = extPart[..3];
    return (basePart.ToUpperInvariant(), extPart.ToUpperInvariant());
  }

  private static string StripHighBits(string raw) {
    var chars = new char[raw.Length];
    for (var i = 0; i < raw.Length; i++)
      chars[i] = (char)(raw[i] & 0x7F);
    return new string(chars).TrimEnd();
  }
}
