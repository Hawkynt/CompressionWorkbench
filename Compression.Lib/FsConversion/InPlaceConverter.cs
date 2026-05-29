using System.Buffers.Binary;

namespace Compression.Lib.FsConversion;

/// <summary>
/// FAT variant identifier — what flavour of FAT a given image uses or should
/// become. Mapped from BPB-derived cluster count (FAT12 &lt; 4085 clusters,
/// FAT16 &lt; 65525 clusters, FAT32 otherwise).
/// </summary>
public enum FatVariant {
  /// <summary>1.5-byte FAT entries, fixed root directory.</summary>
  Fat12,
  /// <summary>2-byte FAT entries, fixed root directory.</summary>
  Fat16,
  /// <summary>4-byte FAT entries, root directory in cluster chain.</summary>
  Fat32,
}

/// <summary>
/// ext version identifier — selects which feature bits a given image carries
/// or should carry. The on-disk inode layout stays compatible across all
/// three: only journal presence and the extent-tree flag distinguish them.
/// </summary>
public enum ExtVersion {
  /// <summary>No journal, no extents — bare ext2.</summary>
  Ext2,
  /// <summary>Journal inode (reserved inode 8) + COMPAT_HAS_JOURNAL bit.</summary>
  Ext3,
  /// <summary>ext3 + INCOMPAT_EXTENTS bit (existing files keep block pointers).</summary>
  Ext4,
}

/// <summary>
/// Result of an in-place filesystem-variant conversion request — reports
/// whether the metadata-only path succeeded, and if not why the caller should
/// fall back to a full migration (extract → reformat → re-import).
/// </summary>
public enum InPlaceConversionResult {
  /// <summary>Conversion completed via metadata-only edits.</summary>
  Succeeded,
  /// <summary>Source already matches target variant — nothing to do.</summary>
  NoOp,
  /// <summary>Source/target pair has no metadata-only path; caller must migrate.</summary>
  NotSupported,
  /// <summary>Pair is supported in principle but the specific image's geometry
  /// (e.g. cluster count out of target's range, no free space for the new FAT)
  /// rules out a metadata-only conversion.</summary>
  GeometryRejected,
}

/// <summary>
/// Orchestrator for filesystem-variant conversions that only need metadata
/// rewrites — no file data is copied. Examples:
/// <list type="bullet">
///   <item>FAT12 ↔ FAT16 ↔ FAT32 (FAT entry width changes; BPB fs-type string
///   and FAT region size are recomputed).</item>
///   <item>ext2 → ext3 (add reserved journal inode #8 + COMPAT_HAS_JOURNAL).</item>
///   <item>ext3 → ext4 (set INCOMPAT_EXTENTS; legacy files keep block pointers).</item>
/// </list>
/// All operations are crash-safe: each step is a targeted write followed by
/// a <see cref="Stream.Flush"/> barrier so partial completions leave the image
/// in a recoverable state (either fully old or fully new).
/// </summary>
public static class InPlaceConverter {

  /// <summary>
  /// Detects which FAT variant the image at the current stream position uses
  /// by parsing the BPB and computing cluster count per FATGEN103 §3.5.
  /// </summary>
  /// <exception cref="InvalidDataException">The image is too small or the
  /// boot signature / BPB fields are not a recognisable FAT layout.</exception>
  public static FatVariant DetectFatVariant(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Length < 512)
      throw new InvalidDataException("FAT: image too small for a BPB.");

    Span<byte> bpb = stackalloc byte[512];
    image.Position = 0;
    image.ReadExactly(bpb);

    if (bpb[0] != 0xEB && bpb[0] != 0xE9 && bpb[0] != 0x00)
      throw new InvalidDataException("FAT: invalid boot jump.");

    var bytesPerSector = (int)BinaryPrimitives.ReadUInt16LittleEndian(bpb[11..]);
    if (bytesPerSector is 0 or > 4096) bytesPerSector = 512;
    var sectorsPerCluster = bpb[13] == 0 ? 1 : (int)bpb[13];
    var reservedSectors = (int)BinaryPrimitives.ReadUInt16LittleEndian(bpb[14..]);
    var fatCount = bpb[16] == 0 ? 2 : (int)bpb[16];
    var rootEntryCount = (int)BinaryPrimitives.ReadUInt16LittleEndian(bpb[17..]);

    var totalSectors16 = (int)BinaryPrimitives.ReadUInt16LittleEndian(bpb[19..]);
    var totalSectors = totalSectors16 == 0
      ? BinaryPrimitives.ReadInt32LittleEndian(bpb[32..])
      : totalSectors16;

    var fatSize16 = (int)BinaryPrimitives.ReadUInt16LittleEndian(bpb[22..]);
    var fatSize = fatSize16 == 0
      ? BinaryPrimitives.ReadInt32LittleEndian(bpb[36..])
      : fatSize16;

    var rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
    var firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
    var totalDataClusters = (totalSectors - firstDataSector) / sectorsPerCluster;

    return totalDataClusters < 4085 ? FatVariant.Fat12
      : totalDataClusters < 65525 ? FatVariant.Fat16
      : FatVariant.Fat32;
  }

  /// <summary>
  /// Detects ext version by reading the feature flags from the superblock at
  /// offset 1024. Distinguishes ext2 (no journal + no extents), ext3 (journal
  /// only), and ext4 (extents flag set, with or without journal).
  /// </summary>
  /// <exception cref="InvalidDataException">Magic at offset 1080 is not 0xEF53.</exception>
  public static ExtVersion DetectExtVersion(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Length < 1024 + 264)
      throw new InvalidDataException("ext: image too small for superblock.");

    Span<byte> sb = stackalloc byte[264];
    image.Position = 1024;
    image.ReadExactly(sb);

    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb[56..]);
    if (magic != 0xEF53)
      throw new InvalidDataException($"ext: invalid magic 0x{magic:X4}, expected 0xEF53.");

    var featureCompat = BinaryPrimitives.ReadUInt32LittleEndian(sb[92..]);
    var featureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(sb[96..]);

    var hasJournal = (featureCompat & ExtVersionConverter.FeatureCompatHasJournal) != 0;
    var hasExtents = (featureIncompat & ExtVersionConverter.FeatureIncompatExtents) != 0;

    return hasExtents ? ExtVersion.Ext4
      : hasJournal ? ExtVersion.Ext3
      : ExtVersion.Ext2;
  }

  /// <summary>
  /// Converts a FAT filesystem image from <paramref name="src"/> to
  /// <paramref name="dst"/> variant in place. Reads existing files into memory,
  /// rebuilds the image with the new FAT entry width / root-dir layout, and
  /// writes it back. File data bytes themselves are preserved verbatim; only
  /// the reserved metadata region (BPB, FAT, root dir) is reshaped.
  ///
  /// <para>Returns <see cref="InPlaceConversionResult.NoOp"/> when src == dst,
  /// <see cref="InPlaceConversionResult.GeometryRejected"/> when the resulting
  /// cluster count would fall outside the target variant's legal range, and
  /// <see cref="InPlaceConversionResult.Succeeded"/> on success.</para>
  /// </summary>
  /// <remarks>
  /// Crash safety: the conversion stages a complete new image in memory before
  /// any bytes are written back, so a crash during the rebuild leaves the old
  /// image intact. Once the write begins, a flush is issued after the data is
  /// committed but before <see cref="Stream.SetLength"/> truncates / extends —
  /// see <see cref="FatVariantConverter.Convert"/> for the per-step rationale.
  /// </remarks>
  public static InPlaceConversionResult ConvertFatVariant(Stream image, FatVariant src, FatVariant dst) {
    ArgumentNullException.ThrowIfNull(image);
    if (src == dst) return InPlaceConversionResult.NoOp;
    return FatVariantConverter.Convert(image, src, dst);
  }

  /// <summary>
  /// Converts an ext-family filesystem image from <paramref name="src"/> to
  /// <paramref name="dst"/> in place. Only metadata bytes are touched — every
  /// data block stays at its original offset and every existing inode
  /// continues to use its original layout (block pointers or extents).
  ///
  /// <para>Supported pairs (forward only — downgrades return
  /// <see cref="InPlaceConversionResult.NotSupported"/> because the journal
  /// inode and extents flag cannot be silently dropped without a full rebuild):</para>
  /// <list type="bullet">
  ///   <item>ext2 → ext3: allocate journal inode #8, set HAS_JOURNAL.</item>
  ///   <item>ext2 → ext4: ext2 → ext3 → ext4 chained.</item>
  ///   <item>ext3 → ext4: set INCOMPAT_EXTENTS (existing files unchanged).</item>
  /// </list>
  /// </summary>
  public static InPlaceConversionResult ConvertExtVersion(Stream image, ExtVersion src, ExtVersion dst) {
    ArgumentNullException.ThrowIfNull(image);
    if (src == dst) return InPlaceConversionResult.NoOp;
    return ExtVersionConverter.Convert(image, src, dst);
  }

  /// <summary>
  /// High-level entry point used by <c>PartitionEditor.ConvertFilesystem</c>.
  /// Looks at the source and target format-ids (as registered with
  /// <c>FormatRegistry</c>) and dispatches to the right per-family converter.
  /// Returns <see cref="InPlaceConversionResult.NotSupported"/> if no in-place
  /// path exists for the given pair — the caller is expected to fall back to
  /// a full extract-then-format migration.
  /// </summary>
  /// <param name="image">Partition-scoped stream (read+write+seek).</param>
  /// <param name="sourceFsId">Source filesystem id. Recognised values: "Fat",
  /// "Fat12", "Fat16", "Fat32", "Ext", "Ext2", "Ext3", "Ext4". For "Fat" the
  /// source variant is auto-detected from the BPB.</param>
  /// <param name="targetFsId">Target filesystem id (same recognised set).</param>
  public static InPlaceConversionResult TryConvert(Stream image, string sourceFsId, string targetFsId) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentException.ThrowIfNullOrEmpty(sourceFsId);
    ArgumentException.ThrowIfNullOrEmpty(targetFsId);

    var srcFamily = ClassifyFamily(sourceFsId);
    var dstFamily = ClassifyFamily(targetFsId);
    if (srcFamily != dstFamily || srcFamily == FsFamily.Unknown)
      return InPlaceConversionResult.NotSupported;

    if (srcFamily == FsFamily.Fat) {
      var srcVariant = ResolveFatVariant(sourceFsId, image);
      var dstVariant = ResolveFatVariant(targetFsId, image: null);
      if (srcVariant is null || dstVariant is null) return InPlaceConversionResult.NotSupported;
      return ConvertFatVariant(image, srcVariant.Value, dstVariant.Value);
    }

    if (srcFamily == FsFamily.Ext) {
      var srcVersion = ResolveExtVersion(sourceFsId, image);
      var dstVersion = ResolveExtVersion(targetFsId, image: null);
      if (srcVersion is null || dstVersion is null) return InPlaceConversionResult.NotSupported;
      return ConvertExtVersion(image, srcVersion.Value, dstVersion.Value);
    }

    return InPlaceConversionResult.NotSupported;
  }

  // ── Family classification ─────────────────────────────────────────────

  private enum FsFamily { Unknown, Fat, Ext }

  private static FsFamily ClassifyFamily(string id) {
    if (id.StartsWith("Fat", StringComparison.OrdinalIgnoreCase)) return FsFamily.Fat;
    if (id.StartsWith("Ext", StringComparison.OrdinalIgnoreCase)) return FsFamily.Ext;
    return FsFamily.Unknown;
  }

  private static FatVariant? ResolveFatVariant(string id, Stream? image) {
    if (id.Equals("Fat12", StringComparison.OrdinalIgnoreCase)) return FatVariant.Fat12;
    if (id.Equals("Fat16", StringComparison.OrdinalIgnoreCase)) return FatVariant.Fat16;
    if (id.Equals("Fat32", StringComparison.OrdinalIgnoreCase)) return FatVariant.Fat32;
    if (id.Equals("Fat", StringComparison.OrdinalIgnoreCase) && image is not null)
      return DetectFatVariant(image);
    // Generic "Fat" without image context is ambiguous — caller must pick a
    // concrete variant.
    return null;
  }

  private static ExtVersion? ResolveExtVersion(string id, Stream? image) {
    if (id.Equals("Ext2", StringComparison.OrdinalIgnoreCase)) return ExtVersion.Ext2;
    if (id.Equals("Ext3", StringComparison.OrdinalIgnoreCase)) return ExtVersion.Ext3;
    if (id.Equals("Ext4", StringComparison.OrdinalIgnoreCase)) return ExtVersion.Ext4;
    if (id.Equals("Ext", StringComparison.OrdinalIgnoreCase) && image is not null)
      return DetectExtVersion(image);
    return null;
  }
}
