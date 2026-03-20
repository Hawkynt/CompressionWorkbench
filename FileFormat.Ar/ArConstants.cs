namespace FileFormat.Ar;

/// <summary>
/// Constants for the Unix ar archive format.
/// </summary>
public static class ArConstants {
  /// <summary>
  /// Global archive header: "!&lt;arch&gt;\n" (8 bytes).
  /// </summary>
  public static ReadOnlySpan<byte> GlobalMagic => "!<arch>\n"u8;

  /// <summary>
  /// Size in bytes of the global archive header.
  /// </summary>
  public const int GlobalHeaderSize = 8;

  /// <summary>
  /// Size in bytes of each entry header.
  /// </summary>
  public const int EntryHeaderSize = 60;

  /// <summary>
  /// The two-byte magic that terminates every entry header: "`\n" (0x60, 0x0A).
  /// </summary>
  public static ReadOnlySpan<byte> EntryMagic => "`\n"u8;

  /// <summary>
  /// Padding byte appended after entry data when the data length is odd.
  /// </summary>
  public const byte PaddingByte = 0x0A;

  /// <summary>
  /// GNU extended filename table entry name ("//").
  /// </summary>
  public const string GnuStringTableName = "//";

  /// <summary>
  /// Prefix for a GNU long filename reference (e.g. "/12").
  /// </summary>
  public const char GnuLongNamePrefix = '/';

  /// <summary>
  /// Maximum filename length that fits directly in the 16-byte name field
  /// (15 usable characters + '/' terminator).
  /// </summary>
  public const int MaxInlineNameLength = 15;
}
