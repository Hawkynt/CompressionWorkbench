namespace Hawkynt.Algorithms.Hashing;

/// <summary>Grøstl hash family with a single implementation selected by output size.</summary>
public static class Groestl {
  /// <summary>
  /// Gets the supported hash-output sizes, in bits.
  /// </summary>
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes { get; } = [
    new(224, 256, 32),
    new(384, 512, 128)
  ];

  /// <summary>
  /// Computes the Groestl hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data, int hashSizeBits = 256) {
    if (!SupportedHashSizes.Supports(hashSizeBits))
      throw new ArgumentOutOfRangeException(nameof(hashSizeBits));
    return GroestlCore.Compute(data, hashSizeBits / 8);
  }
}

/// <summary>Streebog (GOST R 34.11-2012) hash family with one implementation for both standard sizes.</summary>
public static class Streebog {
  /// <summary>
  /// Gets the supported hash-output sizes, in bits.
  /// </summary>
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes { get; } = [
    new(256, 512, 256)
  ];

  /// <summary>
  /// Computes the Streebog hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data, int hashSizeBits = 512) {
    if (!SupportedHashSizes.Supports(hashSizeBits))
      throw new ArgumentOutOfRangeException(nameof(hashSizeBits));
    return StreebogCore.Compute(data, hashSizeBits / 8);
  }
}
