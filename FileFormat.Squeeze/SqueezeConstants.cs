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
  /// Pseudo-symbol representing end of file in the Huffman tree.
  /// </summary>
  public const int EofMarker = 256;

  /// <summary>
  /// Maximum number of nodes in the Huffman tree (256 byte symbols + 1 EOF + up to 256 internal nodes).
  /// </summary>
  public const int MaxNodes = 513;
}
