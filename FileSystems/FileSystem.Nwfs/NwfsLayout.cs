#pragma warning disable CS1591
namespace FileSystem.Nwfs;

/// <summary>
/// Where the areas of an NWFS386 partition sit, and the few arithmetic rules
/// that tie them together.
/// </summary>
/// <remarks>
/// <para>A NetWare partition opens with a hotfix header at its own sector 32,
/// the mirror header in the sector after that, and the volume area a stated
/// number of redirection sectors further on. The data area follows the volume
/// area, and every block number a volume uses is counted from there.</para>
///
/// <para>The block size is not stored directly. The volume entry carries a
/// divisor instead, and the size is <c>(256 / divisor) * 1024</c> bytes — so a
/// divisor of 64 means blocks of 4 KB.</para>
/// </remarks>
internal static class NwfsLayout {

  /// <summary>Sector size the whole layout is counted in.</summary>
  internal const int SectorSize = 512;

  /// <summary>Where the hotfix header sits, counted from the partition's start.</summary>
  internal const long HotfixOffsetInPartition = 0x4000;

  /// <summary>The mirror header follows the hotfix header by one sector.</summary>
  internal const long MirrorOffsetInPartition = HotfixOffsetInPartition + SectorSize;

  /// <summary>The volume area is this long whatever the number of volumes in it.</summary>
  internal const int VolumeAreaBytes = 4 * 16384;

  /// <summary>Bytes one volume entry takes in the volume area.</summary>
  internal const int VolumeEntryBytes = 60;

  /// <summary>Bytes every directory entry takes, whatever kind it is.</summary>
  internal const int DirectoryEntryBytes = 128;

  /// <summary>Bytes one FAT entry takes: the block's place in its chain, then the next block.</summary>
  internal const int FatEntryBytes = 8;

  /// <summary>What a chain's last block names as its successor, and what a free entry holds.</summary>
  internal const uint NoBlock = 0xFFFFFFFF;

  /// <summary>The directory this volume's root is, which entries at the top name as their parent.</summary>
  internal const uint RootDirectoryId = 0;

  /// <summary>Parent ids that mark an entry as something other than a file or a directory.</summary>
  internal const uint DirIdAvailable = 0xFFFFFFFF;
  internal const uint DirIdGrantList = 0xFFFFFFFE;
  internal const uint DirIdVolumeInfo = 0xFFFFFFFD;

  /// <summary>The bit in an entry's attributes that says it is a directory.</summary>
  internal const uint AttributeDirectory = 0x10;

  /// <summary>The archive bit, which a freshly written file carries.</summary>
  internal const uint AttributeArchive = 0x20;

  /// <summary>The longest name an entry holds, the field being twelve bytes.</summary>
  internal const int MaxNameLength = 12;

  /// <summary>The volume name field, and the longest name that fits it.</summary>
  internal const int MaxVolumeNameLength = 19;

  /// <summary>What the first segment of a volume gives as its first sector.</summary>
  internal const uint FirstSectorOfFirstSegment = 160;

  /// <summary>The object every file and directory written here belongs to.</summary>
  internal const uint SupervisorObjectId = 1;

  /// <summary>The divisor a volume entry carries for <paramref name="blockSize" />.</summary>
  internal static uint BlockValue(int blockSize) => (uint)(256 * 1024 / blockSize);

  /// <summary>Whether <paramref name="blockSize" /> is one the format can name.</summary>
  internal static bool IsValidBlockSize(int blockSize)
    => blockSize >= 1024 && blockSize <= 256 * 1024
       && (blockSize & blockSize - 1) == 0
       && 256 * 1024 % blockSize == 0;
}
