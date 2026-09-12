#pragma warning disable CS1591
namespace FileSystem.Rt11;

/// <summary>
/// RAD-50 (also written "Radix-50") encoding used by DEC PDP-11 operating
/// systems for filenames. Three printable characters from a 40-symbol alphabet
/// are packed into one 16-bit word: <c>word = c1*40^2 + c2*40 + c3</c>. The
/// alphabet is: space, A-Z, '$', '.', '0'-'9' (40 symbols, indices 0-39).
/// </summary>
public static class Rad50 {

  // Index 0 = space (pad), 1-26 = A-Z, 27 = '$', 28 = '.', 29 = unused/'?',
  // 30-39 = '0'-'9'. The official DEC table reserves index 29 for an unused
  // code point but accepts '?' there in some toolchains. We follow the manual
  // and reject anything outside the 40-symbol alphabet at encode time.
  private const string Alphabet = " ABCDEFGHIJKLMNOPQRSTUVWXYZ$.?0123456789";

  /// <summary>
  /// Encodes the first three characters of <paramref name="text"/> (case-insensitive,
  /// shorter strings are space-padded) into a single PDP-11 word.
  /// </summary>
  /// <exception cref="InvalidOperationException">A character is not in the RAD-50 alphabet.</exception>
  public static ushort EncodeWord(string text, int startIndex) {
    var c1 = SafeChar(text, startIndex + 0);
    var c2 = SafeChar(text, startIndex + 1);
    var c3 = SafeChar(text, startIndex + 2);
    var i1 = IndexOf(c1);
    var i2 = IndexOf(c2);
    var i3 = IndexOf(c3);
    return (ushort)((i1 * 40 * 40) + (i2 * 40) + i3);
  }

  /// <summary>
  /// Decodes a 16-bit RAD-50 word into three characters. Returns trailing spaces
  /// when fewer than three meaningful symbols were packed.
  /// </summary>
  public static string DecodeWord(ushort word) {
    var c3 = Alphabet[word % 40];
    word = (ushort)(word / 40);
    var c2 = Alphabet[word % 40];
    word = (ushort)(word / 40);
    var c1 = Alphabet[word % 40];
    return new string([c1, c2, c3]);
  }

  /// <summary>
  /// Encodes a 6-character RT-11 filename stem into two RAD-50 words.
  /// </summary>
  public static (ushort High, ushort Low) EncodeName6(string name) {
    var padded = (name ?? "").ToUpperInvariant().PadRight(6).Substring(0, 6);
    return (EncodeWord(padded, 0), EncodeWord(padded, 3));
  }

  /// <summary>
  /// Encodes a 3-character RT-11 file type / extension into one RAD-50 word.
  /// </summary>
  public static ushort EncodeType3(string type)
    => EncodeWord((type ?? "").ToUpperInvariant().PadRight(3).Substring(0, 3), 0);

  /// <summary>
  /// Decodes a (high, low) word pair into a 6-char filename stem (trailing
  /// spaces trimmed).
  /// </summary>
  public static string DecodeName6(ushort high, ushort low)
    => (DecodeWord(high) + DecodeWord(low)).TrimEnd(' ');

  /// <summary>
  /// Decodes a 3-char extension word (trailing spaces trimmed).
  /// </summary>
  public static string DecodeType3(ushort type) => DecodeWord(type).TrimEnd(' ');

  /// <summary>
  /// Returns true if <paramref name="text"/> contains only characters representable
  /// in the RAD-50 alphabet (case-insensitive).
  /// </summary>
  public static bool IsValid(string text) {
    if (text == null) return true;
    foreach (var c in text) {
      var u = char.ToUpperInvariant(c);
      if (Alphabet.IndexOf(u) < 0) return false;
    }
    return true;
  }

  private static char SafeChar(string s, int idx)
    => idx < s.Length ? char.ToUpperInvariant(s[idx]) : ' ';

  private static int IndexOf(char c) {
    var idx = Alphabet.IndexOf(c);
    if (idx < 0)
      throw new InvalidOperationException($"RAD-50: character '{c}' (0x{(int)c:X2}) is not in the RT-11 alphabet.");
    return idx;
  }
}
