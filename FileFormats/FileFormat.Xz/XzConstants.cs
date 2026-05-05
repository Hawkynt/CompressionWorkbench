namespace FileFormat.Xz;

/// <summary>
/// Constants for the XZ file format.
/// </summary>
internal static class XzConstants {
  /// <summary>6-byte stream header magic: \xFD7zXZ\0.</summary>
  public static ReadOnlySpan<byte> StreamHeaderMagic => [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];

  /// <summary>2-byte stream footer magic: "YZ".</summary>
  public const ushort StreamFooterMagic = 0x5A59;

  /// <summary>No integrity check.</summary>
  public const byte CheckNone = 0x00;

  /// <summary>CRC-32 integrity check.</summary>
  public const byte CheckCrc32 = 0x01;

  /// <summary>CRC-64 integrity check.</summary>
  public const byte CheckCrc64 = 0x04;

  /// <summary>SHA-256 integrity check.</summary>
  public const byte CheckSha256 = 0x0A;

  /// <summary>LZMA2 filter ID.</summary>
  public const ulong FilterLzma2 = 0x21;

  /// <summary>Delta filter ID.</summary>
  public const ulong FilterDelta = 0x03;

  /// <summary>BCJ x86 filter ID.</summary>
  public const ulong FilterBcjX86 = 0x04;

  /// <summary>BCJ PowerPC filter ID.</summary>
  public const ulong FilterBcjPowerPc = 0x05;

  /// <summary>BCJ IA-64 filter ID.</summary>
  public const ulong FilterBcjIa64 = 0x06;

  /// <summary>BCJ ARM filter ID.</summary>
  public const ulong FilterBcjArm = 0x07;

  /// <summary>BCJ ARM Thumb filter ID.</summary>
  public const ulong FilterBcjArmThumb = 0x08;

  /// <summary>BCJ SPARC filter ID.</summary>
  public const ulong FilterBcjSparc = 0x09;

  /// <summary>Returns the check value size in bytes for the given check type.</summary>
  public static int CheckSize(byte checkType) => checkType switch {
    CheckNone => 0,
    CheckCrc32 => 4,
    CheckCrc64 => 8,
    CheckSha256 => 32,
    _ => throw new InvalidDataException($"Unsupported XZ check type: 0x{checkType:X2}"),
  };
}
