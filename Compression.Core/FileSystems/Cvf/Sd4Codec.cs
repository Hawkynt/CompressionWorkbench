namespace Compression.Core.FileSystems.Cvf;

/// <summary>
/// Stacker 4 (SD-4, cluster header <c>0x0081</c>) per-cluster codec, implemented from
/// <c>docs/CVF-CODECS-ON-DISK.md</c>. SD-4 is Huffman-coded LZ77: a helper code (code lengths 0..15
/// and six run tokens) transmits the 336 code lengths of the main code, whose symbols are the 256
/// literals, 63 short-match tokens, 16 long-match tokens and the end token. Bits are read MSB-first
/// from little-endian 16-bit words; both codes are canonical and complete.
/// </summary>
public static class Sd4Codec {
  private const int Signature = 0x0081;
  private const int HelperSymbols = 22;          // code lengths 0..15, run tokens 16..21
  private const int MainSymbols = 0x150;         // literals 0..255, tokens 1..80
  private const int MaxCodeLength = 15;
  private const int FirstToken = 0x100 - 1;      // main symbol of token t is FirstToken + t
  private const int FirstLongToken = 64;
  private const int EndToken = 80;
  private const int ShortMaxLength = 5;
  private const int LongMinLength = 6;
  private const int LongTokenMaxLength = 21;
  private const int MaxMatchLength = LongTokenMaxLength + 0xF + 0xFF + 0xFFF + 0xFFFF;   // every extension saturated

  // Short-match distance classes (token t = 1..63: class (t-1)/3, length 3 + (t-1)%3).
  private static readonly int[] ShortBase = [1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536];
  private static readonly int[] ShortBits = [0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9];

  // Long-match distance classes, picked by a 2-bit selector.
  private static readonly int[] LongBase = [1, 33, 161, 673];
  private static readonly int[] LongBits = [5, 7, 9, 11];

  // A long match whose token says 21 carries these extensions, each taken only while the previous one saturated.
  private static readonly int[] LengthExtensionBits = [4, 8, 12, 16];

  private static readonly int ShortMaxDistance = ShortBase[^1] + (1 << ShortBits[^1]) - 1;   // 2047
  private static readonly int LongMaxDistance = LongBase[^1] + (1 << LongBits[^1]) - 1;      // 2720

  // ── decoding ────────────────────────────────────────────────────────────

  /// <summary>
  /// Decodes one SD-4 cluster payload into <paramref name="outLen"/> bytes. A stream that ends (end
  /// token) before the cluster is full leaves the tail zero.
  /// </summary>
  /// <exception cref="InvalidDataException">Bad signature, an inconsistent code table, or a match that reaches before the cluster start.</exception>
  public static byte[] Decode(byte[] payload, int inLen, int outLen) {
    var reader = new MsbWordReader(payload.AsSpan(0, Math.Min(inLen, payload.Length)));
    if (reader.Read(16) != Signature) throw new InvalidDataException("SD-4: bad signature.");

    var main = new CanonicalDecoder(ReadMainLengths(ref reader));
    var output = new byte[outLen];
    var position = 0;
    while (position < outLen) {
      var symbol = main.Decode(ref reader);
      if (symbol <= 0xFF) {
        output[position++] = (byte)symbol;
        continue;
      }

      var token = symbol - FirstToken;
      if (token == EndToken) break;

      int length, distance;
      if (token < FirstLongToken) {
        var cls = (token - 1) / 3;
        length = 3 + (token - 1) % 3;
        distance = ShortBase[cls] + reader.Read(ShortBits[cls]);
      } else {
        var selector = reader.Read(2);
        distance = LongBase[selector] + reader.Read(LongBits[selector]);
        length = token - 58;
        var saturated = LongTokenMaxLength;
        foreach (var bits in LengthExtensionBits) {
          if (length != saturated) break;
          length += reader.Read(bits);
          saturated += (1 << bits) - 1;
        }
      }

      if (distance > position) throw new InvalidDataException("SD-4: match reaches before the start of the cluster.");
      length = Math.Min(length, outLen - position);
      for (var end = position + length; position < end; ++position)
        output[position] = output[position - distance];
    }
    return output;
  }

  private static byte[] ReadMainLengths(ref MsbWordReader reader) {
    var first = reader.Read(3) + 1;
    var last = reader.Read(5);
    if (first > last || last > MaxCodeLength) throw new InvalidDataException("SD-4: helper length range is invalid.");

    var helperLengths = new byte[HelperSymbols];
    helperLengths[0] = (byte)reader.Read(4);
    for (var i = first; i <= last; ++i) helperLengths[i] = (byte)reader.Read(4);
    for (var i = 16; i < HelperSymbols; ++i) helperLengths[i] = (byte)reader.Read(4);
    var helper = new CanonicalDecoder(helperLengths);

    var lengths = new byte[MainSymbols];
    for (var n = 0; n < MainSymbols;) {
      var symbol = helper.Decode(ref reader);
      if (symbol < 16) {
        lengths[n++] = (byte)symbol;
        continue;
      }
      if (symbol >= 19 && n == 0) throw new InvalidDataException("SD-4: length repeat with no previous length.");
      var (value, count) = symbol switch {
        16 => ((byte)0, 2),
        17 => ((byte)0, 3),
        18 => ((byte)0, ReadZeroRun(ref reader)),
        19 => (lengths[n - 1], 2),
        20 => (lengths[n - 1], 3),
        _ => (lengths[n - 1], ReadRepeatRun(ref reader)),
      };
      count = Math.Min(count, MainSymbols - n);
      lengths.AsSpan(n, count).Fill(value);
      n += count;
    }
    return lengths;
  }

  // Helper symbol 18: 4 + 3 bits; a 7 continues with 7-bit groups while they read 0x7F.
  private static int ReadZeroRun(ref MsbWordReader reader) {
    var v = reader.Read(3);
    var n = 4 + v;
    if (v == 7)
      do n += v = reader.Read(7); while (v == 0x7F);
    return n;
  }

  // Helper symbol 21: 4 + 3-bit groups, continuing while a group reads 7.
  private static int ReadRepeatRun(ref MsbWordReader reader) {
    var n = 4;
    int v;
    do n += v = reader.Read(3); while (v == 7);
    return n;
  }

  // ── encoding ────────────────────────────────────────────────────────────

  /// <summary>Encodes one cluster with back-references, Huffman-coded behind a run-length coded code table.</summary>
  /// <param name="data">The cluster.</param>
  /// <param name="level">1 (fastest) and up; deeper match search at higher levels.</param>
  public static byte[] Encode(ReadOnlySpan<byte> data, int level = 2)
    => Encode(data, level, matches: true);

  /// <summary>Encodes with back-references switched off when <paramref name="matches"/> is false — a
  /// literals-only stream whose code depth follows the byte statistics alone (used to probe decoders).</summary>
  internal static byte[] Encode(ReadOnlySpan<byte> data, int level, bool matches) {
    var symbols = matches ? Parse(data, level) : LiteralsOnly(data);

    var mainFrequencies = new long[MainSymbols];
    foreach (var s in symbols) ++mainFrequencies[s.Symbol];
    EnsureTwoSymbols(mainFrequencies, 0);
    var mainLengths = PackageMerge.Lengths(mainFrequencies, MaxCodeLength);

    var runs = RunLengthCode(mainLengths);
    var helperFrequencies = new long[HelperSymbols];
    foreach (var r in runs) ++helperFrequencies[r.Symbol];
    EnsureTwoSymbols(helperFrequencies, helperFrequencies[0] == 0 ? 0 : 16);
    var helperLengths = PackageMerge.Lengths(helperFrequencies, MaxCodeLength);

    var writer = new MsbWordWriter();
    writer.Write(Signature, 16);
    WriteHelperHeader(writer, mainLengths, helperLengths);

    WriteSymbols(writer, runs, helperLengths);
    WriteSymbols(writer, symbols, mainLengths);
    return writer.ToArray();
  }

  /// <summary>A symbol of the main or helper code followed by its extra-bit fields, in stream order.</summary>
  private readonly record struct CodedSymbol(int Symbol, params (int Value, int Bits)[] Extras);

  private static void WriteSymbols(MsbWordWriter writer, List<CodedSymbol> symbols, int[] lengths) {
    var codes = CanonicalCodes(lengths);
    foreach (var s in symbols) {
      writer.Write(codes[s.Symbol], lengths[s.Symbol]);
      foreach (var (value, bits) in s.Extras) writer.Write(value, bits);
    }
  }

  private static List<CodedSymbol> LiteralsOnly(ReadOnlySpan<byte> data) {
    var symbols = new List<CodedSymbol>(data.Length + 1);
    foreach (var b in data) symbols.Add(new(b));
    symbols.Add(new(FirstToken + EndToken));
    return symbols;
  }

  private static List<CodedSymbol> Parse(ReadOnlySpan<byte> data, int level) {
    var bytes = data.ToArray();
    var finder = new ClusterMatchFinder(bytes, LongMaxDistance, MaxMatchLength, level);
    var symbols = new List<CodedSymbol>(bytes.Length / 2 + 2);

    var position = 0;
    var pending = Usable(finder.Find(0));
    while (position < bytes.Length) {
      if (pending.Length == 0) {
        symbols.Add(new(bytes[position], []));
        pending = Usable(finder.Find(++position));
        continue;
      }

      var next = position + 1 < bytes.Length ? Usable(finder.Find(position + 1)) : (Length: 0, Distance: 0);
      if (next.Length > pending.Length) {
        symbols.Add(new(bytes[position], []));
        ++position;
        pending = next;
        continue;
      }

      symbols.Add(MatchSymbol(pending.Length, pending.Distance));
      position += pending.Length;
      pending = position < bytes.Length ? Usable(finder.Find(position)) : (Length: 0, Distance: 0);
    }

    symbols.Add(new(FirstToken + EndToken, []));
    return symbols;
  }

  // Lengths 3..5 only reach as far as the short classes; everything longer may use the long ones.
  private static (int Length, int Distance) Usable((int Length, int Distance) match)
    => match.Length is > 0 and <= ShortMaxLength && match.Distance > ShortMaxDistance ? (0, 0) : match;

  private static CodedSymbol MatchSymbol(int length, int distance) {
    if (length <= ShortMaxLength) {
      var cls = ClassOf(ShortBase, ShortBits, distance);
      var token = 1 + cls * 3 + (length - 3);
      return new(FirstToken + token, [(distance - ShortBase[cls], ShortBits[cls])]);
    }

    var selector = ClassOf(LongBase, LongBits, distance);
    var extras = new List<(int, int)> { (selector, 2), (distance - LongBase[selector], LongBits[selector]) };
    var longToken = FirstLongToken + Math.Min(length, LongTokenMaxLength) - LongMinLength;
    var remaining = length - LongTokenMaxLength;
    foreach (var bits in LengthExtensionBits) {
      if (remaining < 0) break;
      var max = (1 << bits) - 1;
      extras.Add((Math.Min(remaining, max), bits));
      remaining -= max;                          // continues only when this extension saturated
    }
    return new(FirstToken + longToken, [.. extras]);
  }

  private static int ClassOf(int[] bases, int[] bits, int distance) {
    for (var c = bases.Length - 1; c >= 0; --c)
      if (distance >= bases[c])
        return distance < bases[c] + (1 << bits[c]) ? c : throw new ArgumentOutOfRangeException(nameof(distance));
    throw new ArgumentOutOfRangeException(nameof(distance));
  }

  // A complete prefix code needs at least two symbols; give a spare one a nominal count.
  private static void EnsureTwoSymbols(long[] frequencies, int spare) {
    var used = frequencies.Count(f => f > 0);
    for (var s = spare; used < 2 && s < frequencies.Length; ++s)
      if (frequencies[s] == 0) { frequencies[s] = 1; ++used; }
  }

  /// <summary>
  /// Transmits the main code lengths as helper symbols: a length as itself, runs of zeros as tokens
  /// 16/17/18, repeats of the previous length as tokens 19/20/21.
  /// </summary>
  private static List<CodedSymbol> RunLengthCode(int[] lengths) {
    var runs = new List<CodedSymbol>();
    for (var i = 0; i < lengths.Length;) {
      var value = lengths[i];
      var run = 1;
      while (i + run < lengths.Length && lengths[i + run] == value) ++run;
      i += run;

      if (value == 0) {
        runs.Add(run switch {
          1 => new(0),
          2 => new(16),
          3 => new(17),
          < 11 => new(18, (run - 4, 3)),
          _ => new(18, [(7, 3), .. Groups(run - 11, 7)]),
        });
        continue;
      }

      runs.Add(new(value));
      var repeats = run - 1;
      switch (repeats) {
        case 0: break;
        case 1: runs.Add(new(value)); break;
        case 2: runs.Add(new(19)); break;
        case 3: runs.Add(new(20)); break;
        default: runs.Add(new(21, [.. Groups(repeats - 4, 3)])); break;
      }
    }
    return runs;
  }

  /// <summary>
  /// Splits <paramref name="total"/> into <paramref name="bits"/>-bit groups where every group but the
  /// last is saturated (all ones) and the last is not, which is how the decoder knows to stop.
  /// </summary>
  private static IEnumerable<(int Value, int Bits)> Groups(int total, int bits) {
    var saturated = (1 << bits) - 1;
    for (; total >= saturated; total -= saturated) yield return (saturated, bits);
    yield return (total, bits);
  }


  private static void WriteHelperHeader(MsbWordWriter writer, int[] mainLengths, int[] helperLengths) {
    int shortest = MaxCodeLength, longest = 1;
    foreach (var l in mainLengths) {
      if (l == 0) continue;
      shortest = Math.Min(shortest, l);
      longest = Math.Max(longest, l);
    }
    var first = Math.Min(shortest, 8);
    writer.Write(first - 1, 3);
    writer.Write(longest, 5);
    writer.Write(helperLengths[0], 4);
    for (var i = first; i <= longest; ++i) writer.Write(helperLengths[i], 4);
    for (var i = 16; i < HelperSymbols; ++i) writer.Write(helperLengths[i], 4);
  }

  /// <summary>Canonical codes: ascending by length, then by symbol, each the previous plus one.</summary>
  private static int[] CanonicalCodes(int[] lengths) {
    var perLength = new int[MaxCodeLength + 1];
    foreach (var l in lengths) ++perLength[l];
    perLength[0] = 0;
    var next = new int[MaxCodeLength + 2];
    for (var len = 1; len <= MaxCodeLength; ++len) next[len + 1] = (next[len] + perLength[len]) << 1;
    var codes = new int[lengths.Length];
    for (var s = 0; s < lengths.Length; ++s)
      if (lengths[s] != 0) codes[s] = next[lengths[s]]++;
    return codes;
  }

  /// <summary>
  /// Canonical MSB-first prefix code decoder. The code must be complete (Kraft sum exactly one) with
  /// lengths of at most 15 bits.
  /// </summary>
  private sealed class CanonicalDecoder {
    private readonly int[] _perLength = new int[MaxCodeLength + 1];
    private readonly int[] _symbols;

    public CanonicalDecoder(ReadOnlySpan<byte> lengths) {
      foreach (var l in lengths) {
        if (l > MaxCodeLength) throw new InvalidDataException("SD-4: code length above 15.");
        ++this._perLength[l];
      }
      this._perLength[0] = 0;
      var free = 1;
      for (var len = 1; len <= MaxCodeLength; ++len) {
        free = (free << 1) - this._perLength[len];
        if (free < 0) throw new InvalidDataException("SD-4: over-subscribed code table.");
      }
      if (free != 0) throw new InvalidDataException("SD-4: incomplete code table.");

      var offsets = new int[MaxCodeLength + 2];
      for (var len = 1; len <= MaxCodeLength; ++len) offsets[len + 1] = offsets[len] + this._perLength[len];
      this._symbols = new int[offsets[MaxCodeLength + 1]];
      for (var s = 0; s < lengths.Length; ++s)
        if (lengths[s] != 0) this._symbols[offsets[lengths[s]]++] = s;
    }

    public int Decode(ref MsbWordReader reader) {
      int code = 0, first = 0, index = 0;
      for (var len = 1; len <= MaxCodeLength; ++len) {
        code |= reader.Read(1);
        var count = this._perLength[len];
        if (code - first < count) return this._symbols[index + code - first];
        index += count;
        first = (first + count) << 1;
        code <<= 1;
      }
      throw new InvalidDataException("SD-4: undecodable bit sequence.");
    }
  }

  /// <summary>Reads bits MSB-first from little-endian 16-bit words; zeros past the end.</summary>
  private ref struct MsbWordReader {
    private readonly ReadOnlySpan<byte> _data;
    private int _next;
    private uint _buffer;
    private int _count;

    public MsbWordReader(ReadOnlySpan<byte> data) => this._data = data;

    public int Read(int count) {
      if (count == 0) return 0;
      while (this._count < count) {
        var lo = this._next < this._data.Length ? this._data[this._next] : 0;
        var hi = this._next + 1 < this._data.Length ? this._data[this._next + 1] : 0;
        this._next += 2;
        this._buffer = this._buffer << 16 | (uint)(lo | hi << 8);
        this._count += 16;
      }
      this._count -= count;
      return (int)(this._buffer >> this._count) & ((1 << count) - 1);
    }
  }

  /// <summary>Writes bits MSB-first into little-endian 16-bit words.</summary>
  private sealed class MsbWordWriter {
    private readonly List<byte> _bytes = [];
    private int _word;
    private int _count;

    public void Write(int value, int count) {
      for (var i = count - 1; i >= 0; --i) {
        this._word = this._word << 1 | (value >> i & 1);
        if (++this._count < 16) continue;
        this._bytes.Add((byte)this._word);
        this._bytes.Add((byte)(this._word >> 8));
        (this._word, this._count) = (0, 0);
      }
    }

    public byte[] ToArray() {
      if (this._count > 0) this.Write(0, 16 - this._count);
      return [.. this._bytes];
    }
  }
}
