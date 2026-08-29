namespace Hawkynt.Algorithms.Checksums;

internal static class ChecksumSizeSets {
  internal const int MaximumByteAlignedBits = int.MaxValue & ~7;

  internal static readonly IReadOnlyList<ChecksumSizeRange> Bits8 = [ChecksumSizeRange.Exact(8)];
  internal static readonly IReadOnlyList<ChecksumSizeRange> Bits16 = [ChecksumSizeRange.Exact(16)];
  internal static readonly IReadOnlyList<ChecksumSizeRange> Bits32 = [ChecksumSizeRange.Exact(32)];
  internal static readonly IReadOnlyList<ChecksumSizeRange> Bits64 = [ChecksumSizeRange.Exact(64)];
  internal static readonly IReadOnlyList<ChecksumSizeRange> Bits128 = [ChecksumSizeRange.Exact(128)];
  internal static readonly IReadOnlyList<ChecksumSizeRange> PowerOfTwoOrByteAligned = [
    ChecksumSizeRange.Exact(1),
    ChecksumSizeRange.Exact(2),
    ChecksumSizeRange.Exact(4),
    new(8, MaximumByteAlignedBits, 8)
  ];
  internal static readonly IReadOnlyList<ChecksumSizeRange> EvenPowerOfTwoOrByteAligned = [
    ChecksumSizeRange.Exact(2),
    ChecksumSizeRange.Exact(4),
    new(8, MaximumByteAlignedBits, 8)
  ];
  internal static readonly IReadOnlyList<ChecksumSizeRange> Bits8To64 = [new(8, 64)];
}

// Static extension members provide a uniform Type.SupportedChecksumSizes surface without
// multiplying implementation wrappers. Each receiver has its own containing class because
// C# lowers static extension properties to parameterless getter methods.
public static class AdlerChecksumSizeExtensions { extension(Adler) { public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.EvenPowerOfTwoOrByteAligned; } }
public static class FletcherChecksumSizeExtensions { extension(Fletcher) { public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.EvenPowerOfTwoOrByteAligned; } }
public static class BsdChecksumSizeExtensions { extension(BsdChecksum) { public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits16; } }
public static class SysVChecksumSizeExtensions { extension(SysVChecksum) { public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits16; } }
public static class SumChecksumSizeExtensions { extension(SumChecksum) { public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.PowerOfTwoOrByteAligned; } }
public static class LrcChecksumSizeExtensions { extension(Lrc) { public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits8; } }
public static class XorChecksumSizeExtensions { extension(XorChecksum) { public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits8; } }
public static class InternetChecksumSizeExtensions { extension(InternetChecksum) { public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits16; } }
public static class ComplementChecksumSizeExtensions { extension(ComplementChecksum) { public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.PowerOfTwoOrByteAligned; } }
public static class ParityChecksumSizeExtensions { extension(Parity) { public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.PowerOfTwoOrByteAligned; } }
public static class Nmea0183ChecksumSizeExtensions { extension(Nmea0183) { public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits8; } }
public static class CrcChecksumSizeExtensions { extension(Crc) { public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits8To64; } }
public static class Crc128ChecksumSizeExtensions { extension(Crc128) { public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits128; } }
