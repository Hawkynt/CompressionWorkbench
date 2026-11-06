namespace FileFormat.Sqx;

/// <summary>
/// Block-level constants and checksums of the SQX container: every block starts with a 16-bit
/// checksum, a type byte, 16-bit flags and the 16-bit header size, all little-endian.
/// </summary>
internal static class SqxContainer {
  public const byte BlockMain = 0x52;
  public const byte BlockFile = 0x44;
  public const byte BlockDirectory = 0x56;
  public const byte BlockEnd = 0x53;
  public const int HeaderSize = 7;
  public const int MainBlockSize = 25;

  /// <summary>Archive signature, 7 bytes into the main block.</summary>
  public static ReadOnlySpan<byte> Signature => "-sqx-"u8;

  /// <summary>CRC-32 (reflected polynomial 0xEDB88320, initial value 0xFFFFFFFF) without the final inversion.</summary>
  public static uint RawCrc32(ReadOnlySpan<byte> data) => ~Compression.Core.Checksums.Crc32.Compute(data);

  /// <summary>A block's checksum: the low 16 bits of <see cref="RawCrc32"/> over the header from its type byte on.</summary>
  public static ushort BlockCrc(ReadOnlySpan<byte> headerFromType) => (ushort)RawCrc32(headerFromType);
}
