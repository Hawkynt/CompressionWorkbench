using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Zip;

/// <summary>
/// Decodes the names (and comments) stored in ZIP headers. With general-purpose bit 11 set
/// the bytes are UTF-8. Without it, APPNOTE.TXT (4.4.4 and appendix D) names IBM code page
/// 437, but tools disagree in practice, so a name is read the way 7-Zip and Info-ZIP read
/// it: from an Info-ZIP Unicode Path extra field (0x7075) while that field still matches
/// the stored bytes, as UTF-8 when the bytes are valid UTF-8 (macOS, Linux tools and phones
/// write UTF-8 without setting the flag), and in code page 437 otherwise (what Windows
/// Explorer writes on systems whose OEM code page is 437, and what the specification names).
/// </summary>
/// <remarks>
/// Names without the flag used to be read as Latin-1, so a name Windows Explorer wrote as
/// "Ürlaub Straße.jpg" showed as "\x9arlaub Straáe.jpg".
/// </remarks>
internal static class ZipNameEncoding {
  private const ushort UnicodePathExtraId = 0x7075;

  private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

  // IBM code page 437, bytes 0x80..0xFF, from the Unicode Consortium's mapping table
  // (MAPPINGS/VENDORS/MICSFT/PC/CP437.TXT).
  private const string Cp437High =
    "ÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜ¢£¥₧ƒ" +
    "áíóúñÑªº¿⌐¬½¼¡«»░▒▓│┤╡╢╖╕╣║╗╝╜╛┐" +
    "└┴┬├─┼╞╟╚╔╩╦╠═╬╧╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀" +
    "αßΓπΣσµτΦΘΩδ∞φε∩≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ";

  /// <summary>The name in <paramref name="raw"/>, given the entry's flags and extra field.</summary>
  public static string Decode(byte[] raw, ushort flags, byte[]? extra) {
    ArgumentNullException.ThrowIfNull(raw);
    if ((flags & ZipConstants.FlagUtf8) != 0)
      return Encoding.UTF8.GetString(raw);
    return UnicodePath(raw, extra) ?? DecodeWithoutFlag(raw);
  }

  /// <summary>Bytes stored without the UTF-8 flag: ASCII, valid UTF-8, else code page 437.</summary>
  public static string DecodeWithoutFlag(byte[] raw) {
    ArgumentNullException.ThrowIfNull(raw);
    if (raw.All(static b => b < 0x80))
      return Encoding.ASCII.GetString(raw);
    try {
      return StrictUtf8.GetString(raw);
    } catch (DecoderFallbackException) {
      return Cp437(raw);
    }
  }

  /// <summary>The bytes as code page 437 text.</summary>
  public static string Cp437(ReadOnlySpan<byte> raw) {
    var sb = new StringBuilder(raw.Length);
    foreach (var b in raw)
      sb.Append(b < 0x80 ? (char)b : Cp437High[b - 0x80]);
    return sb.ToString();
  }

  // The Info-ZIP Unicode Path extra field (0x7075): version 1, the CRC-32 of the stored
  // name, then the name in UTF-8. A tool that renamed the entry without updating the field
  // leaves a CRC that no longer matches; the field is then ignored.
  private static string? UnicodePath(byte[] raw, byte[]? extra) {
    if (extra == null) return null;
    for (var i = 0; i + 4 <= extra.Length;) {
      var id = (ushort)(extra[i] | extra[i + 1] << 8);
      var size = extra[i + 2] | extra[i + 3] << 8;
      var data = i + 4;
      if (data + size > extra.Length) return null;
      if (id == UnicodePathExtraId && size >= 5 && extra[data] == 1) {
        var crc = (uint)(extra[data + 1] | extra[data + 2] << 8 | extra[data + 3] << 16 | extra[data + 4] << 24);
        if (crc != Crc32.Compute(raw)) return null;
        try {
          return StrictUtf8.GetString(extra, data + 5, size - 5);
        } catch (DecoderFallbackException) {
          return null;
        }
      }
      i = data + size;
    }
    return null;
  }
}
