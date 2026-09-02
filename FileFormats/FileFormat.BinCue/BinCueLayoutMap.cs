#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.BinCue;

/// <summary>
/// Walks a BIN/CUE disc image and emits the byte-level layout showing the
/// track/sector structure: system area, volume descriptors, directory records,
/// and file data regions based on the detected sector geometry.
/// </summary>
public static class BinCueLayoutMap {

  private const int Iso9660SectorSize = 2048;

    /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    stream.Position = 0;

    if (stream.Length < 2048)
      yield break;

    // Detect sector geometry (same logic as BinCueReader)
    var (sectorSize, dataOffset) = DetectGeometry(stream);

    // System area: LBAs 0-15 (16 sectors)
    var systemAreaBytes = (long)16 * sectorSize;
    if (systemAreaBytes <= stream.Length) {
      yield return new DefragBlockInfo(0, systemAreaBytes, DefragBlockKind.MetadataReserved,
        FileName: "System Area (LBA 0-15)");
    }

    // Try reading PVD at LBA 16
    var pvdStart = (long)16 * sectorSize;
    var pvdData = ReadSectorData(stream, 16, sectorSize, dataOffset);
    if (pvdData == null)
      yield break;

    // Check for valid PVD
    if (pvdData[0] != 1 || pvdData[1] != (byte)'C' || pvdData[2] != (byte)'D' ||
        pvdData[3] != (byte)'0' || pvdData[4] != (byte)'0' || pvdData[5] != (byte)'1')
      yield break;

    yield return new DefragBlockInfo(pvdStart, sectorSize, DefragBlockKind.MetadataReserved,
      FileName: "Primary Volume Descriptor (LBA 16)");

    // Volume descriptor set terminator at LBA 17
    var vdtStart = (long)17 * sectorSize;
    if (vdtStart + sectorSize <= stream.Length) {
      yield return new DefragBlockInfo(vdtStart, sectorSize, DefragBlockKind.MetadataReserved,
        FileName: "Volume Descriptor Terminator (LBA 17)");
    }

    // Root directory
    var rootLba = ReadUInt32LE(pvdData, 156 + 2);
    var rootSize = ReadUInt32LE(pvdData, 156 + 10);

    if (rootLba > 0 && rootSize > 0) {
      var rootStart = (long)rootLba * sectorSize;
      var rootSectors = (rootSize + Iso9660SectorSize - 1) / Iso9660SectorSize;
      yield return new DefragBlockInfo(rootStart, rootSectors * sectorSize, DefragBlockKind.MetadataReserved,
        FileName: $"Root Directory (LBA {rootLba})");

      // Walk directory to find file extents
      var dirData = ReadSectorData(stream, (int)rootLba, sectorSize, dataOffset);
      if (dirData != null) {
        var pos = 0;
        while (pos + 34 <= dirData.Length && pos < (int)rootSize) {
          var recLen = dirData[pos];
          if (recLen == 0) {
            // Advance to next sector boundary
            var nextSector = ((pos / Iso9660SectorSize) + 1) * Iso9660SectorSize;
            pos = nextSector;
            continue;
          }
          if (pos + recLen > dirData.Length) break;

          var idLen = dirData[pos + 32];
          if (idLen > 0 && recLen >= 33 + idLen) {
            // Skip . and .. entries
            if (!(idLen == 1 && (dirData[pos + 33] == 0x00 || dirData[pos + 33] == 0x01))) {
              var fileLba = ReadUInt32LE(dirData, pos + 2);
              var fileSize = ReadUInt32LE(dirData, pos + 10);
              var flags = dirData[pos + 25];
              var isDir = (flags & 0x02) != 0;

              var rawName = System.Text.Encoding.ASCII.GetString(dirData, pos + 33, idLen);
              var semi = rawName.IndexOf(';');
              var name = semi >= 0 ? rawName[..semi] : rawName;

              if (fileLba > 0 && fileSize > 0 && !isDir) {
                var fileStart = (long)fileLba * sectorSize;
                var fileSectors = ((long)fileSize + Iso9660SectorSize - 1) / Iso9660SectorSize;
                yield return new DefragBlockInfo(fileStart, fileSectors * sectorSize, DefragBlockKind.Used,
                  FileName: name, Classification: DefragBlockClass.Normal);
              }
            }
          }

          pos += recLen;
        }
      }
    }

    // Any remaining space after the last known structure
    var lastKnown = stream.Length;
    // (Gap detection is left to the UI layer which fills unaccounted regions as Free)
  }

  private static (int SectorSize, int DataOffset) DetectGeometry(Stream stream) {
    if (TryProbe(stream, 2352, 16)) return (2352, 16);
    if (TryProbe(stream, 2352, 24)) return (2352, 24);
    if (TryProbe(stream, 2336, 8)) return (2336, 8);
    if (TryProbe(stream, 2048, 0)) return (2048, 0);
    return (2352, 16);
  }

  private static bool TryProbe(Stream stream, int sectorSize, int dataOff) {
    var pvdOff = (long)16 * sectorSize + dataOff;
    if (pvdOff + 6 > stream.Length) return false;
    stream.Position = pvdOff;
    Span<byte> sig = stackalloc byte[6];
    var read = stream.Read(sig);
    if (read < 6) return false;
    return sig[0] == 1 && sig[1] == (byte)'C' && sig[2] == (byte)'D' &&
           sig[3] == (byte)'0' && sig[4] == (byte)'0' && sig[5] == (byte)'1';
  }

  private static byte[]? ReadSectorData(Stream stream, int lba, int sectorSize, int dataOffset) {
    var start = (long)lba * sectorSize + dataOffset;
    if (start + Iso9660SectorSize > stream.Length) return null;
    stream.Position = start;
    var buf = new byte[Iso9660SectorSize];
    var total = 0;
    while (total < buf.Length) {
      var r = stream.Read(buf, total, buf.Length - total);
      if (r == 0) return null;
      total += r;
    }
    return buf;
  }

  private static uint ReadUInt32LE(byte[] data, int offset) =>
    (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
}
