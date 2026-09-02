#pragma warning disable CS1591
namespace FileFormat.Ypf;

/// <summary>
/// Represents a ypf constants.
/// </summary>
public static class YpfConstants {
  // 4-byte ASCII+NUL magic at offset 0 of the file. The trailing NUL is part of the magic
  // (not a string terminator) — real YukaScript engines compare all four bytes.
    /// <summary>
  /// Provides the magic value.
  /// </summary>
public static readonly byte[] Magic = [0x59, 0x50, 0x46, 0x00];

  // 32-byte fixed-size file header: magic(4) + version(4) + entryCount(4) + tableSize(4) + reserved(16).
    /// <summary>
  /// Defines the header size constant value.
  /// </summary>
public const int HeaderSize = 32;

  // Reserved region following the four populated 32-bit fields. Spec calls it "unused";
  // we write zeros and ignore on read so existing files with non-zero fingerprints still load.
    /// <summary>
  /// Defines the reserved size constant value.
  /// </summary>
public const int ReservedSize = 16;

  // We target only YPF v480 — the version used by Yu-No remake, Iyashi no Megami no Marmot,
  // and most modern YukaScript-based VNs. Older revisions (often <300) used a different
  // entry-record layout we intentionally don't support.
    /// <summary>
  /// Defines the supported version constant value.
  /// </summary>
public const uint SupportedVersion = 480;

  // Standard CRC-32 (IEEE 802.3 / zlib) reflected polynomial. YPF stores this CRC over the
  // COMPRESSED bytes of each entry — same convention as PSF, common foot-gun if a writer
  // accidentally CRCs the uncompressed payload instead.
    /// <summary>
  /// Defines the crc 32 polynomial constant value.
  /// </summary>
public const uint Crc32Polynomial = 0xEDB88320u;

  // Per-entry compression flag values.
    /// <summary>
  /// Defines the compression stored constant value.
  /// </summary>
public const byte CompressionStored = 0;
    /// <summary>
  /// Defines the compression zlib constant value.
  /// </summary>
public const byte CompressionZlib = 1;

  // Per-entry type byte. 0 = unspecified/binary; the engine uses 1/2/3 to hint scripts/pictures/sounds.
  // We always write 0 because the engine doesn't require correct typing for extraction.
    /// <summary>
  /// Defines the type unspecified constant value.
  /// </summary>
public const byte TypeUnspecified = 0;
}
