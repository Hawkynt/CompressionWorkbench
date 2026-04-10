#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.D81;

public sealed class D81Writer {
  private const int StandardSize = 819200;
  private const int SectorSize = 256;
  private const int SectorsPerTrackConst = 40;
  private const int TotalTracks = 80;
  private const int DirTrack = 40;
  private const int HeaderSector = 0;
  private const int Bam1Sector = 1;
  private const int Bam2Sector = 2;
  private const int DirStartSector = 3;
  private const int Interleave = 10;

  private readonly List<(string Name, byte FileType, byte[] Data)> _files = [];

  public void AddFile(string name, byte fileType, byte[] data) => _files.Add((name, fileType, data));

  public void AddFile(string name, byte[] data) => _files.Add((name, 0x82, data)); // PRG default

  public byte[] Build(string diskName = "DISK") {
    var disk = new byte[StandardSize];
    var bam = new bool[TotalTracks + 1][];

    for (var t = 1; t <= TotalTracks; t++)
      bam[t] = new bool[SectorsPerTrackConst];

    // Reserve header, BAM, and directory sectors on track 40
    bam[DirTrack][HeaderSector] = true;
    bam[DirTrack][Bam1Sector] = true;
    bam[DirTrack][Bam2Sector] = true;
    bam[DirTrack][DirStartSector] = true;

    // Write files
    var dirEntries = new List<(string Name, byte FileType, int StartTrack, int StartSector, int SectorCount)>();

    foreach (var (name, fileType, data) in _files) {
      if (data.Length == 0) continue;
      var sectors = WriteSectorChain(disk, bam, data);
      if (sectors.Count == 0) continue;
      dirEntries.Add((name, fileType, sectors[0].Track, sectors[0].Sector, sectors.Count));
    }

    // Write directory
    WriteDirectory(disk, bam, dirEntries);

    // Write header and BAM
    WriteHeader(disk, diskName);
    WriteBam(disk, bam);

    return disk;
  }

  private static int GetSectorOffset(int track, int sector) {
    return ((track - 1) * SectorsPerTrackConst + sector) * SectorSize;
  }

  private static List<(int Track, int Sector)> WriteSectorChain(byte[] disk, bool[][] bam, byte[] data) {
    var sectors = new List<(int Track, int Sector)>();
    var dataLen = data.Length;
    var pos = 0;

    while (pos < dataLen) {
      var (track, sector) = AllocateSector(bam, sectors.Count > 0 ? sectors[^1].Track : 1,
        sectors.Count > 0 ? sectors[^1].Sector : 0);
      if (track == 0) break;

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

    // Fix forward links
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
      if (track == DirTrack) continue;

      var startSector = (track == lastTrack) ? (lastSector + Interleave) % SectorsPerTrackConst : 0;

      for (var s = 0; s < SectorsPerTrackConst; s++) {
        var sector = (startSector + s) % SectorsPerTrackConst;
        if (!bam[track][sector]) {
          bam[track][sector] = true;
          return (track, sector);
        }
      }
    }
    return (0, 0);
  }

  private static void WriteDirectory(byte[] disk, bool[][] bam,
    List<(string Name, byte FileType, int StartTrack, int StartSector, int SectorCount)> entries) {

    var dirSectorTrack = DirTrack;
    var dirSectorNum = DirStartSector;
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
        if (nextSector >= SectorsPerTrackConst) nextSector -= SectorsPerTrackConst;
        while (bam[DirTrack][nextSector] && nextSector != dirSectorNum) {
          nextSector = (nextSector + 1) % SectorsPerTrackConst;
        }
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

  private static void WriteHeader(byte[] disk, string diskName) {
    var off = GetSectorOffset(DirTrack, HeaderSector);

    // Link to directory start
    disk[off] = (byte)DirTrack;
    disk[off + 1] = (byte)DirStartSector;

    // DOS version
    disk[off + 2] = 0x44; // 'D' (1581 DOS version)
    disk[off + 3] = 0x00;

    // Disk name at offset 4, 16 bytes padded with 0xA0
    var nameBytes = Encoding.ASCII.GetBytes(diskName.Length > 16 ? diskName[..16] : diskName);
    nameBytes.CopyTo(disk, off + 4);
    for (var j = nameBytes.Length; j < 16; j++)
      disk[off + 4 + j] = 0xA0;

    // Disk ID at offset 0x16 (22): 2 bytes
    disk[off + 0x16] = 0x30; // '0'
    disk[off + 0x17] = 0x30; // '0'
    disk[off + 0x18] = 0xA0;
    // DOS type
    disk[off + 0x19] = 0x33; // '3'
    disk[off + 0x1A] = 0x44; // 'D'
  }

  private static void WriteBam(byte[] disk, bool[][] bam) {
    // BAM sector 1: tracks 1-40
    WriteBamSector(disk, bam, Bam1Sector, 1, 40);

    // BAM sector 2: tracks 41-80
    WriteBamSector(disk, bam, Bam2Sector, 41, 80);
  }

  private static void WriteBamSector(byte[] disk, bool[][] bam, int bamSector, int startTrack, int endTrack) {
    var off = GetSectorOffset(DirTrack, bamSector);

    // Link bytes: next track/sector (0/0xFF for last)
    disk[off] = (byte)DirTrack;
    disk[off + 1] = (byte)(bamSector == Bam1Sector ? Bam2Sector : 0xFF);

    // DOS version ID
    disk[off + 2] = 0x44; // 'D'
    disk[off + 3] = 0xBB; // complement

    // Disk ID
    disk[off + 4] = 0x30;
    disk[off + 5] = 0x30;

    // I/O byte and auto-boot flag
    disk[off + 6] = 0xC0;
    disk[off + 7] = 0x00;

    // BAM entries: 6 bytes per track starting at offset 16
    for (var t = startTrack; t <= endTrack; t++) {
      var bamOff = off + 16 + (t - startTrack) * 6;
      var freeSectors = 0;
      var bitmap = new byte[5];

      for (var s = 0; s < SectorsPerTrackConst; s++) {
        if (!bam[t][s]) {
          freeSectors++;
          bitmap[s / 8] |= (byte)(1 << (s % 8));
        }
      }

      disk[bamOff] = (byte)freeSectors;
      bitmap.CopyTo(disk, bamOff + 1);
    }
  }
}
