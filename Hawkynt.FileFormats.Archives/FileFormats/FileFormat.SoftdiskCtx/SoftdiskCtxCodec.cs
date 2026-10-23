using System.Text;

namespace FileFormat.SoftdiskCtx;

/// <summary>
/// Softdisk Text Compressor (<c>.ctx</c>, Softdisk PC diskmagazines, 1993): byte-level
/// substitution with two tables carried in every file.
/// </summary>
/// <remarks>
/// <para>
/// Layout: <c>03 'C' 'T' '0' '0' '1'</c>; the original file name, NUL-terminated; 30
/// strings of up to five bytes (each ends after five bytes or at a NUL); 127 two-byte
/// pairs. Then the text: bytes 0–9, 11, 12 and 14–31 stand for the 30 strings in order,
/// 128–254 for the pairs, 13 for CR LF; 255 escapes — followed by 32–127 it means "repeat
/// the next byte (value − 30) times", followed by anything else it is that byte, literally.
/// Every other byte is itself.
/// </para>
/// <para>
/// Implemented from the format description on the Just Solve the File Format Problem
/// wiki (CC0); the encoder's tables are the ones Softdisk's own files carry. Checked
/// against the ctxer decompressor and Softdisk-made files.
/// </para>
/// </remarks>
public static class SoftdiskCtxCodec {

  /// <summary>The six signature bytes.</summary>
  public static ReadOnlySpan<byte> Magic => [0x03, 0x43, 0x54, 0x30, 0x30, 0x31];

  private const int WordCount = 30;
  private const int PairCount = 127;

  /// <summary>The 30 short strings, as Softdisk PC issues 102–110 carry them.</summary>
  private static readonly string[] DefaultWords = [
    " the ", " and ", "SDPC", " the", " or ", " an ", " to ", " of ", " is ", " it ",
    " in ", " on ", "ing ", " th", "nd ", " yo", ".  ", "ed ", " a ", "er ",
    "is ", "or ", "ng ", "re ", " pr", "our", "ou ", " co", "ion", "ve ",
  ];

  /// <summary>The 127 pairs, as Softdisk's files carry them.</summary>
  private const string DefaultPairs =
    "e  tths  ahet inn d err  oanreouon i sory  wtoo , aratises. nd cente p fvehalentstitrose mf  bng yaledlltirayoofl meurea h dasa hicog neomdeamlipr Iil lush riu ca euticelow rm  gwigrlyecetbefoiomace urstalola ndieege Tavctsisschacnsunmosuogsodono WhoI rt";

  /// <summary>Byte value that stands for word <paramref name="i"/>.</summary>
  private static byte WordCode(int i) => (byte)(i < 10 ? i : i < 12 ? i + 1 : i + 2);

  /// <summary>Decodes a <c>.ctx</c> file; also returns the stored original file name.</summary>
  /// <exception cref="InvalidDataException">Bad signature or truncated tables.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> data, out string originalName) {
    if (data.Length < Magic.Length || !data[..Magic.Length].SequenceEqual(Magic))
      throw new InvalidDataException("CTX: missing 03 'CT001' signature.");
    var pos = Magic.Length;
    var nameEnd = data[pos..].IndexOf((byte)0);
    if (nameEnd < 0) throw new InvalidDataException("CTX: unterminated file name.");
    originalName = Encoding.Latin1.GetString(data.Slice(pos, nameEnd));
    pos += nameEnd + 1;

    var words = new byte[WordCount][];
    for (var i = 0; i < WordCount; ++i) {
      var start = pos;
      var len = 0;
      while (len < 5) {
        if (pos >= data.Length) throw new InvalidDataException("CTX: truncated string table.");
        if (data[pos++] == 0) break;
        ++len;
      }
      words[i] = data.Slice(start, len).ToArray();
    }
    if (pos + 2 * PairCount > data.Length) throw new InvalidDataException("CTX: truncated pair table.");
    var pairs = data.Slice(pos, 2 * PairCount);
    pos += 2 * PairCount;

    var codeToWord = new int[32];
    Array.Fill(codeToWord, -1);
    for (var i = 0; i < WordCount; ++i) codeToWord[WordCode(i)] = i;

    using var output = new MemoryStream(data.Length * 2);
    while (pos < data.Length) {
      var b = data[pos++];
      if (b == 13) {
        output.WriteByte(13);
        output.WriteByte(10);
      } else if (b < 32 && codeToWord[b] >= 0)
        output.Write(words[codeToWord[b]]);
      else if (b is >= 128 and <= 254)
        output.Write(pairs.Slice(2 * (b - 128), 2));
      else if (b == 255) {
        if (pos >= data.Length) throw new InvalidDataException("CTX: escape at end of data.");
        var n = data[pos++];
        if (n is >= 32 and <= 127) {
          if (pos >= data.Length) throw new InvalidDataException("CTX: repeat at end of data.");
          var c = data[pos++];
          for (var k = 0; k < n - 30; ++k) output.WriteByte(c);
        } else
          output.WriteByte(n);
      } else
        output.WriteByte(b);
    }
    return output.ToArray();
  }

  /// <summary>Encodes text with Softdisk's tables; CR LF pairs become the single CR code.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data, string originalName = "") {
    var words = DefaultWords.Select(Encoding.ASCII.GetBytes).ToArray();
    var pairs = Encoding.ASCII.GetBytes(DefaultPairs);
    using var output = new MemoryStream(data.Length + 512);
    output.Write(Magic);
    var name = Encoding.Latin1.GetBytes(originalName.Length > 12 ? originalName[..12] : originalName);
    output.Write(name);
    output.WriteByte(0);
    foreach (var w in words) {
      output.Write(w);
      if (w.Length < 5) output.WriteByte(0);
    }
    output.Write(pairs);

    // Longest strings first, so " the " wins over " the".
    var order = Enumerable.Range(0, WordCount).OrderByDescending(i => words[i].Length).ToArray();
    var pos = 0;
    while (pos < data.Length) {
      var b = data[pos];
      if (b == 13 && pos + 1 < data.Length && data[pos + 1] == 10) {
        output.WriteByte(13);
        pos += 2;
        continue;
      }
      var matched = false;
      foreach (var i in order) {
        var w = words[i];
        if (w.Length > 0 && data[pos..].StartsWith(w)) {
          output.WriteByte(WordCode(i));
          pos += w.Length;
          matched = true;
          break;
        }
      }
      if (matched) continue;
      if (pos + 1 < data.Length) {
        var pair = -1;
        for (var i = 0; i < PairCount; ++i)
          if (pairs[2 * i] == b && pairs[2 * i + 1] == data[pos + 1]) { pair = i; break; }
        if (pair >= 0) {
          output.WriteByte((byte)(128 + pair));
          pos += 2;
          continue;
        }
      }
      var run = 1;
      while (run < 97 && pos + run < data.Length && data[pos + run] == b) ++run;
      if (run >= 4) {
        output.WriteByte(255);
        output.WriteByte((byte)(run + 30));
        output.WriteByte(b);
        pos += run;
        continue;
      }
      if (b is >= 32 and <= 127)
        output.WriteByte(b);
      else {
        output.WriteByte(255);
        output.WriteByte(b);
      }
      ++pos;
    }
    return output.ToArray();
  }
}
