namespace FileFormat.BinHex;

/// <summary>
/// Constants for the BinHex 4.0 encoding format.
/// </summary>
internal static class BinHexConstants {
  /// <summary>
  /// The standard header line that precedes BinHex-encoded data.
  /// </summary>
  public const string HeaderLine = "(This file must be converted with BinHex 4.0)";

  /// <summary>
  /// Character that marks both the start and end of encoded data.
  /// </summary>
  public const char StartChar = ':';

  /// <summary>
  /// Character that marks both the start and end of encoded data.
  /// </summary>
  public const char EndChar = ':';

  /// <summary>
  /// Run-length encoding escape byte.
  /// </summary>
  public const byte RleEscapeByte = 0x90;

  /// <summary>
  /// Maximum characters per line in encoded output.
  /// </summary>
  public const int LineWidth = 64;

  /// <summary>
  /// The 64-character BinHex 6-to-8 encoding alphabet.
  /// </summary>
  public const string Alphabet = "!\"#$%&'()*+,-012345689@ABCDEFGHIJKLMNPQRSTUVXYZ[`abcdefhijklmpqr";

  /// <summary>
  /// Lookup table mapping a 6-bit value (0-63) to its encoded character.
  /// </summary>
  public static readonly char[] EncodeTable = Alphabet.ToCharArray();

  /// <summary>
  /// Lookup table mapping an ASCII character to its 6-bit value.
  /// A value of 0xFF indicates an invalid character.
  /// </summary>
  public static readonly byte[] DecodeTable = BuildDecodeTable();

  private static byte[] BuildDecodeTable() {
    var table = new byte[128];
    Array.Fill(table, (byte)0xFF);
    for (var i = 0; i < Alphabet.Length; i++)
      table[Alphabet[i]] = (byte)i;
    return table;
  }
}
