#pragma warning disable CS1591
namespace FileSystem.Gemdos;

/// <summary>
/// Builds Atari ST GEMDOS disk images by delegating to <see cref="FileSystem.Fat.FatWriter"/>
/// (which emits a spec-compliant FAT12 BPB) and then patching the jump byte at
/// offset 0 from MS-DOS's <c>0xEB</c> (x86 <c>JMP</c>) to Atari's <c>0x60</c>
/// (m68k <c>BRA.S</c>). All other BPB fields use the FAT spec layout. The
/// result is a byte-identical GEMDOS volume: same boot-sector size, same FAT
/// chains, same root directory, same data-cluster layout.
/// </summary>
public sealed class GemdosWriter {

  private readonly FileSystem.Fat.FatWriter _inner = new();

  /// <summary>Adds a file to the GEMDOS image. Paths may use '/' or '\' separators
  /// for subdirectories; the underlying FAT writer builds the directory tree.</summary>
  public void AddFile(string name, byte[] data, System.DateTime? modTime = null)
    => _inner.AddFile(name, data, modTime);

  /// <summary>
  /// Builds the GEMDOS image. GEMDOS volumes are FAT12 with the m68k BRA.S jump
  /// byte (0x60) — we force FAT12 to match the on-disk format and patch the jump.
  /// Standard sizes: 360 KB (DD SS), 720 KB (DD DS), 1.44 MB (HD DS) — all classic
  /// Atari ST floppy formats. Hard-disk GEMDOS partitions are not yet supported
  /// because they need the AHDI partition table (out of scope).
  /// </summary>
  /// <param name="totalSectors">Total sectors at <paramref name="bytesPerSector"/>.</param>
  /// <param name="bytesPerSector">Bytes per sector (256/512/1024 for Atari).</param>
  /// <param name="sectorsPerCluster">Sectors per cluster (1, 2, or 4).</param>
  /// <param name="rootEntries">Root directory entry count (typically 112 on 720 KB,
  /// 224 on 1.44 MB).</param>
  /// <param name="volumeLabel">11-char volume label, null = none.</param>
  public byte[] Build(
      int totalSectors = 1440,
      int bytesPerSector = 512,
      int sectorsPerCluster = 2,
      int rootEntries = 112,
      string? volumeLabel = null) {
    var clusterSize = sectorsPerCluster * bytesPerSector;
    var disk = _inner.Build(
      totalSectors: totalSectors,
      bytesPerSector: bytesPerSector,
      requestedClusterSize: clusterSize,
      volumeLabel: volumeLabel,
      forcedFatType: 12,
      enableLfn: false,                  // GEMDOS predates VFAT — strict 8.3 names
      transactionFat: false,
      requestedRootEntries: rootEntries,
      forceLfn: false);
    // Patch the jump byte: MS-DOS 0xEB → Atari 0x60. The FatWriter writes
    // EB 3C 90 for FAT12; we replace the first byte with 0x60 and zero the
    // 3C/90 so the BPB starts cleanly at offset 0x0B (which is unchanged).
    // Atari boot sectors don't use the same x86 jmp-to-code layout; the
    // displacement bytes are read as a branch by the m68k but in non-boot
    // disks they're just data.
    if (disk.Length >= 1)
      disk[0] = GemdosBpb.GemdosJump;
    return disk;
  }
}
