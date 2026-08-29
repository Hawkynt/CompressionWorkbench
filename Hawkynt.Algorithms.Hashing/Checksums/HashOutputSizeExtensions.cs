using Hawkynt.Algorithms.Hashing;

namespace Compression.Core.Checksums;

/// <summary>Output-size metadata for hash APIs retained in the historical checksum namespace.</summary>
public static class HashOutputSizeExtensions {
  extension(Blake2b) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits8To512; }
  extension(Md5) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits128; }
  extension(Sha1) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits160; }
  extension(Sha256) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; }
  extension(XxHash32) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits32; }
  extension(XxHash64) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits64; }
}
