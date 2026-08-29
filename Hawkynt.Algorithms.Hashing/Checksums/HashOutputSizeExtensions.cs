using Hawkynt.Algorithms.Hashing;

namespace Compression.Core.Checksums;

public static class Blake2bHashSizeExtensions { extension(Blake2b) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits8To512; } }
public static class Md5HashSizeExtensions { extension(Md5) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits128; } }
public static class Sha1HashSizeExtensions { extension(Sha1) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits160; } }
public static class Sha256HashSizeExtensions { extension(Sha256) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits256; } }
public static class XxHash32HashSizeExtensions { extension(XxHash32) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits32; } }
public static class XxHash64HashSizeExtensions { extension(XxHash64) { public static IReadOnlyList<HashSizeRange> SupportedHashSizes => HashSizeSets.Bits64; } }
