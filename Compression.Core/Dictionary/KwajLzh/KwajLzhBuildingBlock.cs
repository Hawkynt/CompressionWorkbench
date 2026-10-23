using Compression.Core.Entropy.Huffman;
using Compression.Registry;

namespace Compression.Core.Dictionary.KwajLzh;

/// <summary>
/// KWAJ method 3 ("LZ + Huffman"), the strongest mode of Microsoft's MS-DOS
/// <c>COMPRESS.EXE -Z</c>: LZSS over a 4 KiB ring with five canonical Huffman tables.
/// </summary>
/// <remarks>
/// <para>
/// Converted from libmspack's <c>kwajd.c</c> (method 3 written by Jeff Johnson,
/// © 2003-2023 Stuart Caie, LGPL-2.1), see THIRD-PARTY-NOTICES.md. Bits are read
/// MSB first. The stream opens with six 4-bit table-encoding types (the sixth is
/// padding), followed by the code lengths of MATCHLEN1 (16 symbols), MATCHLEN2 (16),
/// LITLEN (32), OFFSET (64) and LITERAL (256), each in one of four encodings:
/// 0 = flat, 1 = delta with "same / +1 / explicit", 2 = delta with "−1 / same / +1 /
/// explicit", 3 = explicit 4-bit lengths.
/// </para>
/// <para>
/// Tokens: a match-length symbol <c>n</c> &gt; 0 copies <c>n + 2</c> bytes from
/// distance <c>(OFFSET &lt;&lt; 6) | 6 raw bits</c>; <c>n</c> = 0 opens a literal run of
/// <c>LITLEN + 1</c> LITERAL symbols. The length symbol after a run shorter than 32
/// comes from MATCHLEN2, otherwise from MATCHLEN1. The ring starts filled with spaces.
/// </para>
/// <para>
/// There is no end marker: the stream ends where its bytes do. The decoder follows
/// libmspack exactly — it may read zero bits past the end, abandons the token that
/// needed them, and stops after the token during which the end was first touched —
/// so the encoder chooses its tail padding by running that decoder over its own
/// output, which makes every stream it writes decode identically in libmspack.
/// </para>
/// </remarks>
public sealed class KwajLzhBuildingBlock : IBuildingBlock {

  /// <inheritdoc/>
  public string Id => "BB_KwajLzh";
  /// <inheritdoc/>
  public string DisplayName => "KWAJ LZH";
  /// <inheritdoc/>
  public string Description => "MS-DOS COMPRESS.EXE -Z (KWAJ method 3): 4 KiB LZSS with five canonical Huffman tables";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Classic;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => Encode(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => Decode(data);

  private const int WindowSize = 4096;
  private const int WindowMask = WindowSize - 1;
  private const byte WindowFill = 0x20;
  private const int MaxCodeLength = 15;
  private const int MinMatch = 3;
  private const int MaxMatch = 17;
  private const int MaxLiteralRun = 32;

  private const int MatchLen1Syms = 16;
  private const int MatchLen2Syms = 16;
  private const int LitLenSyms = 32;
  private const int OffsetSyms = 64;
  private const int LiteralSyms = 256;

  private static int FlatLength(int symbols) => symbols switch { 16 => 4, 32 => 5, 64 => 6, 256 => 8, _ => 0 };

  // ── decoder ─────────────────────────────────────────────────────────

  /// <summary>Thrown internally when a read consumed bits past the end of the input.</summary>
  private sealed class EndOfInput : Exception;

  /// <summary>MSB-first reader with libmspack's end-of-input accounting.</summary>
  private ref struct Reader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos;
    private ulong _buffer;      // left-aligned
    private int _bitsLeft;
    private int _inputEnd;      // 0 until the input ran out, then 8 per fake byte injected

    public readonly bool InputEnded => this._inputEnd != 0;

    private void Ensure(int n) {
      while (this._bitsLeft < n) {
        byte b;
        if (this._pos < this._data.Length)
          b = this._data[this._pos++];
        else {
          this._inputEnd += 8;
          b = 0;
        }
        this._buffer |= (ulong)b << (56 - this._bitsLeft);
        this._bitsLeft += 8;
      }
    }

    private readonly void CheckFake() {
      if (this._inputEnd != 0 && this._bitsLeft < this._inputEnd)
        throw new EndOfInput();
    }

    public int ReadBits(int n) {
      this.Ensure(n);
      var v = (int)(this._buffer >> (64 - n));
      this._buffer <<= n;
      this._bitsLeft -= n;
      this.CheckFake();
      return v;
    }

    public int ReadSymbol(Table table) {
      this.Ensure(16); // HUFF_MAXBITS
      var entry = table.Lookup[(int)(this._buffer >> (64 - table.Bits))];
      var len = entry >> 9;
      this._buffer <<= len;
      this._bitsLeft -= len;
      this.CheckFake();
      return entry & 0x1FF;
    }
  }

  /// <summary>A canonical decoding table indexed by the next <see cref="Bits"/> bits.</summary>
  private sealed class Table {
    public required int Bits { get; init; }
    public required ushort[] Lookup { get; init; } // (length << 9) | symbol
  }

  private static Table BuildTable(ReadOnlySpan<byte> lengths) {
    var maxLen = 0;
    foreach (var l in lengths) maxLen = Math.Max(maxLen, l);
    if (maxLen == 0)
      throw new InvalidDataException("KWAJ LZH: Huffman table has no codes.");

    var lookup = new ushort[1 << maxLen];
    var code = 0;
    for (var len = 1; len <= maxLen; ++len) {
      for (var sym = 0; sym < lengths.Length; ++sym) {
        if (lengths[sym] != len) continue;
        var span = 1 << (maxLen - len);
        var start = code << (maxLen - len);
        if (start + span > lookup.Length)
          throw new InvalidDataException("KWAJ LZH: Huffman table is over-subscribed.");
        lookup.AsSpan(start, span).Fill((ushort)((len << 9) | sym));
        ++code;
      }
      code <<= 1;
    }
    if (code >> 1 != 1 << maxLen)
      throw new InvalidDataException("KWAJ LZH: Huffman table is incomplete.");
    return new() { Bits = maxLen, Lookup = lookup };
  }

  private static Table ReadTable(ref Reader reader, int type, int symbols) {
    var lens = new byte[symbols];
    switch (type) {
      case 0:
        lens.AsSpan().Fill((byte)FlatLength(symbols));
        break;
      case 1: {
        var c = reader.ReadBits(4);
        lens[0] = (byte)c;
        for (var i = 1; i < symbols; ++i) {
          if (reader.ReadBits(1) == 0) lens[i] = (byte)c;
          else if (reader.ReadBits(1) == 0) lens[i] = (byte)++c;
          else lens[i] = (byte)(c = reader.ReadBits(4));
        }
        break;
      }
      case 2: {
        var c = reader.ReadBits(4);
        lens[0] = (byte)c;
        for (var i = 1; i < symbols; ++i) {
          var sel = reader.ReadBits(2);
          c = sel == 3 ? reader.ReadBits(4) : c + sel - 1;
          lens[i] = (byte)c;
        }
        break;
      }
      case 3:
        for (var i = 0; i < symbols; ++i)
          lens[i] = (byte)reader.ReadBits(4);
        break;
      default:
        // libmspack leaves the lengths untouched for an unknown type; with a fresh
        // table that is all zeros, which it then rejects.
        throw new InvalidDataException($"KWAJ LZH: unknown table encoding {type}.");
    }
    return BuildTable(lens);
  }

  /// <summary>Decodes a raw KWAJ method-3 stream (the bytes after the KWAJ header).</summary>
  /// <exception cref="InvalidDataException">A Huffman table is malformed.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> data) {
    var output = new byte[(int)Math.Clamp(data.Length * 3L, 256, Array.MaxLength)];
    var count = 0;
    var reader = new Reader(data);
    Span<byte> window = stackalloc byte[WindowSize];
    window.Fill(WindowFill);
    var pos = 0;
    try {
      Span<int> types = stackalloc int[6];
      for (var i = 0; i < 6; ++i)
        types[i] = reader.ReadBits(4);

      var matchLen1 = ReadTable(ref reader, types[0], MatchLen1Syms);
      var matchLen2 = ReadTable(ref reader, types[1], MatchLen2Syms);
      var litLen = ReadTable(ref reader, types[2], LitLenSyms);
      var offsets = ReadTable(ref reader, types[3], OffsetSyms);
      var literals = ReadTable(ref reader, types[4], LiteralSyms);

      var litRun = false;
      while (!reader.InputEnded) {
        var len = reader.ReadSymbol(litRun ? matchLen2 : matchLen1);
        if (len > 0) {
          len += 2;
          litRun = false;
          var offset = reader.ReadSymbol(offsets) << 6;
          offset |= reader.ReadBits(6);
          if (count + len > output.Length) Array.Resize(ref output, Math.Max(output.Length * 2, count + len));
          while (len-- > 0) {
            var b = window[(pos + WindowSize - offset) & WindowMask];
            window[pos] = b;
            output[count++] = b;
            pos = (pos + 1) & WindowMask;
          }
        } else {
          len = reader.ReadSymbol(litLen) + 1;
          litRun = len != MaxLiteralRun;
          if (count + len > output.Length) Array.Resize(ref output, Math.Max(output.Length * 2, count + len));
          while (len-- > 0) {
            var b = (byte)reader.ReadSymbol(literals);
            window[pos] = b;
            output[count++] = b;
            pos = (pos + 1) & WindowMask;
          }
        }
      }
    } catch (EndOfInput) {
      // The token that needed bits past the end is abandoned, as libmspack does.
    }
    return output.AsSpan(0, count).ToArray();
  }

  // ── encoder ─────────────────────────────────────────────────────────

  private readonly record struct Token(bool IsMatch, int Length, int Offset, int LiteralStart);

  /// <summary>Encodes <paramref name="data"/> as a raw KWAJ method-3 stream.</summary>
  /// <remarks>
  /// The stream has no end marker, so how it ends is decided by trial: the natural
  /// parse is tried with growing tail padding first, then with the final bytes forced
  /// into a literal run, each candidate checked by <see cref="Decode"/> (libmspack's
  /// exact end-of-input behaviour). The first candidate that reproduces the input wins.
  /// </remarks>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    var natural = Parse(data);
    for (var forcedTail = 0; forcedTail <= Math.Min(data.Length, MaxLiteralRun); ++forcedTail) {
      var tokens = forcedTail == 0 ? natural : WithLiteralTail(natural, data.Length - forcedTail, data.Length);
      if (TryEncode(data, tokens) is { } result)
        return result;
    }
    throw new InvalidOperationException("KWAJ LZH: no stream ending reproduces the input.");
  }

  /// <summary>
  /// The natural parse cut at <paramref name="cut"/> (a crossing match is shortened, or
  /// turned into literals when too short), with everything after it as one literal run.
  /// </summary>
  private static List<Token> WithLiteralTail(List<Token> natural, int cut, int total) {
    var result = new List<Token>(natural.Count + 2);
    var pos = 0;
    foreach (var t in natural) {
      if (pos + t.Length <= cut) {
        result.Add(t);
        pos += t.Length;
        continue;
      }
      var keep = cut - pos;
      if (t.IsMatch && keep >= MinMatch)
        result.Add(t with { Length = keep });
      else if (keep > 0)
        result.Add(new(false, keep, 0, pos));
      break;
    }
    result.Add(new(false, total - cut, 0, cut));
    return result;
  }

  private static byte[]? TryEncode(ReadOnlySpan<byte> data, List<Token> tokens) {
    // Frequencies per table, following the decoder's table choice.
    var freq = new[] {
      new long[MatchLen1Syms], new long[MatchLen2Syms], new long[LitLenSyms], new long[OffsetSyms], new long[LiteralSyms],
    };
    var litRun = false;
    foreach (var t in tokens) {
      var ml = freq[litRun ? 1 : 0];
      if (t.IsMatch) {
        ++ml[t.Length - 2];
        ++freq[3][t.Offset >> 6];
        litRun = false;
      } else {
        ++ml[0];
        ++freq[2][t.Length - 1];
        for (var i = 0; i < t.Length; ++i) ++freq[4][data[t.LiteralStart + i]];
        litRun = t.Length != MaxLiteralRun;
      }
    }

    // The tail padding below relies on long codes in the match-length and offset
    // tables; a flat table is skewed so it has some.
    var lens = new int[5][];
    for (var i = 0; i < 5; ++i)
      lens[i] = CompleteLengths(freq[i], skewIfFlat: i is 0 or 1 or 3);
    var codes = lens.Select(CanonicalCodes).ToArray();

    var writer = new BitWriterMsb(data.Length / 2 + 512);
    var types = lens.Select(ChooseTableType).ToArray();
    foreach (var t in types) writer.Write(t, 4);
    writer.Write(0, 4);
    for (var i = 0; i < 5; ++i)
      WriteTable(writer, types[i], lens[i]);

    litRun = false;
    foreach (var t in tokens) {
      var table = litRun ? 1 : 0;
      if (t.IsMatch) {
        writer.Write(codes[table][t.Length - 2], lens[table][t.Length - 2]);
        writer.Write(codes[3][t.Offset >> 6], lens[3][t.Offset >> 6]);
        writer.Write(t.Offset & 63, 6);
        litRun = false;
      } else {
        writer.Write(codes[table][0], lens[table][0]);
        writer.Write(codes[2][t.Length - 1], lens[2][t.Length - 1]);
        for (var i = 0; i < t.Length; ++i) {
          var b = data[t.LiteralStart + i];
          writer.Write(codes[4][b], lens[4][b]);
        }
        litRun = t.Length != MaxLiteralRun;
      }
    }

    // Whatever a decoder reads after the last token must need more bits than remain:
    // the pad spells the longest match-length code, then the longest offset code.
    var lenTable = litRun ? 1 : 0;
    var padSym = LongestCodeSymbol(lens[lenTable], fromSymbol: 1);
    var offSym = LongestCodeSymbol(lens[3], fromSymbol: 0);
    var pad = new BitWriterMsb(8);
    pad.Write(codes[lenTable][padSym], lens[lenTable][padSym]);
    pad.Write(codes[3][offSym], lens[3][offSym]);
    var padBytes = pad.Snapshot(out var padBits);

    var body = writer.Snapshot(out var bodyBits);
    for (var extraBytes = 0; extraBytes <= 3; ++extraBytes) {
      var totalBits = (bodyBits + 7) / 8 * 8 + extraBytes * 8;
      var candidate = new BitWriterMsb(body.Length + 8);
      candidate.Append(body, bodyBits);
      candidate.Append(padBytes, Math.Min(padBits, totalBits - bodyBits));
      var result = candidate.ToArrayPadded(totalBits / 8);
      if (Decode(result).AsSpan().SequenceEqual(data))
        return result;
    }
    return null;
  }
  private static int LongestCodeSymbol(int[] lengths, int fromSymbol) {
    var best = fromSymbol;
    for (var s = fromSymbol; s < lengths.Length; ++s)
      if (lengths[s] > lengths[best]) best = s;
    return best;
  }

  private static List<Token> Parse(ReadOnlySpan<byte> data) {
    var tokens = new List<Token>();
    const int hashBits = 13;
    var head = new int[1 << hashBits];
    var prev = new int[WindowSize];
    Array.Fill(head, -1);

    static int Hash(ReadOnlySpan<byte> d, int i) => ((d[i] << 5) ^ (d[i + 1] << 2) ^ (d[i + 2] * 0x9E5)) & ((1 << hashBits) - 1);

    void Insert(ReadOnlySpan<byte> d, int i) {
      if (i + MinMatch > d.Length) return;
      var h = Hash(d, i);
      prev[i & WindowMask] = head[h];
      head[h] = i;
    }

    (int Length, int Offset) Find(ReadOnlySpan<byte> d, int i) {
      if (i + MinMatch > d.Length) return (0, 0);
      int bestLen = 0, bestOff = 0;
      var maxLen = Math.Min(MaxMatch, d.Length - i);
      var cand = head[Hash(d, i)];
      for (var chain = 0; cand >= 0 && chain < 48; ++chain) {
        var off = i - cand;
        if (off >= WindowSize) break;
        if (d[cand + bestLen] == d[i + bestLen] || bestLen == 0) {
          var l = 0;
          while (l < maxLen && d[cand + l] == d[i + l]) ++l;
          if (l > bestLen) { bestLen = l; bestOff = off; if (l == maxLen) break; }
        }
        var next = prev[cand & WindowMask];
        if (next >= cand) break;
        cand = next;
      }
      return bestLen >= MinMatch ? (bestLen, bestOff) : (0, 0);
    }

    var runStart = 0;
    var runLength = 0;
    void FlushRun(List<Token> list) {
      while (runLength > 0) {
        var n = Math.Min(runLength, MaxLiteralRun);
        list.Add(new(false, n, 0, runStart));
        runStart += n;
        runLength -= n;
      }
    }

    var pos = 0;
    while (pos < data.Length) {
      var (len, off) = Find(data, pos);
      if (len > 0 && len < MaxMatch && pos + 1 < data.Length) {
        // One-step lazy evaluation: prefer a literal when the next position matches longer.
        Insert(data, pos);
        var (nextLen, _) = Find(data, pos + 1);
        if (nextLen > len) {
          if (runLength == 0) runStart = pos;
          ++runLength;
          ++pos;
          continue;
        }
        FlushRun(tokens);
        tokens.Add(new(true, len, off, 0));
        for (var k = 1; k < len; ++k) Insert(data, pos + k);
        pos += len;
        continue;
      }
      if (len > 0) {
        FlushRun(tokens);
        tokens.Add(new(true, len, off, 0));
        for (var k = 0; k < len; ++k) Insert(data, pos + k);
        pos += len;
        continue;
      }
      Insert(data, pos);
      if (runLength == 0) runStart = pos;
      ++runLength;
      ++pos;
    }
    FlushRun(tokens);
    return tokens;
  }

  /// <summary>
  /// Code lengths for every symbol (unused ones get the minimum weight so the tree is
  /// complete, which libmspack requires), limited to 15 bits and repaired to an exact
  /// Kraft sum of one.
  /// </summary>
  private static int[] CompleteLengths(long[] freq, bool skewIfFlat) {
    var weights = new long[freq.Length];
    for (var i = 0; i < freq.Length; ++i) weights[i] = freq[i] * 4 + 1;
    var lengths = DeterministicHuffman.BuildCodeLengths(weights);
    if (skewIfFlat && lengths.All(l => l == lengths[0])) {
      // Give the most frequent symbol half the code space; the rest grow longer.
      var top = Array.IndexOf(weights, weights.Max());
      weights[top] = weights.Sum();
      lengths = DeterministicHuffman.BuildCodeLengths(weights);
    }
    HuffmanTree.LimitCodeLengths(lengths, MaxCodeLength);
    HuffmanTree.CompleteCodeLengths(lengths, MaxCodeLength);
    return lengths;
  }

  private static int[] CanonicalCodes(int[] lengths) {
    var codes = new int[lengths.Length];
    var code = 0;
    for (var len = 1; len <= MaxCodeLength; ++len) {
      for (var s = 0; s < lengths.Length; ++s)
        if (lengths[s] == len) codes[s] = code++;
      code <<= 1;
    }
    return codes;
  }

  private static int TableCost(int type, int[] lens) {
    switch (type) {
      case 0:
        return lens.All(l => l == FlatLength(lens.Length)) ? 0 : int.MaxValue;
      case 1: {
        var bits = 4;
        var c = lens[0];
        for (var i = 1; i < lens.Length; ++i) {
          if (lens[i] == c) bits += 1;
          else if (lens[i] == c + 1) { bits += 2; c++; }
          else { bits += 6; c = lens[i]; }
        }
        return bits;
      }
      case 2: {
        var bits = 4;
        var c = lens[0];
        for (var i = 1; i < lens.Length; ++i) {
          bits += Math.Abs(lens[i] - c) <= 1 ? 2 : 6;
          c = lens[i];
        }
        return bits;
      }
      default:
        return 4 * lens.Length;
    }
  }

  private static int ChooseTableType(int[] lens) {
    var best = 3;
    for (var t = 0; t < 3; ++t)
      if (TableCost(t, lens) < TableCost(best, lens)) best = t;
    return best;
  }

  private static void WriteTable(BitWriterMsb w, int type, int[] lens) {
    switch (type) {
      case 0:
        return;
      case 1: {
        var c = lens[0];
        w.Write(c, 4);
        for (var i = 1; i < lens.Length; ++i) {
          if (lens[i] == c) w.Write(0, 1);
          else if (lens[i] == c + 1) { w.Write(2, 2); c++; }
          else { w.Write(3, 2); w.Write(lens[i], 4); c = lens[i]; }
        }
        return;
      }
      case 2: {
        var c = lens[0];
        w.Write(c, 4);
        for (var i = 1; i < lens.Length; ++i) {
          var d = lens[i] - c;
          if (Math.Abs(d) <= 1) w.Write(d + 1, 2);
          else { w.Write(3, 2); w.Write(lens[i], 4); }
          c = lens[i];
        }
        return;
      }
      default:
        foreach (var l in lens) w.Write(l, 4);
        return;
    }
  }

  /// <summary>Minimal MSB-first bit writer.</summary>
  private sealed class BitWriterMsb(int capacity) {
    private readonly List<byte> _bytes = new(capacity);
    private int _acc;
    private int _accBits;

    public void Write(int value, int bits) {
      this._acc = (this._acc << bits) | (value & ((1 << bits) - 1));
      this._accBits += bits;
      while (this._accBits >= 8) {
        this._accBits -= 8;
        this._bytes.Add((byte)(this._acc >> this._accBits));
      }
      this._acc &= (1 << this._accBits) - 1;
    }

    public void Append(byte[] bytes, int bits) {
      for (var i = 0; i < bits; ++i)
        this.Write((bytes[i >> 3] >> (7 - (i & 7))) & 1, 1);
    }

    public byte[] Snapshot(out int bits) {
      bits = this._bytes.Count * 8 + this._accBits;
      var result = new byte[(bits + 7) / 8];
      this._bytes.CopyTo(result);
      if (this._accBits > 0) result[^1] = (byte)(this._acc << (8 - this._accBits));
      return result;
    }

    public byte[] ToArrayPadded(int byteCount) {
      var bytes = this.Snapshot(out _);
      if (bytes.Length >= byteCount) return bytes;
      var result = new byte[byteCount];
      bytes.CopyTo(result, 0);
      return result;
    }
  }
}
