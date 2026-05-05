namespace FileFormat.Lzop;

/// <summary>
/// Constants for the LZOP file format.
/// </summary>
public static class LzopConstants {
  /// <summary>
  /// The LZOP magic number: 9 bytes that begin every LZOP file.
  /// </summary>
  public static ReadOnlySpan<byte> Magic => [0x89, 0x4C, 0x5A, 0x4F, 0x00, 0x0D, 0x0A, 0x1A, 0x0A];

  /// <summary>
  /// The LZOP file format version written by this implementation (1.0.3.0).
  /// </summary>
  public const ushort Version = 0x1030;

  /// <summary>
  /// The LZO library version reported in the header.
  /// </summary>
  public const ushort LibVersion = 0x2080;

  /// <summary>
  /// The minimum LZOP version required to decompress files written by this implementation.
  /// </summary>
  public const ushort VersionNeeded = 0x0940;

  /// <summary>
  /// LZO1X-1 compression method identifier.
  /// </summary>
  public const byte MethodLzo1X1 = 1;

  /// <summary>
  /// Default compression level.
  /// </summary>
  public const byte DefaultLevel = 1;

  /// <summary>
  /// Flag bit: include Adler-32 checksum for uncompressed data.
  /// </summary>
  public const uint FlagAdler32D = 0x00000001u;

  /// <summary>
  /// Flag bit: include Adler-32 checksum for compressed data.
  /// </summary>
  public const uint FlagAdler32C = 0x00000002u;

  /// <summary>
  /// The block size used when splitting large inputs: 256 KB.
  /// </summary>
  public const int BlockSize = 256 * 1024;
}
