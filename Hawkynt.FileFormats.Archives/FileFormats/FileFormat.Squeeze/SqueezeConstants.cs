namespace FileFormat.Squeeze;

/// <summary>
/// Constants for the CP/M Squeeze file format (Richard Greenlaw, 1981).
/// </summary>
internal static class SqueezeConstants {

  /// <summary>
  /// Magic number identifying a Squeeze-compressed file (0xFF76, stored little-endian as 0x76, 0xFF).
  /// </summary>
  public const ushort Magic = 0xFF76;

  /// <summary>
  /// Run-length escape byte used by the preprocessing stage.
  /// </summary>
  public const byte RleDelimiter = 0x90;

  /// <summary>
  /// Pseudo-symbol representing end of file in the Huffman tree.
  /// </summary>
  public const int EofMarker = 256;

  /// <summary>
  /// Maximum number of serialized internal Huffman nodes for the 257-symbol alphabet.
  /// </summary>
  public const int MaxNodes = 256;
}
