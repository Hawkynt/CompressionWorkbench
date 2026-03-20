namespace FileFormat.IcePacker;

/// <summary>
/// Constants for the Atari ST ICE Packer format (Axe of Delight, 1989).
/// A popular packer in the Atari demo scene that compresses a single file
/// using backward LZ77 with variable-length match encoding.
/// </summary>
internal static class IcePackerConstants {
  /// <summary>
  /// Standard magic signature: ASCII "Ice!" (0x49 0x63 0x65 0x21).
  /// </summary>
  public const uint Magic1 = 0x49636521;

  /// <summary>
  /// Alternate magic signature: ASCII "ICE!" (0x49 0x43 0x45 0x21).
  /// </summary>
  public const uint Magic2 = 0x49434521;

  /// <summary>
  /// Total size of the ICE header in bytes: 4 (magic) + 4 (packed size) + 4 (original size).
  /// </summary>
  public const int HeaderSize = 12;

  /// <summary>Maximum match length encodable (12 + 255 = 267).</summary>
  public const int MaxMatchLength = 267;

  /// <summary>Minimum match length (2).</summary>
  public const int MinMatchLength = 2;

  /// <summary>Maximum offset for a length-2 match (9-bit offset field, 1-512).</summary>
  public const int MaxOffset2 = 512;

  /// <summary>Maximum offset for a length-3 match (10-bit offset field, 1-1024).</summary>
  public const int MaxOffset3 = 1024;

  /// <summary>Maximum offset for longer matches (12-bit offset field, 1-4096).</summary>
  public const int MaxOffsetLong = 4096;
}
