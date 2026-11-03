using System.Text;

namespace FileFormat.Mcm;

/// <summary>
/// The word list a text block's dictionary filter carries in front of its data
/// (<c>docs/MCM-ON-DISK.md</c>, "Dict filter").
/// </summary>
internal sealed class McmDictionary {
  public const byte Escape = 0x03;
  public const byte CapitalFirst = 0x04;
  public const byte CapitalWord = 0x06;
  public const int CodeCount = 128;

  public required int OneByteCount { get; init; }
  public required int TwoByteCount { get; init; }
  public required int ThreeByteCount { get; init; }

  /// <summary>One-byte words, then two-byte, then three-byte, each group sorted.</summary>
  public required IReadOnlyList<byte[]> Words { get; init; }

  /// <summary>The header bytes the filter emits first.</summary>
  public byte[] Serialize() {
    using var stream = new MemoryStream();
    stream.Write([0, 0, 0, 0, Escape, CapitalFirst, CapitalWord,
      (byte)this.OneByteCount, (byte)this.TwoByteCount, (byte)this.ThreeByteCount, CodeCount]);
    foreach (var word in this.Words) {
      stream.Write(word);
      stream.WriteByte(0);
    }
    var bytes = stream.ToArray();
    bytes[0] = (byte)(bytes.Length >> 24);
    bytes[1] = (byte)(bytes.Length >> 16);
    bytes[2] = (byte)(bytes.Length >> 8);
    bytes[3] = (byte)bytes.Length;
    return bytes;
  }
}

/// <summary>mcm's text dictionary filter, both directions.</summary>
internal static class McmDictionaryFilter {
  private const int Window = 16 * 1024;
  private const int MaxWordLength = 256;

  private static bool IsWordByte(int value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= 128;

  private static bool IsUpper(int value) => value is >= 'A' and <= 'Z';

  private enum WordCase { None, FirstChar, All, Invalid }

  private static WordCase CaseOf(ReadOnlySpan<byte> word) {
    var upper = 0;
    foreach (var b in word)
      if (IsUpper(b))
        ++upper;
    if (upper == word.Length)
      return WordCase.All;
    if (IsUpper(word[0]) && upper == 1)
      return WordCase.FirstChar;
    return upper != 0 ? WordCase.Invalid : WordCase.None;
  }

  private static byte Lower(byte value) => IsUpper(value) ? (byte)(value + 32) : value;

  private static byte Upper(byte value) => value is >= (byte)'a' and <= (byte)'z' ? (byte)(value - 32) : value;

  /// <summary>Runs the forward filter over a whole text block, emitting the header first.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> input, McmDictionary dictionary) {
    var codes = BuildCodes(dictionary);
    var header = dictionary.Serialize();
    var output = new List<byte>(input.Length + header.Length);
    var headerPosition = 0;
    var last = 0;
    var consumed = 0;
    var word = new byte[MaxWordLength];
    while (consumed < input.Length || headerPosition < header.Length) {
      var pass = input.Slice(consumed, Math.Min(Window, input.Length - consumed));
      var o = 0;
      if (headerPosition < header.Length) {
        var count = Math.Min(header.Length - headerPosition, Window);
        for (var k = 0; k < count; ++k)
          output.Add(header[headerPosition + k]);
        headerPosition += count;
        o += count;
      }
      var i = 0;
      while (i < pass.Length && o + 5 < Window) {
        var afterSeparator = !IsWordByte(last);
        if (afterSeparator && IsWordByte(pass[i])) {
          var length = 0;
          while (length < MaxWordLength && i + length < pass.Length && IsWordByte(pass[i + length]))
            ++length;
          if (i + length >= pass.Length && length != pass.Length)
            break;
          var room = Window - o;
          var shortest = Math.Min(Math.Max(length, 3), 6);
          var coded = false;
          for (var len = length; len >= shortest; --len) {
            var candidate = pass.Slice(i, len);
            var wordCase = CaseOf(candidate);
            candidate.CopyTo(word);
            if (wordCase == WordCase.All)
              for (var k = 0; k < len; ++k)
                word[k] = Lower(word[k]);
            else if (wordCase == WordCase.FirstChar)
              word[0] = Lower(word[0]);
            if (!codes.TryGetValue(Encoding.Latin1.GetString(word, 0, len), out var code))
              continue;
            if (wordCase == WordCase.All) {
              output.Add(McmDictionary.CapitalWord);
              ++o;
            } else if (wordCase != WordCase.None) {
              output.Add(McmDictionary.CapitalFirst);
              ++o;
            }
            output.AddRange(code);
            o += code.Length;
            i += len;
            last = 'a';
            coded = true;
            break;
          }
          if (coded)
            continue;
          if (length < room) {
            var wordCase = CaseOf(pass.Slice(i, length));
            last = 'a';
            if (wordCase is WordCase.All or WordCase.FirstChar) {
              output.Add(wordCase == WordCase.All ? McmDictionary.CapitalWord : McmDictionary.CapitalFirst);
              for (var k = 0; k < length; ++k)
                output.Add(wordCase == WordCase.All || k == 0 ? Lower(pass[i + k]) : pass[i + k]);
              o += length + 1;
              i += length;
              break;
            }
          }
        }
        if (afterSeparator) {
          var value = pass[i];
          if (value is McmDictionary.Escape or McmDictionary.CapitalFirst or McmDictionary.CapitalWord or >= 128) {
            output.Add(McmDictionary.Escape);
            ++o;
          }
        }
        last = pass[i++];
        output.Add((byte)last);
        ++o;
      }
      consumed += i;
    }
    return output.ToArray();
  }

  /// <summary>Inverts <see cref="Encode"/>.</summary>
  public static byte[] Decode(ReadOnlySpan<byte> encoded, int expectedLength) {
    if (encoded.Length < 4)
      throw new InvalidDataException("mcm dictionary header is truncated.");
    var headerLength = encoded[0] << 24 | encoded[1] << 16 | encoded[2] << 8 | encoded[3];
    if (headerLength < 11 || headerLength > encoded.Length)
      throw new InvalidDataException("mcm dictionary header length is invalid.");
    int escape = encoded[4], capitalFirst = encoded[5], capitalWord = encoded[6];
    int one = encoded[7], two = encoded[8], three = encoded[9], codeCount = encoded[10];
    if (codeCount != McmDictionary.CodeCount)
      throw new NotSupportedException($"mcm dictionary with {codeCount} code bytes; mcm only writes {McmDictionary.CodeCount}.");
    var words = new List<byte[]>();
    for (var p = 11; p < headerLength;) {
      var end = encoded[p..headerLength].IndexOf((byte)0);
      if (end < 0)
        throw new InvalidDataException("mcm dictionary word is not terminated.");
      words.Add(encoded.Slice(p, end).ToArray());
      p += end + 1;
    }
    List<byte[]> oneByte = [], twoByte = [], threeByte = [];
    var index = 0;
    for (var b = 0; b < one && index < words.Count; ++b)
      oneByte.Add(words[index++]);
    for (var b = 0; b < two * codeCount && index < words.Count; ++b)
      twoByte.Add(words[index++]);
    for (var b = 0; b < three * codeCount * codeCount && index < words.Count; ++b)
      threeByte.Add(words[index++]);
    var oneStart = 128;
    var twoStart = oneStart + one;
    var threeStart = twoStart + two;

    var output = new byte[expectedLength];
    var o = 0;
    var last = 0;
    var capitalMode = false;
    var i = headerLength;
    int Next(ReadOnlySpan<byte> source, ref int position)
      => position < source.Length ? source[position++] : throw new InvalidDataException("mcm dictionary stream ends early.");
    void Put(byte value, ref int outPosition, byte[] target) {
      if (outPosition >= target.Length)
        throw new InvalidDataException("mcm dictionary stream decodes past its block.");
      target[outPosition++] = value;
    }
    while (i < encoded.Length) {
      int c = encoded[i++];
      if (!IsWordByte(last)) {
        var firstCap = c == capitalFirst;
        var allCap = c == capitalWord;
        if (c >= 128 || firstCap || allCap) {
          if (firstCap || allCap)
            c = Next(encoded, ref i);
          if (c >= oneStart) {
            byte[] word;
            if (c < twoStart)
              word = Pick(oneByte, c - oneStart);
            else if (c < threeStart)
              word = Pick(twoByte, (c - twoStart) * 128 + Next(encoded, ref i) - 128);
            else {
              var c2 = Next(encoded, ref i);
              var c3 = Next(encoded, ref i);
              word = Pick(threeByte, (c - threeStart) * 128 * 128 + (c2 - 128) * 128 + c3 - 128);
            }
            var capitals = allCap ? word.Length : firstCap ? 1 : 0;
            for (var k = 0; k < word.Length; ++k)
              Put(k < capitals ? Upper(word[k]) : word[k], ref o, output);
            last = output[o - 1];
            continue;
          }
          if (firstCap && c is >= 'a' and <= 'z')
            c -= 32;
          else if (allCap)
            capitalMode = true;
        }
        if (c == escape)
          c = Next(encoded, ref i);
      }
      if (capitalMode && c is >= 'a' and <= 'z')
        c -= 32;
      else
        capitalMode = false;
      Put((byte)c, ref o, output);
      last = c;
    }
    if (o != expectedLength)
      throw new InvalidDataException("mcm dictionary stream decodes to the wrong length.");
    return output;
  }

  private static byte[] Pick(List<byte[]> words, int index)
    => (uint)index < (uint)words.Count ? words[index] : throw new InvalidDataException("mcm dictionary code word out of range.");

  private static Dictionary<string, byte[]> BuildCodes(McmDictionary dictionary) {
    var codes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    var words = dictionary.Words;
    var codeStart = 256 - McmDictionary.CodeCount;
    int end1 = codeStart + dictionary.OneByteCount, end2 = end1 + dictionary.TwoByteCount, end3 = end2 + dictionary.ThreeByteCount;
    var index = 0;
    void Add(byte[] code) {
      var key = Encoding.Latin1.GetString(words[index++].Select(Lower).ToArray());
      codes.TryAdd(key, code);
    }
    for (var b1 = codeStart; b1 < end1; ++b1)
      if (index < words.Count)
        Add([(byte)b1]);
    for (var b1 = end1; b1 < end2; ++b1)
      for (var b2 = codeStart; b2 < 256; ++b2)
        if (index < words.Count)
          Add([(byte)b1, (byte)b2]);
    for (var b1 = end2; b1 < end3; ++b1)
      for (var b2 = codeStart; b2 < 256; ++b2)
        for (var b3 = codeStart; b3 < 256; ++b3)
          if (index < words.Count)
            Add([(byte)b1, (byte)b2, (byte)b3]);
    return codes;
  }
}
