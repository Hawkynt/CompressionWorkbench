#pragma warning disable CS1591
namespace FileSystem.Cpm;

/// <summary>
/// Geometry of the canonical Digital Research CP/M 2.2 reference disk (8" SSSD
/// IBM diskette): 77 tracks × 26 sectors × 128 bytes = 256 256 bytes, 2 reserved
/// tracks for the CP/M BIOS, 1024-byte allocation blocks, 64-entry directory.
/// Not every CP/M variant uses these numbers — implementations like Kaypro,
/// Osborne, and Amstrad had their own DPBs — but 8" SSSD is the format every
/// CP/M-80 BDOS shipped with and is the most widely understood layout.
/// </summary>
internal static class CpmLayout {
  public const int TotalBytes = 256_256;
  public const int SectorSize = 128;
  public const int SectorsPerTrack = 26;
  public const int Tracks = 77;
  public const int ReservedTracks = 2;
  public const int BlockSize = 1024;

  // Derived values.
  public const int ReservedBytes = ReservedTracks * SectorsPerTrack * SectorSize; // 6656
  public const int DataBytes = TotalBytes - ReservedBytes;                        // 249600
  public const int TotalBlocks = DataBytes / BlockSize;                            // 243
  public const int DirectoryBlocks = 2;                                            // 2 KB directory
  public const int DirectoryBytes = DirectoryBlocks * BlockSize;                   // 2048
  public const int DirectoryEntries = DirectoryBytes / DirectoryEntrySize;         // 64
  public const int DirectoryEntrySize = 32;
  public const int DataBlockStart = DirectoryBlocks;                               // blocks 2..242 are file data
  public const int UsableBlocks = TotalBlocks - DirectoryBlocks;                   // 241
  public const int RecordsPerBlock = BlockSize / SectorSize;                       // 8
  public const int BlocksPerExtent = 16;                                           // 16 KB per extent
  public const int RecordsPerExtent = BlocksPerExtent * RecordsPerBlock;           // 128
  public const byte EmptyEntryUserCode = 0xE5;                                     // deleted/empty
}
