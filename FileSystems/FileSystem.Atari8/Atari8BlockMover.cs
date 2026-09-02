#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Atari8;

/// <summary>
/// In-place Atari 8-bit block mover. Moves sector-aligned extents within
/// an ATR image and patches the chain trailer bytes (file#/next-sector
/// pointers in each sector's last 3 bytes) + VTOC bitmap so the file
/// remains reachable at its new location.
/// </summary>
public sealed class Atari8BlockMover : IFilesystemBlockMover {

  private const int AtrHeaderSize = 16;
  private const int VtocSector = 360;
  private const int DirectoryStartSector = 361;
  private const int DirectorySectorCount = 8;
  private const int EntriesPerDirectorySector = 8;
  private const int DirectoryEntrySize = 16;
  private const int TotalSectors = 720;

  /// <inheritdoc />
  /// <summary>
  /// Performs the move extent operation.
  /// </summary>
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
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();

    var sectorSize = ReadSectorSize(data);
    var sectorCount = (int)((length + sectorSize - 1) / sectorSize);

    // Convert byte offsets to 1-based sector numbers.
    var oldSectors = new List<int>(sectorCount);
    var newSectors = new List<int>(sectorCount);
    for (var i = 0; i < sectorCount; i++) {
      oldSectors.Add(OffsetToSector(sectorSize, oldOffset + (long)i * sectorSize));
      newSectors.Add(OffsetToSector(sectorSize, newOffset + (long)i * sectorSize));
    }

    var remap = new Dictionary<int, int>(sectorCount);
    for (var i = 0; i < sectorCount; i++)
      remap[oldSectors[i]] = newSectors[i];

    // 1. Patch chain trailer next-sector pointers in the moved sectors.
    for (var i = 0; i < sectorCount; i++) {
      var sec = newSectors[i];
      var off = (int)SectorFileOffset(sectorSize, sec);
      var effSize = EffectiveSectorSize(sectorSize, sec);
      if (off + effSize > data.Length) continue;
      var b0 = data[off + effSize - 3];
      var b1 = data[off + effSize - 2];
      var next = ((b0 & 0x03) << 8) | b1;
      if (next != 0 && remap.TryGetValue(next, out var newNext)) {
        data[off + effSize - 3] = (byte)((b0 & 0xFC) | ((newNext >> 8) & 0x03));
        data[off + effSize - 2] = (byte)(newNext & 0xFF);
      }
    }

    // 2. Walk all sectors outside the moved set to find any that chain
    //    into the old range (the predecessor sector in the chain).
    for (var sec = 1; sec <= TotalSectors; sec++) {
      if (newSectors.Contains(sec)) continue;
      var off = (int)SectorFileOffset(sectorSize, sec);
      var effSize = EffectiveSectorSize(sectorSize, sec);
      if (off + effSize > data.Length) continue;
      var b0 = data[off + effSize - 3];
      var b1 = data[off + effSize - 2];
      var next = ((b0 & 0x03) << 8) | b1;
      if (next != 0 && remap.TryGetValue(next, out var newNext)) {
        data[off + effSize - 3] = (byte)((b0 & 0xFC) | ((newNext >> 8) & 0x03));
        data[off + effSize - 2] = (byte)(newNext & 0xFF);
      }
    }

    // 3. Patch directory entry: update start-sector if it points to an old sector.
    PatchDirectoryStartSector(data, sectorSize, fileName, remap);

    // 4. Update VTOC bitmap: free old, allocate new.
    foreach (var s in oldSectors) SetVtocBit(data, sectorSize, s, free: true);
    foreach (var s in newSectors) SetVtocBit(data, sectorSize, s, free: false);
    // Recount free sectors.
    var freeCount = 0;
    for (var s = 1; s <= TotalSectors; s++) {
      var byteIdx = (int)SectorFileOffset(sectorSize, VtocSector) + 10 + s / 8;
      if (byteIdx < data.Length && (data[byteIdx] & (0x80 >> (s % 8))) != 0) freeCount++;
    }
    var vtocOff = (int)SectorFileOffset(sectorSize, VtocSector);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(vtocOff + 3), (ushort)freeCount);

    image.Position = 0;
    image.Write(data, 0, data.Length);
    // Crash barrier: metadata commit durable before return.
    image.Flush();
  }

  // ── Directory patching ────────────────────────────────────────────

  private static void PatchDirectoryStartSector(byte[] data, int sectorSize, string fileName,
      Dictionary<int, int> remap) {
    var (baseName, ext) = SplitName(fileName);
    for (var i = 0; i < DirectorySectorCount; i++) {
      var sec = DirectoryStartSector + i;
      var off = (int)SectorFileOffset(sectorSize, sec);
      var effSize = EffectiveSectorSize(sectorSize, sec);
      if (off + effSize > data.Length) continue;
      for (var j = 0; j < EntriesPerDirectorySector; j++) {
        var eo = off + j * DirectoryEntrySize;
        var flags = data[eo];
        if ((flags & 0x40) == 0) continue; // not in-use
        if ((flags & 0x80) != 0) continue; // deleted
        var n = Encoding.ASCII.GetString(data, eo + 5, 8).TrimEnd();
        var x = Encoding.ASCII.GetString(data, eo + 13, 3).TrimEnd();
        if ((n != baseName || x != ext)
            && !string.Equals(fileName, "*", StringComparison.Ordinal)) continue;
        var startSec = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(eo + 3));
        if (remap.TryGetValue(startSec, out var newStart))
          BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(eo + 3), (ushort)newStart);
      }
    }
  }

  // ── VTOC bitmap helpers ───────────────────────────────────────────

  private static void SetVtocBit(byte[] data, int sectorSize, int sector, bool free) {
    if (sector < 1 || sector > TotalSectors) return;
    var vtocOff = (int)SectorFileOffset(sectorSize, VtocSector);
    var byteIdx = vtocOff + 10 + sector / 8;
    var bitMask = (byte)(0x80 >> (sector % 8));
    if (byteIdx >= data.Length) return;
    if (free)
      data[byteIdx] |= bitMask;
    else
      data[byteIdx] &= (byte)~bitMask;
  }

  // ── Sector geometry helpers ───────────────────────────────────────

  private static int ReadSectorSize(byte[] data) {
    if (data.Length < 6 || data[0] != 0x96 || data[1] != 0x02) return 128;
    var raw = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4, 2));
    return raw == 0 ? 128 : raw;
  }

  private static long SectorFileOffset(int sectorSize, int sector1Based) {
    if (sectorSize == 256 && sector1Based <= 3)
      return AtrHeaderSize + (long)(sector1Based - 1) * 128;
    if (sectorSize == 256)
      return AtrHeaderSize + 3L * 128 + (long)(sector1Based - 4) * 256;
    return AtrHeaderSize + (long)(sector1Based - 1) * 128;
  }

  private static int EffectiveSectorSize(int sectorSize, int sector1Based)
    => sectorSize == 256 && sector1Based <= 3 ? 128 : sectorSize;

  private static int OffsetToSector(int sectorSize, long byteOffset) {
    if (sectorSize == 256) {
      var afterHeader = byteOffset - AtrHeaderSize;
      if (afterHeader < 3 * 128)
        return (int)(afterHeader / 128) + 1;
      return (int)((afterHeader - 3 * 128) / 256) + 4;
    }
    return (int)((byteOffset - AtrHeaderSize) / 128) + 1;
  }

  private static (string BaseName, string Ext) SplitName(string raw) {
    if (string.IsNullOrEmpty(raw)) return ("UNNAMED", "");
    var file = Path.GetFileName(raw).ToUpperInvariant();
    var dot = file.LastIndexOf('.');
    string baseName, ext;
    if (dot < 0) { baseName = file; ext = ""; }
    else { baseName = file[..dot]; ext = file[(dot + 1)..]; }
    if (baseName.Length > 8) baseName = baseName[^8..];
    if (ext.Length > 3) ext = ext[..3];
    return (baseName, ext);
  }
}
