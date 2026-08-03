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
  /// Adds a file whose bytes are produced on demand; the layout is settled from
  /// <paramref name="size" /> before any are read.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream)
    => _inner.AddStreamingFile(name, size, openStream);

  /// <summary>Auto-sized streaming build; the TFAT post-pass re-stamps both FAT copies.</summary>
  public void BuildToStreamingAutoSized(Stream output, int bytesPerSector = 512, int requestedClusterSize = 0,
      string? volumeLabel = null, int forcedFatType = 0) {
    ArgumentNullException.ThrowIfNull(output);
    _inner.BuildToStreaming(output, bytesPerSector, requestedClusterSize, volumeLabel,
                            forcedFatType, enableLfn: true, transactionFat: true);
    this.StampTfatOnStream(output);
  }

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
    // BuildToStreaming rather than BuildTo: the latter only lays out the volume
    // and never post-fills the clusters of entries added as stream factories, so
    // every such file came back empty.
    _inner.BuildToStreaming(output, bytesPerSector, requestedClusterSize,
      volumeLabel: volumeLabel, forcedFatType: forcedFatType, enableLfn: true,
      transactionFat: true, requestedTotalSectors: totalSectors);
    this.StampTfatOnStream(output);
  }

  /// <summary>
  /// Applies the same markers as <see cref="StampTfat(byte[])" /> directly to a stream.
  /// Every site is at a BPB-derived offset, so only the boot sector has to be read back.
  /// </summary>
  /// <summary>
  /// The transaction sequence each FAT copy currently carries.
  /// </summary>
  /// <remarks>
  /// Read these before a layout pass. The four bytes sit at the end of a FAT
  /// region, which is FAT's to write — a pass that rewrites the allocation
  /// takes them with it.
  /// </remarks>
  public static (uint First, uint Second) ReadSequences(Stream disk) {
    ArgumentNullException.ThrowIfNull(disk);
    var (first, second) = SequenceOffsets(disk);
    return (ReadSequence(disk, first, 1), ReadSequence(disk, second, 2));
  }

  /// <summary>
  /// Puts the TFAT markers back on a volume whose contents have been moved
  /// about, restoring the transaction sequences it carried before.
  /// </summary>
  /// <remarks>
  /// A defragmentation pass rewrites both FAT copies and the directory
  /// entries, and it knows nothing about the four bytes at the end of each FAT
  /// region or the tag in the boot sector. Handing back the sequences read
  /// before the pass keeps the two copies in the step they were in.
  /// </remarks>
  public static void RestampMarkers(Stream disk, uint firstSequence, uint secondSequence) {
    ArgumentNullException.ThrowIfNull(disk);
    new TFatWriter().StampTfatOnStream(disk, firstSequence, secondSequence);
  }

  /// <summary>
  /// Copies the FAT the volume is currently reading from over the other one,
  /// so both describe the same allocation.
  /// </summary>
  /// <remarks>
  /// TFAT keeps the two copies deliberately apart: a change is written into
  /// the idle one and becomes current with a single write of its sequence
  /// number, which leaves the other holding the allocation as it was before.
  /// Anything that treats the copies as interchangeable — a layout pass, most
  /// of all — reads whichever it happens to pick, and on a volume mid-way
  /// through that protocol the two do not agree. Bringing them together first
  /// gives up the rollback copy, which a layout pass gives up anyway.
  /// </remarks>
  public static void SyncFatCopies(Stream disk) {
    ArgumentNullException.ThrowIfNull(disk);
    var (firstSeq, secondSeq) = ReadSequences(disk);
    if (firstSeq == secondSeq) return;

    var (firstAt, secondAt) = SequenceOffsets(disk);
    var bodyLength = secondAt - firstAt - 4;
    if (bodyLength <= 0) return;

    var from = secondSeq > firstSeq ? secondAt - bodyLength : firstAt - bodyLength;
    var to = secondSeq > firstSeq ? firstAt - bodyLength : secondAt - bodyLength;
    if (from < 0 || to < 0 || from + bodyLength > disk.Length || to + bodyLength > disk.Length) return;

    var body = new byte[bodyLength];
    disk.Position = from;
    disk.ReadExactly(body);
    disk.Position = to;
    disk.Write(body);
    disk.Flush();
  }

  /// <summary>Where each FAT region's trailing sequence number sits.</summary>
  private static (long First, long Second) SequenceOffsets(Stream disk) {
    var boot = new byte[512];
    disk.Position = 0;
    disk.ReadExactly(boot, 0, (int)Math.Min(disk.Length, boot.Length));

    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11));
    if (bytesPerSector is 0 or > 4096) bytesPerSector = 512;
    var reserved = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(14));
    var small = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(22));
    var fatSectors = small == 0 ? BinaryPrimitives.ReadInt32LittleEndian(boot.AsSpan(36)) : small;

    var regionLength = (long)fatSectors * bytesPerSector;
    var first = (long)reserved * bytesPerSector;
    return (first + regionLength - 4, first + 2 * regionLength - 4);
  }

  private void StampTfatOnStream(Stream disk, uint? firstSequence = null, uint? secondSequence = null) {
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

    void WriteAt(long offset, ReadOnlySpan<byte> bytes) {
      if (offset < 0 || offset + bytes.Length > disk.Length) return;
      disk.Position = offset;
      disk.Write(bytes);
    }

    WriteAt(fsTypeOffset, tagBytes);

    var fat1Off = (long)rsv * bps;
    var fatRegionLen = (long)fatSize * bps;
    var first = fat1Off + fatRegionLen - 4;
    var second = fat1Off + 2 * fatRegionLen - 4;

    var seq = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(seq, firstSequence ?? this._initialSequence);
    WriteAt(first, seq);
    BinaryPrimitives.WriteUInt32BigEndian(seq, secondSequence ?? this._initialSequence + 1);
    WriteAt(second, seq);

    if (fatType == 32) {
      var bkOff = 6L * bps;
      WriteAt(bkOff + fsTypeOffset, tagBytes);
    }
    disk.Flush();
  }

  /// <summary>The sequence a FAT region already carries, or a default.</summary>
  private static uint ReadSequence(Stream disk, long at, uint fallback) {
    if (at < 0 || at + 4 > disk.Length) return fallback;

    Span<byte> field = stackalloc byte[4];
    disk.Position = at;
    disk.ReadExactly(field);
    var sequence = BinaryPrimitives.ReadUInt32BigEndian(field);
    return sequence == 0 ? fallback : sequence;
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

    // 1) Stamp BS_FilSysType with the TFAT tag. That is the whole marker:
    //    BS_Reserved1, which this used to set as well, is where FAT records an
    //    unclean unmount, and setting it made every FAT checker call the
    //    volume dirty and possibly corrupt.
    var tag = fatType switch {
      12 => "TFAT12  ",
      16 => "TFAT16  ",
      _ => "TFAT32  "
    };
    var tagBytes = Encoding.ASCII.GetBytes(tag);
    var fsTypeOffset = fatType == 32 ? 82 : 54;
    tagBytes.CopyTo(disk, fsTypeOffset);

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
      if (bkOff + fsTypeOffset + 8 <= disk.Length)
        tagBytes.CopyTo(disk, bkOff + fsTypeOffset);
    }

    return disk;
  }
}
