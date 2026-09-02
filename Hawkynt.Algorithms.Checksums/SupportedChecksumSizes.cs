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
/// <summary>
/// Provides supported checksum-output size metadata for <see cref="Adler"/>.
/// </summary>
public static class AdlerChecksumSizeExtensions {
  extension(Adler) {
    /// <summary>
    /// Gets the supported checksum-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.EvenPowerOfTwoOrByteAligned;
  }
}
/// <summary>
/// Provides supported checksum-output size metadata for <see cref="Fletcher"/>.
/// </summary>
public static class FletcherChecksumSizeExtensions {
  extension(Fletcher) {
    /// <summary>
    /// Gets the supported checksum-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.EvenPowerOfTwoOrByteAligned;
  }
}
/// <summary>
/// Provides supported checksum-output size metadata for <see cref="BsdChecksum"/>.
/// </summary>
public static class BsdChecksumSizeExtensions {
  extension(BsdChecksum) {
    /// <summary>
    /// Gets the supported checksum-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits16;
  }
}
/// <summary>
/// Provides supported checksum-output size metadata for <see cref="SysVChecksum"/>.
/// </summary>
public static class SysVChecksumSizeExtensions {
  extension(SysVChecksum) {
    /// <summary>
    /// Gets the supported checksum-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits16;
  }
}
/// <summary>
/// Provides supported checksum-output size metadata for <see cref="SumChecksum"/>.
/// </summary>
public static class SumChecksumSizeExtensions {
  extension(SumChecksum) {
    /// <summary>
    /// Gets the supported checksum-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.PowerOfTwoOrByteAligned;
  }
}
/// <summary>
/// Provides supported checksum-output size metadata for <see cref="Lrc"/>.
/// </summary>
public static class LrcChecksumSizeExtensions {
  extension(Lrc) {
    /// <summary>
    /// Gets the supported checksum-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits8;
  }
}
/// <summary>
/// Provides supported checksum-output size metadata for <see cref="XorChecksum"/>.
/// </summary>
public static class XorChecksumSizeExtensions {
  extension(XorChecksum) {
    /// <summary>
    /// Gets the supported checksum-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits8;
  }
}
/// <summary>
/// Provides supported checksum-output size metadata for <see cref="InternetChecksum"/>.
/// </summary>
public static class InternetChecksumSizeExtensions {
  extension(InternetChecksum) {
    /// <summary>
    /// Gets the supported checksum-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits16;
  }
}
/// <summary>
/// Provides supported checksum-output size metadata for <see cref="ComplementChecksum"/>.
/// </summary>
public static class ComplementChecksumSizeExtensions {
  extension(ComplementChecksum) {
    /// <summary>
    /// Gets the supported checksum-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.PowerOfTwoOrByteAligned;
  }
}
/// <summary>
/// Provides supported checksum-output size metadata for <see cref="Parity"/>.
/// </summary>
public static class ParityChecksumSizeExtensions {
  extension(Parity) {
    /// <summary>
    /// Gets the supported checksum-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.PowerOfTwoOrByteAligned;
  }
}
/// <summary>
/// Provides supported checksum-output size metadata for <see cref="Nmea0183"/>.
/// </summary>
public static class Nmea0183ChecksumSizeExtensions {
  extension(Nmea0183) {
    /// <summary>
    /// Gets the supported checksum-output sizes, in bits.
    /// </summary>
    public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits8;
  }
}
/// <summary>
/// Provides supported checksum-output size metadata for <see cref="Crc"/>.
/// </summary>
public static class CrcChecksumSizeExtensions {
  /// <summary>
  /// Gets the supported checksum-output sizes, in bits.
  /// </summary>
  public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits8To64;
}
/// <summary>
/// Provides supported checksum-output size metadata for <see cref="Crc128"/>.
/// </summary>
public static class Crc128ChecksumSizeExtensions {
  /// <summary>
  /// Gets the supported checksum-output sizes, in bits.
  /// </summary>
  public static IReadOnlyList<ChecksumSizeRange> SupportedChecksumSizes => ChecksumSizeSets.Bits128;
}
