#pragma warning disable CS1591
using System.Buffers.Binary;
using FileSystem.Fat;

namespace FileSystem.FatPlus;

/// <summary>
/// Builds FAT+ filesystem images. FAT+ is a backward-compatible extension to
/// standard FAT32 (and FAT16) that lifts the 4 GiB per-file size cap to 256 GiB
/// by repurposing the low 6 bits of the otherwise-reserved <c>DIR_NTRes</c>
/// byte (offset 12) of the 32-byte directory entry as the high 6 bits of the
/// file size — together with the standard 32-bit <c>DIR_FileSize</c> at offset
/// 28 this forms a 38-bit size field. The OEM-name string in the BPB is set to
/// <c>"FAT+    "</c> (offset 3..10) so FAT+-aware readers see the extension.
/// </summary>
/// <remarks>
/// <para><b>Implementation strategy.</b> This writer wraps <see cref="FatWriter"/>
/// to produce the underlying FAT32 image, then patches:
/// <list type="bullet">
///   <item><description>OEM signature at offset 3..10 to <c>"FAT+    "</c>.</description></item>
///   <item><description>Per-file directory entry: low 6 bits of <c>DIR_NTRes</c>
///     (offset 12) hold bits 32..37 of file size; top 2 bits stay clear to
///     preserve the Windows NT case-flag convention.</description></item>
///   <item><description>Per-file <c>DIR_FileSize</c> (offset 28..31) holds bits 0..31.</description></item>
/// </list>
/// </para>
///
/// <para><b>Extended sizes for tests.</b> The optional <c>extendedSize</c>
/// parameter on <see cref="AddFile"/> allows storing a file whose declared size
/// exceeds the actual data bytes — the cluster chain only carries
/// <c>data.Length</c> bytes but the directory entry reports the larger
/// extended size. This is the pragma testers use to exercise the 38-bit
/// encoding without writing actual >4 GiB payloads; a FAT+-aware reader will
/// stop at end-of-chain rather than invent missing bytes.</para>
/// </remarks>
public sealed class FatPlusWriter {

  private readonly List<(string Name, byte[]? Data, long ExtendedSize, long Size, Func<Stream>? Opener)> _files = [];

  /// <summary>
  /// Adds a file to the image.
  /// </summary>
  /// <param name="name">Filename (8.3 or long).</param>
  /// <param name="data">File data — must fit in a <see cref="byte"/>[].</param>
  /// <param name="extendedSize">Optional declared size to encode in the
  /// 38-bit FAT+ extended-size field. When negative or unset, defaults to
  /// <c>data.Length</c>. Use a value &gt; 4 GiB to exercise the upper
  /// 6 bits of NTRes for size-encoding tests.</param>
  public void AddFile(string name, byte[] data, long extendedSize = -1) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var size = extendedSize < 0 ? data.Length : extendedSize;
    if (size < 0 || size >= 1L << 38)
      throw new ArgumentOutOfRangeException(nameof(extendedSize),
        "FAT+ extended size must fit in 38 bits (0 .. 256 GiB − 1).");
    this._files.Add((name, data, size, data.LongLength, null));
  }

  /// <summary>
  /// Adds a file whose bytes are produced on demand. <paramref name="size" /> must
  /// match what <paramref name="openStream" /> yields; the layout is settled from
  /// it before a byte is read, so a payload past what a byte[] holds is placed
  /// like any other.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    if (size < 0 || size >= 1L << 38)
      throw new ArgumentOutOfRangeException(nameof(size),
        "FAT+ extended size must fit in 38 bits (0 .. 256 GiB - 1).");
    this._files.Add((name, null, size, size, openStream));
  }

  /// <summary>
  /// Builds the FAT+ image. Default size (200_000 sectors ≈ 100 MB) is chosen to
  /// land in the FAT32 cluster-count range without using unnecessary disk.
  /// </summary>
  /// <param name="totalSectors">Total sectors (default 200_000 ≈ 100 MB).
  /// Must be large enough to force the underlying writer into FAT32 mode
  /// (cluster count &gt; 65525).</param>
  /// <param name="bytesPerSector">Bytes per sector (default 512).</param>
  /// <param name="requestedClusterSize">Desired cluster size in bytes (0 = auto).</param>
  /// <param name="volumeLabel">Optional volume label (up to 11 chars, ASCII). Null = "NO NAME".
  /// Passed straight through to the underlying <see cref="FatWriter"/>; the dirent-size
  /// patch loop skips the volume-label entry so the FAT+ encoding is unaffected.</param>
  public byte[] Build(int totalSectors = 200_000, int bytesPerSector = 512, int requestedClusterSize = 0,
    string? volumeLabel = null) {
    // Delegate to FatWriter for the bulk of the work. We feed it the same files
    // (with their actual byte payloads) and then post-process the resulting image
    // to (1) patch the OEM signature and (2) patch per-file dirent NTRes + size.
    var inner = this.NewInnerWriter();
    // Force FAT32: FAT+ is a FAT32 extension, and the dirent-size patch below
    // assumes the FAT32 on-disk layout (root directory in the data area, located
    // via BPB_FATSz32 at offset 36). A small payload would otherwise let the
    // underlying writer pick FAT12/16 — whose root directory lives in a fixed
    // region the patch can't locate — so we pin FAT32 to keep the encoding valid.
    var image = inner.Build(totalSectors, bytesPerSector, requestedClusterSize,
      volumeLabel: volumeLabel, forcedFatType: 32);

    // (1) OEM signature → "FAT+    " in the primary boot sector AND its FAT32
    // backup copy (sector BPB_BkBootSec, conventionally 6). FatWriter cloned the
    // boot sector to the backup before this patch ran, so leaving the backup
    // un-patched makes fsck.fat report "differences between boot sector and its
    // backup" at offsets 3..10 — harmless but a genuine inconsistency. Mirror
    // the OEM bytes into the backup so the two sectors stay identical.
    FatPlusReader.OemSignature.CopyTo(image, 3);
    PatchBackupOem(image);

    // (2) Per-file dirent patch. Walk the root directory in order and patch each
    // short-name entry positionally — the underlying FatWriter writes dirents in
    // the same order files were added, so position N in our list matches the
    // N-th short-name entry on disk. For each entry: set the low 32 bits of size
    // in DIR_FileSize (offset 28) and the high 6 bits in the low 6 bits of
    // DIR_NTRes (offset 12), preserving the top 2 bits for NT case-flag use.
    var rootStart = LocateRootDirOffset(image);
    this.PatchDirentSizes(image, rootStart, image.Length);

    return image;
  }

  /// <summary>
  /// Builds the smallest FAT+ image that still holds all file data plus FAT
  /// overhead, while never dropping below the 200_000-sector floor that keeps
  /// the volume in FAT32 (the cluster-count range FAT+ extends). Prefer this
  /// over <see cref="Build"/> when the caller has not pinned an image size.
  /// </summary>
  /// <param name="requestedClusterSize">Desired cluster size in bytes (0 = auto).</param>
  /// <param name="bytesPerSector">Bytes per sector (default 512).</param>
  /// <param name="volumeLabel">Optional volume label (up to 11 chars, ASCII).</param>
  public byte[] BuildAutoSized(int requestedClusterSize = 0, int bytesPerSector = 512,
    string? volumeLabel = null) {
    // Size to the actual payload (data + ~50 % headroom for directory entries and
    // cluster-tail slack), but never below the FAT32 floor so the FAT+ extension
    // stays meaningful.
    return this.Build(this.PlanTotalSectors(bytesPerSector), bytesPerSector, requestedClusterSize, volumeLabel);
  }

  /// <summary>
  /// Sector count a volume needs to hold the files added: the payload plus ~50 %
  /// headroom for directory entries and cluster-tail slack, never below the FAT32
  /// floor so the FAT+ extension stays meaningful.
  /// </summary>
  public int PlanTotalSectors(int bytesPerSector = 512) {
    var totalDataBytes = this._files.Sum(f => f.Size);
    var neededBytes = totalDataBytes * 3 / 2 + 16L * 1024 * 1024;
    var neededSectors = (neededBytes + bytesPerSector - 1) / bytesPerSector;
    return (int)Math.Min(int.MaxValue, Math.Max(200_000L, neededSectors));
  }

  /// <summary>
  /// Picks the cluster size that minimises slack + FAT-table overhead for a
  /// <em>fixed</em> image size, mirroring <see cref="FatWriter.PickClusterForFixedImage"/>.
  /// FAT+ is always FAT32, so the FAT-type tier is fixed at 32. Returns 0 when no
  /// candidate fits (caller should fall back to the writer's default).
  /// </summary>
  /// <param name="totalSectors">The pinned image size in sectors.</param>
  /// <param name="bytesPerSector">Bytes per sector (default 512).</param>
  public int PickClusterForFixedImage(int totalSectors, int bytesPerSector = 512) {
    // FAT+ always produces a FAT32 volume; delegate to the FAT writer's fixed-image
    // optimiser with the FAT type forced to 32 so it never tries a smaller tier.
    var inner = this.NewInnerWriter();
    return inner.PickClusterForFixedImage(totalSectors, bytesPerSector,
      forcedFatType: 32, requestedRootEntries: 0, enableLfn: true);
  }

  /// <summary>
  /// Creates a <see cref="FatWriter"/> seeded with this writer's files (actual
  /// byte payloads only — the FAT+ extended sizes are patched into the image
  /// afterwards). Shared by <see cref="Build"/> and
  /// <see cref="PickClusterForFixedImage"/>.
  /// </summary>
  /// <summary>
  /// Streams a FAT+ volume to <paramref name="output" />, then applies the FAT+
  /// patches in place. The volume itself comes from <see cref="FatWriter.BuildTo" />,
  /// so free space stays sparse and the image is not bounded by what a byte[] holds.
  /// Only the boot sector and the root-directory region are read back.
  /// </summary>
  public void BuildTo(Stream output, int totalSectors, int bytesPerSector = 512,
    int requestedClusterSize = 0, string? volumeLabel = null) {
    ArgumentNullException.ThrowIfNull(output);
    var inner = this.NewInnerWriter();
    // FAT+ is always FAT32, and FAT32 needs at least 65525 clusters. Left on Auto,
    // the writer picks a cluster size for the payload and then grows the volume
    // until that floor is met -- a 40 MB payload came out as a 4 GB volume. Pick
    // the cluster size against the intended size instead, so the floor is met by
    // using smaller clusters rather than by inflating the image.
    if (requestedClusterSize <= 0) {
      const long fat32MinClusters = 65525 + 2048; // the floor plus the writer's margin
      var maxCluster = (long)totalSectors * bytesPerSector / fat32MinClusters;
      var picked = bytesPerSector;
      foreach (var candidate in new[] { 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536 })
        if (candidate >= bytesPerSector && candidate <= maxCluster) picked = candidate;
      requestedClusterSize = picked;
    }

    // BuildToStreaming rather than BuildTo: the latter only lays out the volume
    // and never post-fills the clusters of entries added as stream factories, so
    // every such file came back empty.
    inner.BuildToStreaming(output, bytesPerSector, requestedClusterSize,
      volumeLabel: volumeLabel, forcedFatType: 32, requestedTotalSectors: totalSectors);
    this.PatchFatPlusOnStream(output);
  }

  /// <summary>
  /// Streams an auto-sized FAT+ volume, sized to the files added, then applies the
  /// FAT+ patches in place.
  /// </summary>
  public void BuildToStreamingAutoSized(Stream output, int bytesPerSector = 512,
    int requestedClusterSize = 0, string? volumeLabel = null)
    => this.BuildTo(output, this.PlanTotalSectors(bytesPerSector), bytesPerSector,
                    requestedClusterSize, volumeLabel);

  /// <summary>Applies the FAT+ OEM signature and per-dirent extended sizes to a built volume.</summary>
  private void PatchFatPlusOnStream(Stream output) {
    var boot = new byte[512];
    output.Position = 0;
    output.ReadExactly(boot, 0, (int)Math.Min(boot.Length, output.Length));

    // (1) OEM signature in the primary boot sector and its FAT32 backup.
    var bps = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11));
    if (bps is 0 or > 4096) bps = 512;
    var backupSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(50));
    if (backupSector is 0 or > 64) backupSector = 6;

    output.Position = 3;
    output.Write(FatPlusReader.OemSignature);
    var backupOem = (long)backupSector * bps + 3;
    if (backupOem + FatPlusReader.OemSignature.Length <= output.Length) {
      output.Position = backupOem;
      output.Write(FatPlusReader.OemSignature);
    }

    // (2) Per-file dirent patch over the root-directory region. Two dirents per
    // file covers a long-name slot plus its short-name entry, and the loop stops
    // at the end-of-directory marker regardless.
    var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(14));
    var fatCount = boot[16] == 0 ? 2 : boot[16];
    var fatSize32 = BinaryPrimitives.ReadInt32LittleEndian(boot.AsSpan(36));
    var rootStart = ((long)reservedSectors + (long)fatCount * fatSize32) * bps;
    if (rootStart >= output.Length) return;

    var windowLen = (int)Math.Min(
      Math.Max(64 * 1024, (this._files.Count + 4) * 64),
      output.Length - rootStart);
    var window = new byte[windowLen];
    output.Position = rootStart;
    output.ReadExactly(window, 0, windowLen);

    this.PatchDirentSizes(window, 0, windowLen);

    output.Position = rootStart;
    output.Write(window, 0, windowLen);
    output.Flush();
  }

  private FatWriter NewInnerWriter() {
    var inner = new FatWriter();
    foreach (var (name, data, _, size, opener) in this._files)
      if (opener == null)
        inner.AddFile(name, data!);
      else
        inner.AddStreamingFile(name, size, opener);
    return inner;
  }

  /// <summary>
  /// Mirrors the <c>"FAT+    "</c> OEM signature into the FAT32 backup boot
  /// sector so it matches the primary. The backup lives at sector
  /// <c>BPB_BkBootSec</c> (offset 50, conventionally sector 6); only patched
  /// when that field is non-zero and the sector is in range.
  /// </summary>
  internal static void PatchBackupOem(byte[] image) {
    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11));
    if (bytesPerSector is 0 or > 4096) bytesPerSector = 512;
    var bkBootSec = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(50));
    if (bkBootSec == 0) return;
    var bkOff = bkBootSec * bytesPerSector;
    if (bkOff + 11 > image.Length) return;
    FatPlusReader.OemSignature.CopyTo(image.AsSpan(bkOff + 3));
  }

  /// <summary>
  /// Computes the offset of the FAT32 root directory in the freshly-built image.
  /// </summary>
  private static int LocateRootDirOffset(byte[] image) {
    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11));
    if (bytesPerSector is 0 or > 4096) bytesPerSector = 512;
    var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(14));
    var fatCount = image[16] == 0 ? 2 : image[16];
    var fatSize32 = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(36));
    var firstDataSector = reservedSectors + fatCount * fatSize32;
    return firstDataSector * bytesPerSector;
  }

  /// <summary>
  /// Walks the root-directory dirents in order and patches each short-name
  /// entry with the FAT+ extended-size encoding for the corresponding input
  /// file. Matches are positional — assumes <see cref="FatWriter"/> wrote
  /// entries in the order files were added.
  /// </summary>
  private void PatchDirentSizes(byte[] image, int rootStart, int imageLength) {
    var fileIdx = 0;
    var off = rootStart;
    while (off + 32 <= imageLength && fileIdx < this._files.Count) {
      var first = image[off];
      if (first == 0x00) break;             // end of directory
      if (first == 0xE5) { off += 32; continue; } // deleted slot
      var attr = image[off + 11];
      if ((attr & 0x3F) == 0x0F) { off += 32; continue; } // LFN slot
      if ((attr & 0x18) != 0) { off += 32; continue; }    // volume label / dir

      // This is a short-name file entry. Patch its size fields.
      var size = this._files[fileIdx].ExtendedSize;
      var sizeLo = (uint)(size & 0xFFFFFFFFu);
      var sizeHi = (byte)((size >> 32) & 0x3F); // 6 bits
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 28), sizeLo);
      // Top 2 bits of NTRes are reserved for Windows NT case flags — keep them
      // clear (FatWriter writes 0 here by default, so a straight assignment of
      // the 6-bit value is correct). The spec-conformant alternative is to
      // mask: image[off+12] = (byte)((image[off+12] & 0xC0) | sizeHi).
      image[off + 12] = (byte)((image[off + 12] & 0xC0) | sizeHi);

      ++fileIdx;
      off += 32;
    }
  }
}
