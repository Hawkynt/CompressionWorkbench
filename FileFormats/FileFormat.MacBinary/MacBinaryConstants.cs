namespace FileFormat.MacBinary;

/// <summary>
/// Constants for the MacBinary container format.
/// </summary>
internal static class MacBinaryConstants {
  /// <summary>Header size in bytes.</summary>
  public const int HeaderSize = 128;

  /// <summary>Data and resource forks are padded to this alignment.</summary>
  public const int PaddingAlignment = 128;

  /// <summary>MacBinary I version byte (no CRC, no signature).</summary>
  public const byte Version1 = 0;

  /// <summary>MacBinary II version byte (has CRC-16 at offset 124).</summary>
  public const byte Version2 = 129;

  /// <summary>MacBinary III version byte (has signature "mBIN" at offset 102).</summary>
  public const byte Version3 = 130;

  /// <summary>Offset of the "mBIN" signature in the header.</summary>
  public const int SignatureOffset = 102;

  /// <summary>The "mBIN" signature as a 32-bit big-endian value (0x6D42494E).</summary>
  public const uint Signature = 0x6D42494E;

  /// <summary>Offset of the CRC-16 field in the header.</summary>
  public const int CrcOffset = 124;
}
