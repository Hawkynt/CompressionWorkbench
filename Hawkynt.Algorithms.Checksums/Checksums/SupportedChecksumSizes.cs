using Hawkynt.Algorithms.Checksums;

namespace Compression.Core.Checksums;

public static class Adler32ChecksumSizeExtensions { extension(Adler32) { public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits32; } }
public static class Crc16ChecksumSizeExtensions { extension(Crc16) { public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits16; } }
public static class Crc16CcittChecksumSizeExtensions { extension(Crc16Ccitt) { public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits16; } }
public static class Crc32ChecksumSizeExtensions { extension(Crc32) { public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits32; } }
public static class Crc64ChecksumSizeExtensions { extension(Crc64) { public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits64; } }
