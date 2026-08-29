using Compression.Core.Checksums;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>
/// Registry name for the Esch256/SPARKLE-384 construction.
/// The JavaScript registry implementation is byte-for-byte the Esch256 algorithm.
/// </summary>
public static class SparkleHash {
  public static byte[] Compute(ReadOnlySpan<byte> data) => Esch256.Compute(data);
}

/// <summary>Generic xxHash registry facade. The registry defaults to xxHash32 and optionally exposes xxHash64.</summary>
public static class XxHash {
  public static uint Compute32(ReadOnlySpan<byte> data, uint seed = 0) => XxHash32.Compute(data, seed);
  public static ulong Compute64(ReadOnlySpan<byte> data, ulong seed = 0) => XxHash64.Compute(data, seed);
}
