using Compression.Core.Checksums;
using Compression.Core.Entropy.Ppmd;

namespace FileFormat.StuffItX;

/// <summary>
/// The English preprocessor of StuffIt X (preprocess id 0): words of a fixed 100,366-word
/// dictionary replaced by a code byte and the word number in base-52 letters, plus a case model
/// that flips the first letter of every sentence.
/// </summary>
/// <remarks>
/// <para>
/// The filtered stream opens with four code bytes: escape, word, capitalised word and upper-case
/// word. An escape passes the next byte through and ends the sentence-start state. A word code is
/// followed by letters ('a'-'z' = 1-26, 'A'-'Z' = 27-52, most significant first) giving the word
/// number, then by the byte that followed the word (itself escaped when it equals the escape code).
/// Outside words, the first letter after a sentence start ('.', '?', '!') has its case flipped.
/// </para>
/// <para>
/// The dictionary is 881,863 bytes of newline-separated words, stored PPMd var.I-packed (order 16,
/// 16 MiB, restart on exhaustion) with CRC-32 0xFB1DCFD5. The packed bytes are XADMaster's
/// <c>StuffItXEnglishDictionary.c</c>, carried verbatim as an embedded resource; the filter is
/// converted from <c>XADStuffItXEnglishHandle.m</c> (LGPL-2.1-or-later, see
/// THIRD-PARTY-NOTICE.XADMaster.txt).</para>
/// </remarks>
internal static class StuffItXEnglish {
  private const int WordCount = 100366;
  private const int DictionarySize = 881863;
  private const uint DictionaryCrc = 0xFB1DCFD5;

  private static readonly Lazy<(byte[] Words, int[] Starts)> Dictionary = new(Load);

  private static (byte[] Words, int[] Starts) Load() {
    using var resource = typeof(StuffItXEnglish).Assembly.GetManifestResourceStream("FileFormat.StuffItX.EnglishDictionary.ppmd")
      ?? throw new InvalidOperationException("The StuffIt X English dictionary resource is missing.");
    using var ms = new MemoryStream();
    resource.CopyTo(ms);
    var model = new Ppmd8(16 << 20, 16, Ppmd8RestoreMethod.Restart);
    if (!model.StartDecoding(ms.ToArray(), 0)) throw new InvalidDataException("The StuffIt X English dictionary does not decode.");
    var words = new byte[DictionarySize];
    for (var i = 0; i < words.Length; ++i) {
      var symbol = model.DecodeSymbol();
      if (symbol < 0) throw new InvalidDataException("The StuffIt X English dictionary is truncated.");
      words[i] = (byte)symbol;
    }
    if (Crc32.Compute(words) != DictionaryCrc) throw new InvalidDataException("The StuffIt X English dictionary fails its CRC-32.");

    // one start per word plus a closing boundary: word i spans starts[i] .. starts[i+1]-2 (newline excluded)
    var starts = new int[WordCount + 1];
    var p = 0;
    for (var i = 1; i <= WordCount; ++i) {
      while (words[p] != 0x0A) ++p;
      starts[i] = ++p;
    }
    return (words, starts);
  }

  /// <summary>Word <paramref name="index"/> of the dictionary.</summary>
  internal static string Word(int index) {
    var (words, starts) = Dictionary.Value;
    return System.Text.Encoding.Latin1.GetString(words, starts[index], starts[index + 1] - starts[index] - 1);
  }

  /// <summary>Undoes the filter on <paramref name="filtered"/>, producing at most <paramref name="length"/> bytes.</summary>
  public static byte[] Decode(byte[] filtered, long length) {
    if (filtered.Length < 4) throw new InvalidDataException("StuffIt X English stream is too short.");
    int escape = filtered[0], word = filtered[1], first = filtered[2], upper = filtered[3];
    var output = new MemoryStream((int)Math.Min(Math.Max(length, 0), int.MaxValue));
    var caseFlag = true;
    var p = 4;
    Span<byte> buffer = stackalloc byte[64];
    while (p < filtered.Length && output.Length < length) {
      int c = filtered[p++];
      if (c == escape) {
        caseFlag = false;
        if (p >= filtered.Length) throw new InvalidDataException("StuffIt X English stream ends inside an escape.");
        output.WriteByte(filtered[p++]);
        continue;
      }
      if (c == word || c == first || c == upper) {
        int c2 = -1, index = 0;
        while (p < filtered.Length) {
          c2 = filtered[p++];
          if (c2 is (< 'A' or > 'Z') and (< 'a' or > 'z')) break;
          index = index * 52 + (c2 <= 'Z' ? c2 - 'A' + 27 : c2 - 'a' + 1);
          if (index >= WordCount) throw new InvalidDataException("StuffIt X English word number is out of range.");
          c2 = -1;
        }
        var (words, starts) = Dictionary.Value;
        var wordLength = starts[index + 1] - starts[index] - 1;
        words.AsSpan(starts[index], wordLength).CopyTo(buffer);
        if (c == upper) {
          for (var i = 0; i < wordLength; ++i) buffer[i] -= 32;
        } else if (c == first) {
          buffer[0] -= 32;
        }
        if (caseFlag) {
          if (buffer[0] is >= (byte)'A' and <= (byte)'Z') buffer[0] += 32;
          else if (buffer[0] is >= (byte)'a' and <= (byte)'z') buffer[0] -= 32;
        }
        if (c2 == escape) c2 = p < filtered.Length ? filtered[p++] : throw new InvalidDataException("StuffIt X English stream ends inside an escape.");
        if (c2 != -1) buffer[wordLength++] = (byte)c2;
        caseFlag = c2 is '.' or '?' or '!';
        output.Write(buffer[..wordLength]);
        continue;
      }
      if (caseFlag) {
        if (c is >= 'A' and <= 'Z') {
          c += 32;
          caseFlag = false;
        } else if (c is >= 'a' and <= 'z') {
          c -= 32;
          caseFlag = false;
        }
      }
      if (c is '.' or '?' or '!') caseFlag = true;
      else if (c is not (' ' or '\n' or '\r' or '\t')) caseFlag = false;
      output.WriteByte((byte)c);
    }
    var result = output.ToArray();
    return result.Length > length ? result.AsSpan(0, (int)length).ToArray() : result;
  }
}
