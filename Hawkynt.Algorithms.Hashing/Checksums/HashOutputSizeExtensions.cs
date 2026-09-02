using Hawkynt.Algorithms.Hashing;

namespace Compression.Core.Checksums;

/// <summary>
/// Provides supported hash-output size metadata for <see cref="Blake2b"/>.
/// </summary>
public static class Blake2bHashSizeExtensions {
  extension(Blake2b) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits8To512;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Md5"/>.
/// </summary>
public static class Md5HashSizeExtensions {
  extension(Md5) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits128;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Sha1"/>.
/// </summary>
public static class Sha1HashSizeExtensions {
  extension(Sha1) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits160;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="Sha256"/>.
/// </summary>
public static class Sha256HashSizeExtensions {
  extension(Sha256) {
    /// <summary>
    /// Gets the supported hash-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256;
  }
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="XxHash32"/>.
/// </summary>
public static class XxHash32HashSizeExtensions {
  /// <summary>
  /// Gets the supported hash-output sizes, in bits.
  /// </summary>
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits32;
}
/// <summary>
/// Provides supported hash-output size metadata for <see cref="XxHash64"/>.
/// </summary>
public static class XxHash64HashSizeExtensions {
  /// <summary>
  /// Gets the supported hash-output sizes, in bits.
  /// </summary>
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits64;
}
