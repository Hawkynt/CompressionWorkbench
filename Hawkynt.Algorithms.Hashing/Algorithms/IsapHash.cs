namespace Hawkynt.Algorithms.Hashing;

/// <summary>
/// ISAP Hash. The registry's ISAP digest uses the same precomputed Ascon-Hash
/// state, 64-bit rate, P12 absorption, padding, and squeezing as ASCON-HASH.
/// </summary>
public static class IsapHash {
  /// <summary>
  /// Computes the Isap Hash hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data) => AsconHash.Compute(data);
}
