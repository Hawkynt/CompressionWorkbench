#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Lif;

/// <summary>
/// Walks an HP LIF (Logical Interchange Format) volume and yields the actual
/// on-disk byte layout — the volume label sector + the directory sectors as
/// <see cref="DefragBlockKind.MetadataReserved"/>, every per-file contiguous
/// 256-byte sector run as a <see cref="DefragBlockKind.Used"/> extent, and
/// unused sectors as <see cref="DefragBlockKind.Free"/>. Files in LIF are
/// always stored contiguously, so each file produces exactly one Used run.
/// </summary>
public static class LifExtentMap {

  private const int SectorSize = 256;
  private const ushort LifMagic = 0x8000;

  /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    if (data.Length < SectorSize) yield break;

    // Validate LIF magic at sector 0 (big-endian 0x8000).
    var magic = BinaryPrimitives.ReadUInt16BigEndian(data);
    if (magic != LifMagic) yield break;

    var dirStart = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
    var dirSectors = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
    if (dirStart < 1 || dirSectors < 1) yield break;

    var totalSectors = data.Length / SectorSize;
    if (totalSectors <= 0) yield break;

    // Volume label / system area: sectors [0 .. dirStart).
    yield return new DefragBlockInfo(0, (long)dirStart * SectorSize,
      DefragBlockKind.MetadataReserved, FileName: "LIF volume header");

    // Directory: sectors [dirStart .. dirStart+dirSectors).
    yield return new DefragBlockInfo((long)dirStart * SectorSize,
      (long)dirSectors * SectorSize, DefragBlockKind.MetadataReserved,
      FileName: "LIF directory");

    var owned = new bool[totalSectors];
    for (var s = 0; s < dirStart + dirSectors && s < totalSectors; s++) owned[s] = true;

    // Walk the directory entries.
    var dirByteOff = (long)dirStart * SectorSize;
    var entriesPerSector = SectorSize / 32;
    var totalEntries = dirSectors * entriesPerSector;

    for (var i = 0; i < totalEntries; i++) {
      var off = dirByteOff + i * 32;
      if (off + 32 > data.Length) break;
      var entry = data.AsSpan((int)off, 32);

      var first = entry[0];
      if (first == 0xFF) break;             // physical EOD
      if (first == 0x00 || first == ' ') continue; // empty/deleted

      var name = Encoding.ASCII.GetString(entry[..10]).TrimEnd(' ', '\0');
      if (string.IsNullOrEmpty(name)) continue;

      var startSec = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
      var lenSec = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[16..]);
      if (startSec <= 0 || lenSec <= 0) continue;

      var fileOff = (long)startSec * SectorSize;
      var fileLen = (long)lenSec * SectorSize;
      if (fileOff >= data.Length) continue;
      if (fileOff + fileLen > data.Length) fileLen = data.Length - fileOff;
      if (fileLen <= 0) continue;

      yield return new DefragBlockInfo(fileOff, fileLen, DefragBlockKind.Used, name);
      for (var s = startSec; s < startSec + lenSec && s < totalSectors; s++)
        owned[s] = true;
    }

    // Emit Free runs for unowned sectors.
    var freeStart = -1;
    for (var s = 0; s < totalSectors; s++) {
      if (!owned[s]) {
        if (freeStart < 0) freeStart = s;
      } else if (freeStart >= 0) {
        yield return new DefragBlockInfo((long)freeStart * SectorSize,
          (long)(s - freeStart) * SectorSize, DefragBlockKind.Free);
        freeStart = -1;
      }
    }
    if (freeStart >= 0) {
      yield return new DefragBlockInfo((long)freeStart * SectorSize,
        (long)(totalSectors - freeStart) * SectorSize, DefragBlockKind.Free);
    }
  }
}
