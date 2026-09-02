using Hawkynt.Algorithms.Checksums;

namespace Compression.Core.Checksums;

/// <summary>
/// Provides supported checksum-output size metadata for <see cref="Adler32"/>.
/// </summary>
public static class Adler32ChecksumSizeExtensions {
  extension(Adler32) {
    /// <summary>
    /// Gets the supported checksum-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits32;
  }
}
/// <summary>
/// Provides supported checksum-output size metadata for <see cref="Crc16"/>.
/// </summary>
public static class Crc16ChecksumSizeExtensions {
  extension(Crc16) {
    /// <summary>
    /// Gets the supported checksum-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits16;
  }
}
/// <summary>
/// Provides supported checksum-output size metadata for <see cref="Crc16Ccitt"/>.
/// </summary>
public static class Crc16CcittChecksumSizeExtensions {
  /// <summary>
  /// Gets the supported checksum-output sizes, in bits.
  /// </summary>
  public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits16;
}
/// <summary>
/// Provides supported checksum-output size metadata for <see cref="Crc32"/>.
/// </summary>
public static class Crc32ChecksumSizeExtensions {
  /// <summary>
  /// Gets the supported checksum-output sizes, in bits.
  /// </summary>
  public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits32;
}
/// <summary>
/// Provides supported checksum-output size metadata for <see cref="Crc64"/>.
/// </summary>
public static class Crc64ChecksumSizeExtensions {
  /// <summary>
  /// Gets the supported checksum-output sizes, in bits.
  /// </summary>
  public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits64;
}
