using System.Text;

namespace FileSystem.Udf;

/// <summary>
/// OSTA Compressed Unicode (CS0), the character set every UDF identifier is
/// recorded in — file names in File Identifier Descriptors and the dstrings of
/// the volume, logical-volume and file-set descriptors.
/// </summary>
/// <remarks>
/// <para>
/// A CS0 byte string starts with a compression identifier and continues with
/// the characters themselves. Compression 8 means one byte per character, each
/// byte being the whole Unicode code point, so the representable range is
/// U+0000..U+00FF. Compression 16 means two big-endian bytes per character.
/// The encoder picks 8 when every character fits in a byte and 16 otherwise —
/// OSTA UDF §2.1.1.
/// </para>
/// <para>
/// Compression 8 is not UTF-8. Reading it as UTF-8 turns every accented Latin-1
/// name a native tool wrote into replacement characters, and writing UTF-8
/// under the identifier 8 produces names no other implementation can read.
/// </para>
/// </remarks>
internal static class OstaCompressedUnicode {

  /// <summary>Compression identifier for one byte per character.</summary>
  public const byte SingleByte = 8;

  /// <summary>Compression identifier for two big-endian bytes per character.</summary>
  public const byte DoubleByte = 16;

  /// <summary>
  /// Decodes a CS0 byte string, compression identifier included. An empty span,
  /// or one holding nothing but the identifier, decodes to the empty string.
  /// </summary>
  public static string Decode(ReadOnlySpan<byte> raw) {
    if (raw.Length <= 1)
      return string.Empty;

    var body = raw[1..];
    var text = raw[0] switch {
      SingleByte => Encoding.Latin1.GetString(body),
      DoubleByte => Encoding.BigEndianUnicode.GetString(body[..(body.Length & ~1)]),
      // No other identifier is defined for UDF identifiers; treating the whole
      // field as single-byte characters is the least destructive reading.
      _ => Encoding.Latin1.GetString(raw),
    };

    return text.TrimEnd('\0');
  }

  /// <summary>
  /// Encodes <paramref name="text" /> as a CS0 byte string, choosing the
  /// narrower compression whenever every character fits in a single byte.
  /// Returns an empty array for an empty string: a zero-length identifier is
  /// recorded with no compression byte at all.
  /// </summary>
  public static byte[] Encode(string text) {
    ArgumentNullException.ThrowIfNull(text);
    if (text.Length == 0)
      return [];

    var singleByte = true;
    foreach (var c in text)
      if (c > 0xFF) {
        singleByte = false;
        break;
      }

    if (singleByte) {
      var result = new byte[1 + text.Length];
      result[0] = SingleByte;
      for (var i = 0; i < text.Length; ++i)
        result[i + 1] = (byte)text[i];
      return result;
    }

    var wide = Encoding.BigEndianUnicode.GetBytes(text);
    var buffer = new byte[1 + wide.Length];
    buffer[0] = DoubleByte;
    wide.CopyTo(buffer, 1);
    return buffer;
  }

  /// <summary>
  /// Writes an ECMA-167 §1/7.2.12 dstring: a CS0 byte string left-aligned in a
  /// fixed-width field whose final byte records how many bytes of it are used.
  /// The text is truncated on a character boundary when it will not fit.
  /// </summary>
  public static void WriteDString(byte[] buffer, int offset, int fieldLength, string text) {
    ArgumentNullException.ThrowIfNull(buffer);
    Array.Clear(buffer, offset, fieldLength);
    if (string.IsNullOrEmpty(text))
      return;

    var encoded = Encode(text);
    // The length byte occupies the last position, so at most fieldLength-1 bytes
    // of characters fit; drop whole characters until they do.
    var characterBytes = encoded[0] == DoubleByte ? 2 : 1;
    var usable = (fieldLength - 1 - 1) / characterBytes * characterBytes + 1;
    if (encoded.Length > usable)
      encoded = encoded[..usable];

    encoded.CopyTo(buffer, offset);
    buffer[offset + fieldLength - 1] = (byte)encoded.Length;
  }
}
