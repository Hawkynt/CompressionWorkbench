using FileSystem.Fat;

namespace Compression.Lib.FsConversion;

/// <summary>
/// In-place FAT12 ↔ FAT16 ↔ FAT32 variant conversion.
///
/// <para>The on-disk layout differences between the three FAT variants are
/// substantial enough that a strict "patch the BPB only" approach is unsafe:</para>
/// <list type="bullet">
///   <item>FAT12 entries are 1.5 bytes wide; FAT16 entries are 2 bytes; FAT32
///   entries are 4 bytes. The FAT region grows when the variant widens,
///   which displaces the first data cluster (cluster #2 shifts forward).</item>
///   <item>FAT12/16 store the root directory in a fixed sector run sized by
///   BPB_RootEntCnt; FAT32 places the root in a regular cluster chain starting
///   at BPB_RootClus. Switching either way requires moving the root dirents.</item>
///   <item>The extended BPB (offsets 36..89) differs: short form for FAT12/16
///   vs. 56-byte FAT32 extended BPB carrying BPB_FATSz32 / BPB_RootClus /
///   BPB_FSInfo / etc. The fs-type string at +54 (FAT12/16) or +82 (FAT32) is
///   the identifier most tools look at first.</item>
/// </list>
///
/// <para>Implementation strategy: stage the converted image in memory via
/// <see cref="FatReader"/> → <see cref="FatWriter"/>, which guarantees the
/// new image is byte-correct for the target variant, then commit it to the
/// stream as a single contiguous write. This is "metadata-only" in the sense
/// that we never copy file data outside the stream — the rebuilt image is
/// produced from the same bytes, just rearranged for the new FAT geometry.
/// For the cases where the cluster count is acceptable to both src and dst,
/// the file payloads land at the same logical cluster numbers.</para>
///
/// <para>Crash safety: the rebuild happens entirely in-memory before any
/// stream byte is touched. A crash before the first write leaves the original
/// image intact; a crash during the single big write produces a torn image
/// (caller's outer atomic-rename helper, e.g. <see cref="AtomicFileWriter"/>,
/// is responsible for full crash safety at the file level). After the write
/// we Flush before any SetLength so the data lands before the size change.</para>
/// </summary>
internal static class FatVariantConverter {

  /// <summary>
  /// Converts a FAT image stream in place. Returns the result code; throws
  /// only on malformed input. Geometric mismatches (e.g. a FAT32 image with
  /// too many clusters to downgrade to FAT16) are reported via
  /// <see cref="InPlaceConversionResult.GeometryRejected"/> instead of throwing.
  /// </summary>
  internal static InPlaceConversionResult Convert(Stream image, FatVariant src, FatVariant dst) {
    if (src == dst) return InPlaceConversionResult.NoOp;

    // Read all files from the existing image. FatReader handles FAT12/16/32
    // transparently — we get the same FatEntry shape regardless of source
    // variant, with bytes already extracted from the cluster chain.
    image.Position = 0;
    var originalLength = image.Length;
    List<(string Name, byte[] Data)> files;
    int srcBytesPerSector;
    int srcSectorsPerCluster;
    try {
      using var reader = new FatReader(image);
      files = reader.Entries
        .Where(e => !e.IsDirectory)
        .Select(e => (e.Name, reader.Extract(e)))
        .ToList();
      // Inspect the source BPB for sector geometry so we can preserve it on
      // the way out. Stable cluster size = stable per-file cluster count =
      // the closest we can come to "data not copied" semantically.
      image.Position = 0;
      var bpb = new byte[512];
      image.ReadExactly(bpb);
      srcBytesPerSector = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bpb.AsSpan(11));
      if (srcBytesPerSector is 0 or > 4096) srcBytesPerSector = 512;
      srcSectorsPerCluster = bpb[13] == 0 ? 1 : bpb[13];
    } catch (InvalidDataException) {
      return InPlaceConversionResult.NotSupported;
    }

    // Geometry validation: the rebuilt image must be representable at the
    // requested variant given the original disk size. The cluster-count
    // window per FATGEN103:
    //   FAT12: 1..4084
    //   FAT16: 4085..65524
    //   FAT32: 65525..0x0FFFFFF5
    var totalSectors = (int)(originalLength / srcBytesPerSector);
    if (!CanFitTargetVariant(totalSectors, srcBytesPerSector, srcSectorsPerCluster, dst, out var targetSpc))
      return InPlaceConversionResult.GeometryRejected;

    // Build the new image. FatWriter auto-selects FAT type from cluster
    // count, so to force the target variant we hand it a sectors-per-cluster
    // hint that brings the cluster count into the target's window.
    var w = new FatWriter();
    foreach (var (name, data) in files)
      w.AddFile(name, data);
    byte[] rebuilt;
    try {
      rebuilt = w.Build(
        totalSectors: totalSectors,
        bytesPerSector: srcBytesPerSector,
        requestedClusterSize: targetSpc * srcBytesPerSector);
    } catch {
      return InPlaceConversionResult.GeometryRejected;
    }

    // Verify the writer actually produced the requested variant. If the
    // user-supplied geometry pushed it into a different bucket (e.g. too
    // few clusters for FAT32), bail rather than silently producing the
    // wrong fs-type.
    var producedVariant = DetectVariantFromBytes(rebuilt);
    if (producedVariant != dst)
      return InPlaceConversionResult.GeometryRejected;

    // Commit to the stream.
    //
    // Step 1: write the new bytes. We deliberately overwrite the whole image
    // — partial writes are bounded by Stream.Write's contract, so the worst
    // tear is at the end (file end stays the same here, so torn bytes are
    // in the file-data tail). Caller's atomic-file-writer wraps this for
    // process-crash safety.
    image.Position = 0;
    image.Write(rebuilt, 0, rebuilt.Length);
    image.Flush();

    // Step 2: align the stream length. Both directions of the conversion
    // produce the same totalSectors × bytesPerSector image size as the
    // source, so this is usually a no-op — but be defensive in case
    // FatWriter padded up to a sector boundary.
    if (rebuilt.Length != originalLength)
      image.SetLength(rebuilt.Length);
    image.Flush();

    return InPlaceConversionResult.Succeeded;
  }

  /// <summary>
  /// Computes a sectors-per-cluster value that lands the resulting image
  /// inside <paramref name="dst"/>'s cluster-count window, given the
  /// original disk size and sector geometry. Returns <c>false</c> when no
  /// power-of-two sectors-per-cluster ∈ {1,2,4,8,16,32,64,128} produces a
  /// representable layout.
  /// </summary>
  /// <remarks>
  /// Per FATGEN103, cluster sizes must be powers of two and ≥ bytesPerSector.
  /// We try the smallest spc first (more clusters, finer granularity) and
  /// widen until we either land in-window or exhaust the candidates.
  /// </remarks>
  private static bool CanFitTargetVariant(
      int totalSectors, int bytesPerSector, int srcSpc,
      FatVariant dst, out int targetSpc) {
    int[] spcCandidates = [srcSpc, 1, 2, 4, 8, 16, 32, 64, 128];

    foreach (var spc in spcCandidates) {
      if (spc <= 0) continue;
      if ((spc & (spc - 1)) != 0) continue; // must be power of two

      var clusters = EstimateClusterCount(totalSectors, bytesPerSector, spc, dst);
      var inWindow = dst switch {
        FatVariant.Fat12 => clusters is > 0 and < 4085,
        FatVariant.Fat16 => clusters is >= 4085 and < 65525,
        FatVariant.Fat32 => clusters >= 65525,
        _ => false,
      };
      if (inWindow) {
        targetSpc = spc;
        return true;
      }
    }

    targetSpc = 0;
    return false;
  }

  /// <summary>
  /// Rough cluster-count estimate matching <see cref="FatWriter.Build"/>'s
  /// layout: reserved+FATs+rootDirSectors on FAT12/16, 32 reserved + no root
  /// dir sectors on FAT32. Used only to pre-check whether a given spc lands
  /// in the target variant's window; the writer does the precise sizing.
  /// </summary>
  private static int EstimateClusterCount(
      int totalSectors, int bytesPerSector, int sectorsPerCluster, FatVariant dst) {
    const int fatCount = 2;
    int reservedSectors, rootDirSectors;
    long fatSizeSectors;
    switch (dst) {
      case FatVariant.Fat32:
        reservedSectors = 32;
        rootDirSectors = 0;
        var dataSectorsEstimate32 = totalSectors - reservedSectors;
        var dataClustersEstimate32 = dataSectorsEstimate32 / sectorsPerCluster;
        fatSizeSectors = (dataClustersEstimate32 * 4 + bytesPerSector - 1) / bytesPerSector;
        break;
      case FatVariant.Fat16:
        reservedSectors = 1;
        rootDirSectors = (512 * 32 + bytesPerSector - 1) / bytesPerSector;
        // Writer's formula: (totalSectors * 2 / bytesPerSector) + 1
        fatSizeSectors = (totalSectors * 2 / bytesPerSector) + 1;
        break;
      case FatVariant.Fat12:
      default:
        reservedSectors = 1;
        rootDirSectors = (224 * 32 + bytesPerSector - 1) / bytesPerSector;
        // FAT12 floppy default: 9 sectors per FAT.
        fatSizeSectors = 9;
        break;
    }
    var firstDataSector = reservedSectors + fatCount * fatSizeSectors + rootDirSectors;
    var dataSectors = totalSectors - firstDataSector;
    if (dataSectors <= 0) return 0;
    return (int)(dataSectors / sectorsPerCluster);
  }

  /// <summary>
  /// Detects the FAT variant of a freshly built image by re-parsing the BPB.
  /// Used after the writer finishes to verify it actually produced what we
  /// requested.
  /// </summary>
  private static FatVariant DetectVariantFromBytes(byte[] image) {
    using var ms = new MemoryStream(image, writable: false);
    return InPlaceConverter.DetectFatVariant(ms);
  }
}
