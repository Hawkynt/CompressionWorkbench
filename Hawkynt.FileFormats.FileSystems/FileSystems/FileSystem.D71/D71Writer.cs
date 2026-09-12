#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.D71;

/// <summary>
/// Builds a double-sided Commodore 1571 D71 disk image from a set of files, filling in both BAM sides and the directory.
/// </summary>
public sealed class D71Writer {
  private const int StandardSize = 349696;
  private const int SectorSize = 256;
  private const int DirTrack = 18;
  private const int TotalTracks = 70;
  private const int Interleave = 6;

  // Side 2 BAM is at track 53 sector 0; native 1571 DOS reserves the whole track.
  private const int Side2BamTrack = 53;

  private static readonly int[] SectorsPerTrack = [
    0,
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
    19, 19, 19, 19, 19, 19, 19,
    18, 18, 18, 18, 18, 18,
    17, 17, 17, 17, 17,
    21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21, 21,
    19, 19, 19, 19, 19, 19, 19,
    18, 18, 18, 18, 18, 18,
    17, 17, 17, 17, 17
  ];

  private readonly List<(string Name, byte FileType, byte[] Data)> _files = [];

  public void AddFile(string name, byte fileType, byte[] data) => _files.Add((name, fileType, data));
  public void AddFile(string name, byte[] data) => _files.Add((name, 0x82, data));

  /// <summary>
  /// Builds the complete D71 image.
  /// </summary>
  /// <param name="diskName">PETSCII disk name (up to 16 chars; padded with 0xA0).</param>
  /// <param name="diskId">Two-character disk ID stored at BAM offset 0xA2..0xA3 (default "00").</param>
  public byte[] Build(string diskName = "DISK", string diskId = "00") {
    var disk = new byte[StandardSize];
    var bam = new bool[TotalTracks + 1][];

    for (var t = 1; t <= TotalTracks; t++)
      bam[t] = new bool[SectorsPerTrack[t]];

    bam[DirTrack][0] = true;
    bam[DirTrack][1] = true;
    Array.Fill(bam[Side2BamTrack], true);

    var dirEntries = new List<(string Name, byte FileType, int StartTrack, int StartSector, int SectorCount)>();

    foreach (var (name, fileType, data) in _files) {
      if (data.Length == 0) continue;
      var sectors = WriteSectorChain(disk, bam, data);
      if (sectors.Count == 0) continue;
      dirEntries.Add((name, fileType, sectors[0].Track, sectors[0].Sector, sectors.Count));
    }

    WriteDirectory(disk, bam, dirEntries);
    WriteBam(disk, bam, diskName, diskId);
    return disk;
  }

  private static int GetSectorOffset(int track, int sector) {
    var offset = 0;
    for (var t = 1; t < track; t++)
      offset += SectorsPerTrack[t] * SectorSize;
    return offset + sector * SectorSize;
  }

  private static List<(int Track, int Sector)> WriteSectorChain(byte[] disk, bool[][] bam, byte[] data) {
    var sectors = new List<(int Track, int Sector)>();
    var dataLen = data.Length;
    var pos = 0;

    while (pos < dataLen) {
      var (track, sector) = AllocateSector(
        bam,
        sectors.Count > 0 ? sectors[^1].Track : 1,
        sectors.Count > 0 ? sectors[^1].Sector : 0);
      if (track == 0)
        throw new InvalidOperationException(
          $"D71: the disk is full after {sectors.Count:N0} blocks; {dataLen - pos:N0} bytes " +
          "of the file do not fit on a 1571 disk.");

      sectors.Add((track, sector));
      var off = GetSectorOffset(track, sector);
      var remaining = dataLen - pos;
      var chunk = Math.Min(254, remaining);

      if (remaining <= 254) {
        disk[off] = 0;
        disk[off + 1] = (byte)(chunk + 1);
      }

      data.AsSpan(pos, chunk).CopyTo(disk.AsSpan(off + 2));
      pos += chunk;
    }

    for (var i = 0; i < sectors.Count - 1; i++) {
      var off = GetSectorOffset(sectors[i].Track, sectors[i].Sector);
      disk[off] = (byte)sectors[i + 1].Track;
      disk[off + 1] = (byte)sectors[i + 1].Sector;
    }

    return sectors;
  }

  private static (int Track, int Sector) AllocateSector(bool[][] bam, int lastTrack, int lastSector) {
    for (var t = 1; t <= TotalTracks; t++) {
      var track = t;
      if (track is DirTrack or Side2BamTrack) continue;

      var maxSectors = SectorsPerTrack[track];
      var startSector = track == lastTrack ? (lastSector + Interleave) % maxSectors : 0;

      for (var s = 0; s < maxSectors; s++) {
        var sector = (startSector + s) % maxSectors;
        if (!bam[track][sector]) {
          bam[track][sector] = true;
          return (track, sector);
        }
      }
    }
    return (0, 0);
  }

  private static void WriteDirectory(
      byte[] disk,
      bool[][] bam,
      List<(string Name, byte FileType, int StartTrack, int StartSector, int SectorCount)> entries) {
    var dirSectorTrack = DirTrack;
    var dirSectorNum = 1;
    var entryIndex = 0;

    while (entryIndex < entries.Count) {
      var off = GetSectorOffset(dirSectorTrack, dirSectorNum);
      var entriesInSector = Math.Min(8, entries.Count - entryIndex);

      for (var i = 0; i < entriesInSector; i++) {
        var e = entries[entryIndex + i];
        var entryOff = off + i * 32;

        disk[entryOff + 2] = e.FileType;
        disk[entryOff + 3] = (byte)e.StartTrack;
        disk[entryOff + 4] = (byte)e.StartSector;

        var nameBytes = Encoding.ASCII.GetBytes(e.Name.Length > 16 ? e.Name[..16] : e.Name);
        nameBytes.CopyTo(disk, entryOff + 5);
        for (var j = nameBytes.Length; j < 16; j++)
          disk[entryOff + 5 + j] = 0xA0;

        BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(entryOff + 30), (ushort)e.SectorCount);
      }

      entryIndex += entriesInSector;

      if (entryIndex < entries.Count) {
        var nextSector = dirSectorNum + 3;
        if (nextSector >= SectorsPerTrack[DirTrack]) nextSector -= SectorsPerTrack[DirTrack];
        while (bam[DirTrack][nextSector] && nextSector != dirSectorNum)
          nextSector = (nextSector + 1) % SectorsPerTrack[DirTrack];
        bam[DirTrack][nextSector] = true;
        disk[off] = (byte)DirTrack;
        disk[off + 1] = (byte)nextSector;
        dirSectorNum = nextSector;
      } else {
        disk[off] = 0;
        disk[off + 1] = (byte)(entriesInSector * 32 - 1);
      }
    }
  }

  private static void WriteBam(byte[] disk, bool[][] bam, string diskName, string diskId) {
    var off = GetSectorOffset(DirTrack, 0);

    disk[off] = DirTrack;
    disk[off + 1] = 1;
    disk[off + 2] = 0x41;
    disk[off + 3] = 0x80;

    for (var t = 1; t <= 35; t++) {
      var bamOff = off + 4 + (t - 1) * 4;
      var maxSectors = SectorsPerTrack[t];
      var freeSectors = 0;
      uint bitmap = 0;

      for (var s = 0; s < maxSectors; s++) {
        if (!bam[t][s]) {
          freeSectors++;
          bitmap |= 1u << s;
        }
      }

      disk[bamOff] = (byte)freeSectors;
      disk[bamOff + 1] = (byte)bitmap;
      disk[bamOff + 2] = (byte)(bitmap >> 8);
      disk[bamOff + 3] = (byte)(bitmap >> 16);
    }

    var nameOff = off + 0x90;
    var nameBytes = Encoding.ASCII.GetBytes(diskName.Length > 16 ? diskName[..16] : diskName);
    nameBytes.CopyTo(disk, nameOff);
    for (var j = nameBytes.Length; j < 16; j++)
      disk[nameOff + j] = 0xA0;

    var idStr = string.IsNullOrEmpty(diskId) ? "00" : diskId;
    disk[off + 0xA2] = (byte)(idStr.Length > 0 ? idStr[0] : '0');
    disk[off + 0xA3] = (byte)(idStr.Length > 1 ? idStr[1] : '0');
    disk[off + 0xA4] = 0xA0;
    disk[off + 0xA5] = 0x32;
    disk[off + 0xA6] = 0x41;

    for (var t = 36; t <= 70; t++) {
      var freeSectors = 0;
      for (var s = 0; s < SectorsPerTrack[t]; s++)
        if (!bam[t][s]) freeSectors++;
      disk[off + 0xDD + (t - 36)] = (byte)freeSectors;
    }

    var s2off = GetSectorOffset(Side2BamTrack, 0);
    for (var t = 36; t <= 70; t++) {
      var bamOff = s2off + (t - 36) * 3;
      var maxSectors = SectorsPerTrack[t];
      uint bitmap = 0;

      for (var s = 0; s < maxSectors; s++)
        if (!bam[t][s]) bitmap |= 1u << s;

      disk[bamOff] = (byte)bitmap;
      disk[bamOff + 1] = (byte)(bitmap >> 8);
      disk[bamOff + 2] = (byte)(bitmap >> 16);
    }
  }
}
