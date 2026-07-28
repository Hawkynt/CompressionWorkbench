#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileSystem.Fat;

namespace FileSystem.TFat;

/// <summary>
/// Builds a Transactional FAT (TFAT) filesystem image. Delegates the heavy
/// lifting (BPB, FAT chain, root directory, LFN encoding) to
/// <see cref="FatWriter"/>, then post-processes the resulting image to add
/// TFAT-specific markers:
///
/// <list type="number">
///   <item><description>Stamp <c>BS_FilSysType</c> with "TFAT12  ", "TFAT16  ", or "TFAT32  " (replaces the plain FAT type tag).</description></item>
///   <item><description>Set <c>BS_Reserved1</c> byte (offset 37 for FAT12/16, offset 65 for FAT32) to <c>0x01</c> as a redundant TFAT marker.</description></item>
///   <item><description>Write a 4-byte big-endian transaction sequence number to the last 4 bytes of each FAT region. Initially FAT1.seq=1, FAT2.seq=2 — FAT2 is the committed (active) copy.</description></item>
///   <item><description>Both FATs hold identical chain data; subsequent transactional updates will alternate which copy is current.</description></item>
/// </list>
///
/// <para>The result is a standard FAT image (any FAT driver can read it) with
/// TFAT detection markers so <see cref="TFatReader"/> recognises it and picks
/// the active FAT correctly even after a power-fail in the middle of a
/// transaction.</para>
/// </summary>
public sealed class TFatWriter {
  private readonly FatWriter _inner = new();
  private uint _initialSequence = 1;

  /// <summary>
  /// Initial transaction sequence stored on FAT1; FAT2 is stored at
  /// <see cref="InitialSequence"/>+1 so it wins active-FAT selection.
  /// Used by round-trip tests to verify sequence-based active selection.
  /// </summary>
  public uint InitialSequence {
    get => _initialSequence;
    set => _initialSequence = value;
  }

  public void AddFile(string name, byte[] data) => _inner.AddFile(name, data);

  /// <summary>
  /// Builds the TFAT image. <paramref name="totalSectors"/> defaults to 2880
  /// (a 1.44 MB image) which yields FAT12; for FAT32 testing pass a larger
  /// value (e.g. 131072 for ~64 MB).
  /// </summary>
  /// <param name="totalSectors">Total sectors (default 2880 = 1.44 MB floppy).</param>
  /// <param name="bytesPerSector">Bytes per sector (default 512).</param>
  /// <param name="requestedClusterSize">Desired cluster size in bytes (0 = auto-select).</param>
  /// <param name="volumeLabel">Optional volume label (up to 11 chars). Defaults to "NO NAME" when null.</param>
  /// <param name="forcedFatType">Force a specific FAT variant: 12, 16, or 32. 0 = auto-select by cluster count.</param>
  public byte[] Build(int totalSectors = 2880, int bytesPerSector = 512, int requestedClusterSize = 0,
    string? volumeLabel = null, int forcedFatType = 0) {
    // transactionFat: true sets BS_Reserved1 = 0x01 directly in the BPB; StampTfat
    // re-applies it (plus the FilSysType tag + per-FAT sequence numbers) so both
    // paths converge on identical markers.
    var disk = _inner.Build(totalSectors, bytesPerSector, requestedClusterSize,
      volumeLabel: volumeLabel, forcedFatType: forcedFatType, transactionFat: true);
    return StampTfat(disk);
  }

  /// <summary>
  /// Builds a TFAT image auto-sized to fit the added files (delegates sizing to
  /// <see cref="FatWriter.BuildAutoSized"/>), then stamps the TFAT markers.
  /// Prefer this over <see cref="Build"/> when the file count / total size is
  /// not known up-front (e.g. from a directory walk) so the writer picks an
  /// image size and FAT type that fit.
  /// </summary>
  /// <param name="bytesPerSector">Bytes per sector (default 512).</param>
  /// <param name="requestedClusterSize">Desired cluster size in bytes (0 = auto-select).</param>
  /// <param name="volumeLabel">Optional volume label (up to 11 chars). Defaults to "NO NAME" when null.</param>
  /// <param name="forcedFatType">Force a specific FAT variant: 12, 16, or 32. 0 = auto-select by cluster count.</param>
  public byte[] BuildAutoSized(int bytesPerSector = 512, int requestedClusterSize = 0,
    string? volumeLabel = null, int forcedFatType = 0) {
    var disk = _inner.BuildAutoSized(bytesPerSector, requestedClusterSize,
      volumeLabel: volumeLabel, forcedFatType: forcedFatType, transactionFat: true);
    return StampTfat(disk);
  }

  /// <summary>
  /// Picks the cluster size that minimises slack + FAT overhead for a fixed
  /// image size. Pure delegation to <see cref="FatWriter.PickClusterForFixedImage"/>
  /// — TFAT shares FAT's exact geometry, so the same optimiser applies.
  /// Returns 0 if no candidate fits (caller falls back to the writer default).
  /// </summary>
  public int PickClusterForFixedImage(int totalSectors, int bytesPerSector,
    int forcedFatType, int requestedRootEntries, bool enableLfn) =>
    _inner.PickClusterForFixedImage(totalSectors, bytesPerSector, forcedFatType, requestedRootEntries, enableLfn);

  /// <summary>
  /// Post-processes a freshly built FAT image to add the TFAT-specific
  /// detection markers and per-FAT transaction sequence numbers.
  /// </summary>
  /// <summary>
  /// Streams a TFAT image to <paramref name="output" />, then stamps the TFAT markers
  /// in place. Delegates the volume itself to <see cref="FatWriter.BuildTo" />, so free
  /// space stays sparse and the image is not bounded by what a byte[] can hold.
  /// </summary>
  public void BuildTo(Stream output, int totalSectors, int bytesPerSector = 512,
    int requestedClusterSize = 0, string? volumeLabel = null, int forcedFatType = 0) {
    ArgumentNullException.ThrowIfNull(output);
    _inner.BuildTo(output, totalSectors, bytesPerSector, requestedClusterSize,
      volumeLabel: volumeLabel, forcedFatType: forcedFatType, transactionFat: true);
    this.StampTfatOnStream(output);
  }

  /// <summary>
  /// Applies the same markers as <see cref="StampTfat(byte[])" /> directly to a stream.
  /// Every site is at a BPB-derived offset, so only the boot sector has to be read back.
  /// </summary>
  private void StampTfatOnStream(Stream disk) {
    var boot = new byte[512];
    disk.Position = 0;
    disk.ReadExactly(boot, 0, Math.Min(boot.Length, (int)Math.Min(disk.Length, boot.Length)));

    var bps = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11));
    if (bps is 0 or > 4096) bps = 512;
    var spc = boot[13] == 0 ? 1 : boot[13];
    var rsv = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(14));
    var rootEntCnt = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(17));
    var ts16 = (int)BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(19));
    var totalSec = ts16 == 0 ? BinaryPrimitives.ReadInt32LittleEndian(boot.AsSpan(32)) : ts16;
    var fs16 = (int)BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(22));
    var fatSize = fs16 == 0 ? BinaryPrimitives.ReadInt32LittleEndian(boot.AsSpan(36)) : fs16;

    var rootDirSec = (rootEntCnt * 32 + bps - 1) / bps;
    var firstDataSec = rsv + 2L * fatSize + rootDirSec;
    var totalClusters = (totalSec - firstDataSec) / spc;
    var fatType = totalClusters < 4085 ? 12 : totalClusters < 65525 ? 16 : 32;

    var tagBytes = Encoding.ASCII.GetBytes(fatType switch {
      12 => "TFAT12  ", 16 => "TFAT16  ", _ => "TFAT32  " });
    var fsTypeOffset = fatType == 32 ? 82 : 54;
    var reserved1Offset = fatType == 32 ? 65 : 37;

    void WriteAt(long offset, ReadOnlySpan<byte> bytes) {
      if (offset < 0 || offset + bytes.Length > disk.Length) return;
      disk.Position = offset;
      disk.Write(bytes);
    }

    WriteAt(fsTypeOffset, tagBytes);
    WriteAt(reserved1Offset, [0x01]);

    var fat1Off = (long)rsv * bps;
    var fatRegionLen = (long)fatSize * bps;
    var seq = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(seq, _initialSequence);
    WriteAt(fat1Off + fatRegionLen - 4, seq);
    BinaryPrimitives.WriteUInt32BigEndian(seq, _initialSequence + 1);
    WriteAt(fat1Off + fatRegionLen + fatRegionLen - 4, seq);

    if (fatType == 32) {
      var bkOff = 6L * bps;
      WriteAt(bkOff + fsTypeOffset, tagBytes);
      WriteAt(bkOff + reserved1Offset, [0x01]);
    }
    disk.Flush();
  }

  private byte[] StampTfat(byte[] disk) {
    // Derive FAT parameters from the BPB we just wrote, so we know where the
    // FAT regions live and which extended-BPB layout was used.
    var bps = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(11));
    if (bps is 0 or > 4096) bps = 512;
    var spc = disk[13] == 0 ? 1 : disk[13];
    var rsv = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(14));
    var rootEntCnt = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(17));

    var ts16 = (int)BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(19));
    var totalSec = ts16 == 0 ? BinaryPrimitives.ReadInt32LittleEndian(disk.AsSpan(32)) : ts16;
    var fs16 = (int)BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(22));
    var fatSize = fs16 == 0 ? BinaryPrimitives.ReadInt32LittleEndian(disk.AsSpan(36)) : fs16;

    var rootDirSec = (rootEntCnt * 32 + bps - 1) / bps;
    var firstDataSec = rsv + 2 * fatSize + rootDirSec;
    var totalClusters = (totalSec - firstDataSec) / spc;
    var fatType = totalClusters < 4085 ? 12 : totalClusters < 65525 ? 16 : 32;

    // 1) Stamp BS_FilSysType with TFAT tag.
    var tag = fatType switch {
      12 => "TFAT12  ",
      16 => "TFAT16  ",
      _ => "TFAT32  "
    };
    var tagBytes = Encoding.ASCII.GetBytes(tag);
    var fsTypeOffset = fatType == 32 ? 82 : 54;
    tagBytes.CopyTo(disk, fsTypeOffset);

    // 2) Set BS_Reserved1 byte to 0x01.
    var reserved1Offset = fatType == 32 ? 65 : 37;
    disk[reserved1Offset] = 0x01;

    // 3) Write transaction sequence numbers to the trailing 4 bytes of each FAT.
    var fat1Off = rsv * bps;
    var fat2Off = fat1Off + fatSize * bps;
    var fatRegionLen = fatSize * bps;
    var seq1Pos = fat1Off + fatRegionLen - 4;
    var seq2Pos = fat2Off + fatRegionLen - 4;
    if (seq1Pos + 4 <= disk.Length)
      BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(seq1Pos), _initialSequence);
    if (seq2Pos + 4 <= disk.Length)
      BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(seq2Pos), _initialSequence + 1);

    // 4) If FAT32, also mirror the BS_FilSysType into the backup boot sector
    // (sector 6) so a recovery tool that reads the backup still sees TFAT.
    if (fatType == 32) {
      var bkOff = 6 * bps;
      if (bkOff + fsTypeOffset + 8 <= disk.Length) {
        tagBytes.CopyTo(disk, bkOff + fsTypeOffset);
        disk[bkOff + reserved1Offset] = 0x01;
      }
    }

    return disk;
  }
}
