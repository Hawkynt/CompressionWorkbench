#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.TrDos;

/// <summary>
/// Creates TR-DOS (.TRD) ZX Spectrum disk images.
/// </summary>
public sealed class TrDosWriter {
  private const int SectorSize = 256;
  private const int SectorsPerTrack = 16;
  private const int TrackSize = SectorSize * SectorsPerTrack;
  private const int TotalTracks = 160; // 80 tracks * 2 sides
  private const int DiskSize = TotalTracks * TrackSize; // 655360
  private const int DiskInfoOffset = 0x800;

  private readonly List<(string Name, char Type, byte[] Data)> _files = [];

  public void AddFile(string name, char type, byte[] data) => _files.Add((name, type, data));

  public byte[] Build(string label = "DISK") {
    var disk = new byte[DiskSize];

    var dirIndex = 0;
    var freeSector = 1; // first free sector (skip track 0 for directory)
    var freeTrack = 1;

    foreach (var (name, type, data) in _files) {
      var sectors = (data.Length + SectorSize - 1) / SectorSize;
      if (sectors == 0) sectors = 1;

      // Write directory entry
      var dirOffset = dirIndex * 16;
      if (dirOffset + 16 > 8 * SectorSize) break; // directory full

      var paddedName = name.Length > 8 ? name[..8] : name.PadRight(8);
      Encoding.ASCII.GetBytes(paddedName).CopyTo(disk, dirOffset);
      disk[dirOffset + 8] = (byte)type;
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(dirOffset + 9), 0); // param1
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(dirOffset + 11), (ushort)data.Length); // param2
      disk[dirOffset + 13] = (byte)sectors;
      disk[dirOffset + 14] = (byte)freeSector;
      disk[dirOffset + 15] = (byte)freeTrack;

      // Write file data
      var dataOffset = freeTrack * TrackSize + freeSector * SectorSize;
      if (dataOffset + data.Length <= disk.Length)
        data.CopyTo(disk, dataOffset);

      // Advance free position
      var totalSectors = freeSector + sectors;
      freeTrack += totalSectors / SectorsPerTrack;
      freeSector = totalSectors % SectorsPerTrack;

      dirIndex++;
    }

    // Write disk info sector
    disk[DiskInfoOffset + 0xE1] = (byte)freeSector;
    disk[DiskInfoOffset + 0xE2] = (byte)freeTrack;
    disk[DiskInfoOffset + 0xE3] = 0x16; // 80 tracks, 2 sides
    disk[DiskInfoOffset + 0xE4] = (byte)_files.Count;
    var totalFree = (TotalTracks - freeTrack) * SectorsPerTrack - freeSector;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(DiskInfoOffset + 0xE5), (ushort)totalFree);
    disk[DiskInfoOffset + 0xE7] = 0x10; // TR-DOS ID
    var labelBytes = Encoding.ASCII.GetBytes(label.PadRight(8)[..8]);
    labelBytes.CopyTo(disk, DiskInfoOffset + 0xF5);

    return disk;
  }
}
