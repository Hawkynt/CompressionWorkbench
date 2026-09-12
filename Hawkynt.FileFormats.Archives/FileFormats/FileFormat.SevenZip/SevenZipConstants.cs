namespace FileFormat.SevenZip;

/// <summary>
/// Constants for the 7z archive format.
/// </summary>
internal static class SevenZipConstants {
  /// <summary>
  /// The 7z file signature bytes: <c>7z BC AF 27 1C</c>.
  /// </summary>
  public static ReadOnlySpan<byte> Signature => [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

  /// <summary>Property ID: end of header block.</summary>
  public const byte IdEnd = 0x00;

  /// <summary>Property ID: main archive header.</summary>
  public const byte IdHeader = 0x01;

  /// <summary>Property ID: archive properties.</summary>
  public const byte IdArchiveProperties = 0x02;

  /// <summary>Property ID: additional streams info.</summary>
  public const byte IdAdditionalStreams = 0x03;

  /// <summary>Property ID: main streams info.</summary>
  public const byte IdMainStreams = 0x04;

  /// <summary>Property ID: files info.</summary>
  public const byte IdFilesInfo = 0x05;

  /// <summary>Property ID: pack info (compressed sizes).</summary>
  public const byte IdPackInfo = 0x06;

  /// <summary>Property ID: unpack info (folders/coders).</summary>
  public const byte IdUnpackInfo = 0x07;

  /// <summary>Property ID: sub-streams info (per-file sizes within a folder).</summary>
  public const byte IdSubStreamsInfo = 0x08;

  /// <summary>Property ID: size value.</summary>
  public const byte IdSize = 0x09;

  /// <summary>Property ID: CRC-32 digest.</summary>
  public const byte IdCrc = 0x0A;

  /// <summary>Property ID: folder definition.</summary>
  public const byte IdFolder = 0x0B;

  /// <summary>Property ID: coders unpack size.</summary>
  public const byte IdCodersUnpackSize = 0x0C;

  /// <summary>Property ID: number of unpack streams per folder.</summary>
  public const byte IdNumUnpackStreams = 0x0D;

  /// <summary>Property ID: empty stream marker (directories and empty files).</summary>
  public const byte IdEmptyStream = 0x0E;

  /// <summary>Property ID: empty file marker (distinguishes empty files from directories).</summary>
  public const byte IdEmptyFile = 0x0F;

  /// <summary>Property ID: anti file marker.</summary>
  public const byte IdAnti = 0x10;

  /// <summary>Property ID: file names (UTF-16LE).</summary>
  public const byte IdName = 0x11;

  /// <summary>Property ID: creation time.</summary>
  public const byte IdCTime = 0x12;

  /// <summary>Property ID: last access time.</summary>
  public const byte IdATime = 0x13;

  /// <summary>Property ID: last write time.</summary>
  public const byte IdMTime = 0x14;

  /// <summary>Property ID: Windows file attributes.</summary>
  public const byte IdAttributes = 0x15;

  /// <summary>Property ID: encoded (compressed) header.</summary>
  public const byte IdEncodedHeader = 0x17;

  /// <summary>Property ID: dummy/padding.</summary>
  public const byte IdDummy = 0x19;

  /// <summary>Codec ID for the Copy (store) method.</summary>
  public static ReadOnlySpan<byte> CodecCopy => [0x00];

  /// <summary>Codec ID for LZMA compression.</summary>
  public static ReadOnlySpan<byte> CodecLzma => [0x03, 0x01, 0x01];

  /// <summary>Codec ID for LZMA2 compression.</summary>
  public static ReadOnlySpan<byte> CodecLzma2 => [0x21];

  /// <summary>Codec ID for Deflate compression.</summary>
  public static ReadOnlySpan<byte> CodecDeflate => [0x04, 0x01, 0x08];

  /// <summary>Codec ID for Bzip2 compression.</summary>
  public static ReadOnlySpan<byte> CodecBzip2 => [0x04, 0x02, 0x02];

  /// <summary>Codec ID for PPMd compression.</summary>
  public static ReadOnlySpan<byte> CodecPpmd => [0x03, 0x04, 0x05];

  /// <summary>Codec ID for BCJ (x86 executable) filter.</summary>
  public static ReadOnlySpan<byte> CodecBcj => [0x03, 0x03, 0x01, 0x03];

  /// <summary>Codec ID for BCJ2 (x86 executable, 4-stream) filter.</summary>
  public static ReadOnlySpan<byte> CodecBcj2 => [0x03, 0x03, 0x01, 0x1B];

  /// <summary>Codec ID for BCJ PowerPC filter.</summary>
  public static ReadOnlySpan<byte> CodecBcjPpc => [0x03, 0x03, 0x02, 0x05];

  /// <summary>Codec ID for BCJ IA-64 filter.</summary>
  public static ReadOnlySpan<byte> CodecBcjIa64 => [0x03, 0x03, 0x04, 0x01];

  /// <summary>Codec ID for BCJ ARM filter.</summary>
  public static ReadOnlySpan<byte> CodecBcjArm => [0x03, 0x03, 0x05, 0x01];

  /// <summary>Codec ID for BCJ ARM Thumb filter.</summary>
  public static ReadOnlySpan<byte> CodecBcjArmThumb => [0x03, 0x03, 0x07, 0x01];

  /// <summary>Codec ID for BCJ SPARC filter.</summary>
  public static ReadOnlySpan<byte> CodecBcjSparc => [0x03, 0x03, 0x08, 0x05];

  /// <summary>Codec ID for Delta filter.</summary>
  public static ReadOnlySpan<byte> CodecDelta => [0x03];

  /// <summary>Codec ID for AES-256 + SHA-256 encryption.</summary>
  public static ReadOnlySpan<byte> CodecAes => [0x06, 0xF1, 0x07, 0x01];

  /// <summary>Size of the signature header in bytes.</summary>
  public const int SignatureHeaderSize = 32;

  /// <summary>Major version of the 7z format.</summary>
  public const byte FormatMajorVersion = 0;

  /// <summary>Minor version of the 7z format.</summary>
  public const byte FormatMinorVersion = 4;
}
