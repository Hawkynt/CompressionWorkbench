#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.CpcDsk;

/// <summary>
/// Walks an Amstrad CPC DSK disk image (Standard or Extended) and yields the
/// actual on-disk byte layout — the 256-byte Disk Info header + every 256-byte
/// per-track Track Info Block as <see cref="DefragBlockKind.MetadataReserved"/>;
/// the AMSDOS directory area on track 0 side 0 (sectors hosting the directory
/// entries) as <see cref="DefragBlockKind.MetadataReserved"/>; every AMSDOS
/// file's allocated sector list — coalesced to contiguous runs — as
/// <see cref="DefragBlockKind.Used"/>; unallocated sectors as
/// <see cref="DefragBlockKind.Free"/>.
/// </summary>
public static class CpcDskExtentMap {

  private const int DiskInfoSize = 256;
  private const int TrackInfoSize = 256;
  private const int DirEntrySize = 32;
  private const byte DirUnusedMarker = 0xE5;

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    if (data.Length < DiskInfoSize) yield break;

    var magic = Encoding.ASCII.GetString(data, 0, 8);
    bool isExtended;
    if (magic.StartsWith("EXTENDED", StringComparison.Ordinal)) isExtended = true;
    else if (magic.StartsWith("MV - CPC", StringComparison.Ordinal)) isExtended = false;
    else yield break;

    var tracks = data[48];
    var sides = data[49];
    if (tracks == 0 || sides == 0) yield break;

    // Disk Info header.
    yield return new DefragBlockInfo(0, DiskInfoSize,
      DefragBlockKind.MetadataReserved, FileName: "CPC DSK disk info header");

    // Discover per-(track,side) block offsets and sector layout.
    var trackOffsets = new long[tracks * sides];
    int sectorsPerTrack = 0, sectorSize = 0;
    var current = (long)DiskInfoSize;

    if (!isExtended) {
      var trackSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(50));
      for (var t = 0; t < tracks; t++) {
        for (var s = 0; s < sides; s++) {
          trackOffsets[t * sides + s] = current;
          if (sectorsPerTrack == 0)
            (sectorsPerTrack, sectorSize) = ReadTrackGeometry(data, current);
          current += trackSize;
        }
      }
    } else {
      for (var t = 0; t < tracks; t++) {
        for (var s = 0; s < sides; s++) {
          var idx = t * sides + s;
          if (52 + idx >= data.Length) { trackOffsets[idx] = -1; continue; }
          var highByte = data[52 + idx];
          if (highByte == 0) { trackOffsets[idx] = -1; continue; }
          trackOffsets[idx] = current;
          if (sectorsPerTrack == 0)
            (sectorsPerTrack, sectorSize) = ReadTrackGeometry(data, current);
          current += highByte * 256;
        }
      }
    }

    if (sectorsPerTrack == 0 || sectorSize == 0) yield break;

    // Emit each track's TIB (256 B at the start of every formatted track block).
    for (var t = 0; t < tracks; t++) {
      for (var s = 0; s < sides; s++) {
        var off = trackOffsets[t * sides + s];
        if (off < 0 || off + TrackInfoSize > data.Length) continue;
        yield return new DefragBlockInfo(off, TrackInfoSize,
          DefragBlockKind.MetadataReserved, FileName: $"CPC DSK Track-Info T{t:D2}S{s}");
      }
    }

    // AMSDOS directory: track 0 side 0, sectors 0..sectorsPerTrack-1 (the
    // CpcDskWriter's convention reserves all of track 0 side 0 for the dir).
    long GetSectorOffset(int track, int side, int sectorIndexInTrack) {
      if (track < 0 || track >= tracks) return -1;
      if (side < 0 || side >= sides) return -1;
      if (sectorIndexInTrack < 0 || sectorIndexInTrack >= sectorsPerTrack) return -1;
      var trackBase = trackOffsets[track * sides + side];
      if (trackBase < 0) return -1;
      return trackBase + TrackInfoSize + (long)sectorIndexInTrack * sectorSize;
    }

    var owned = new bool[tracks * sides * sectorsPerTrack];
    int LinearSector(int track, int side, int sec) =>
      track * sides * sectorsPerTrack + side * sectorsPerTrack + sec;
    void MarkOwned(int blockNum) {
      var blocksPerTrack = sectorsPerTrack * sides;
      var t = blockNum / blocksPerTrack;
      var withinTrack = blockNum % blocksPerTrack;
      var sd = withinTrack / sectorsPerTrack;
      var se = withinTrack % sectorsPerTrack;
      var lin = LinearSector(t, sd, se);
      if (lin >= 0 && lin < owned.Length) owned[lin] = true;
    }

    // Mark directory area as owned + emit metadata extent for the dir region.
    for (var s = 0; s < sectorsPerTrack; s++) {
      var off = GetSectorOffset(0, 0, s);
      if (off < 0) continue;
      yield return new DefragBlockInfo(off, sectorSize,
        DefragBlockKind.MetadataReserved, FileName: "AMSDOS directory");
      var lin = LinearSector(0, 0, s);
      if (lin < owned.Length) owned[lin] = true;
    }

    // Read directory area into one contiguous buffer.
    var dir = new byte[sectorsPerTrack * sectorSize];
    for (var s = 0; s < sectorsPerTrack; s++) {
      var off = GetSectorOffset(0, 0, s);
      if (off < 0 || off + sectorSize > data.Length) continue;
      Array.Copy(data, off, dir, s * sectorSize, sectorSize);
    }

    // Walk AMSDOS dir → group multi-extent files by (base, ext).
    var groups = new Dictionary<(string b, string x), List<int>>();
    var order = new List<(string b, string x)>();
    var maxEntries = dir.Length / DirEntrySize;
    for (var slot = 0; slot < maxEntries; slot++) {
      var off = slot * DirEntrySize;
      var userNumber = dir[off];
      if (userNumber == DirUnusedMarker) continue;
      if (userNumber > 0x0F) continue;

      var baseStr = StripAttributeBits(Encoding.ASCII.GetString(dir, off + 1, 8)).TrimEnd();
      var extStr = StripAttributeBits(Encoding.ASCII.GetString(dir, off + 9, 3)).TrimEnd();
      if (baseStr.Length == 0 && extStr.Length == 0) continue;

      var key = (baseStr, extStr);
      if (!groups.TryGetValue(key, out var list)) {
        list = [];
        groups[key] = list;
        order.Add(key);
      }
      for (var b = 0; b < 16; b++) {
        var blk = dir[off + 16 + b];
        if (blk == 0) break;
        list.Add(blk);
      }
    }

    var blocksPerTrackSides = sectorsPerTrack * sides;
    var totalBlocks = tracks * blocksPerTrackSides;

    foreach (var key in order) {
      var blocks = groups[key];
      // Sort by block number for run coalescing in physical layout order.
      blocks.Sort();
      var fullName = key.x.Length == 0 ? key.b : key.b + "." + key.x;

      long? runStartOff = null;
      var runByteLen = 0L;
      var prevBlock = -2;
      foreach (var blockNum in blocks) {
        if (blockNum >= totalBlocks) continue;
        MarkOwned(blockNum);
        var t = blockNum / blocksPerTrackSides;
        var withinTrack = blockNum % blocksPerTrackSides;
        var sd = withinTrack / sectorsPerTrack;
        var se = withinTrack % sectorsPerTrack;
        var off = GetSectorOffset(t, sd, se);
        if (off < 0) { prevBlock = blockNum; continue; }

        if (runStartOff == null) {
          runStartOff = off;
          runByteLen = sectorSize;
        } else if (blockNum == prevBlock + 1) {
          runByteLen += sectorSize;
        } else {
          yield return new DefragBlockInfo(runStartOff.Value, runByteLen, DefragBlockKind.Used, fullName);
          runStartOff = off;
          runByteLen = sectorSize;
        }
        prevBlock = blockNum;
      }
      if (runStartOff.HasValue) {
        yield return new DefragBlockInfo(runStartOff.Value, runByteLen, DefragBlockKind.Used, fullName);
      }
    }

    // Free runs — collapse unowned sector ranges in physical order.
    long? freeStartOff = null;
    var freeByteLen = 0L;
    long? prevSecOff = null;
    for (var t = 0; t < tracks; t++) {
      for (var sd = 0; sd < sides; sd++) {
        for (var se = 0; se < sectorsPerTrack; se++) {
          var lin = LinearSector(t, sd, se);
          if (lin >= owned.Length) continue;
          var off = GetSectorOffset(t, sd, se);
          if (off < 0) continue;
          if (!owned[lin]) {
            if (freeStartOff == null) {
              freeStartOff = off;
              freeByteLen = sectorSize;
              prevSecOff = off;
            } else if (off == prevSecOff!.Value + sectorSize) {
              freeByteLen += sectorSize;
              prevSecOff = off;
            } else {
              yield return new DefragBlockInfo(freeStartOff.Value, freeByteLen, DefragBlockKind.Free);
              freeStartOff = off;
              freeByteLen = sectorSize;
              prevSecOff = off;
            }
          } else if (freeStartOff.HasValue) {
            yield return new DefragBlockInfo(freeStartOff.Value, freeByteLen, DefragBlockKind.Free);
            freeStartOff = null;
            freeByteLen = 0;
            prevSecOff = null;
          }
        }
      }
    }
    if (freeStartOff.HasValue) {
      yield return new DefragBlockInfo(freeStartOff.Value, freeByteLen, DefragBlockKind.Free);
    }
  }

  private static (int SectorsPerTrack, int SectorSize) ReadTrackGeometry(byte[] data, long trackBlockOffset) {
    if (trackBlockOffset + TrackInfoSize > data.Length) return (0, 0);
    var marker = Encoding.ASCII.GetString(data, (int)trackBlockOffset, 10);
    if (!marker.StartsWith("Track-Info", StringComparison.Ordinal)) return (0, 0);
    var sizeCode = data[trackBlockOffset + 20];
    var sectorCount = data[trackBlockOffset + 21];
    var sectorSize = 128 << sizeCode;
    if (sectorSize < 128 || sectorSize > 8192) sectorSize = 512;
    if (sectorCount == 0) sectorCount = 9;
    return (sectorCount, sectorSize);
  }

  private static string StripAttributeBits(string raw) {
    var chars = new char[raw.Length];
    for (var i = 0; i < raw.Length; i++) chars[i] = (char)(raw[i] & 0x7F);
    return new string(chars).TrimEnd();
  }
}
