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

  private readonly List<(string Name, byte[] Data, long ExtendedSize)> _files = [];

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
    this._files.Add((name, data, size));
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
  public byte[] Build(int totalSectors = 200_000, int bytesPerSector = 512, int requestedClusterSize = 0) {
    // Delegate to FatWriter for the bulk of the work. We feed it the same files
    // (with their actual byte payloads) and then post-process the resulting image
    // to (1) patch the OEM signature and (2) patch per-file dirent NTRes + size.
    var inner = new FatWriter();
    foreach (var (name, data, _) in this._files)
      inner.AddFile(name, data);
    var image = inner.Build(totalSectors, bytesPerSector, requestedClusterSize);

    // (1) OEM signature → "FAT+    ".
    FatPlusReader.OemSignature.CopyTo(image, 3);

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
