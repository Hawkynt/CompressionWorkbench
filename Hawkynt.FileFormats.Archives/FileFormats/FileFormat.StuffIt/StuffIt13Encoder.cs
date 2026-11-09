namespace FileFormat.StuffIt;

/// <summary>
/// Encoder for StuffIt method 13 (LZ+Huffman), the inverse of <see cref="StuffIt13Decoder"/>.
/// </summary>
/// <remarks>
/// <para>
/// The stream starts with the selector byte <c>0x0F</c>: code set 0 (codes transmitted in the
/// stream), one literal/length code shared by both states (bit 3), and a 17-entry distance code
/// (low bits 7 + 10), which reaches the whole 64 KiB window. Each code is sent as one meta-code
/// symbol per entry: <c>length − 1</c> for a used symbol, 31 for an unused one. The meta code is
/// the fixed 37-symbol code the decoder holds.
/// </para>
/// <para>
/// Matches come from a hash-chained search over the 64 KiB window. Lengths 3–64 are symbols
/// 0x100–0x13D. Symbol 0x13E adds 10 extra bits (65–1088) and 0x13F adds 15 (up to 32832). A
/// distance d is sent as its bit length b (0 for d = 1, else ⌊log2(d − 1)⌋ + 1) followed by the
/// b − 1 low bits of d − 1. Symbol 0x140 ends the stream. Every code is canonical, with
/// lengths limited to 15, and its bits are written first-read-first into a least-significant-bit-
/// first stream.
/// </para>
/// </remarks>
internal static class StuffIt13Encoder {
  private const int WindowSize = 65536;
  private const int MinMatch = 3;
  private const int MaxMatch = 65 + (1 << 15) - 1;
  private const int SymbolCount = 321;
  private const int EndOfStream = 0x140;
  private const int DistanceCodes = 17;
  private const int MaxCodeLength = 15;
  private const int HashBits = 15;
  private const int MaxChain = 128;

  // the decoder's fixed meta code (XADStuffIt13Handle.m), first-read bit = bit 0
  private static readonly int[] MetaCodes = [
    0x5d8, 0x058, 0x040, 0x0c0, 0x000, 0x078, 0x02b, 0x014,
    0x00c, 0x01c, 0x01b, 0x00b, 0x010, 0x020, 0x038, 0x018,
    0x0d8, 0xbd8, 0x180, 0x680, 0x380, 0xf80, 0x780, 0x480,
    0x080, 0x280, 0x3d8, 0xfd8, 0x7d8, 0x9d8, 0x1d8, 0x004,
    0x001, 0x002, 0x007, 0x003, 0x008,
  ];

  private static readonly int[] MetaCodeLengths = [
    11, 8, 8, 8, 8, 7, 6, 5, 5, 5, 5, 6, 5, 6, 7, 7, 9, 12, 10, 11, 11, 12,
    12, 11, 11, 11, 12, 12, 12, 12, 12, 5, 2, 2, 3, 4, 5,
  ];

  private readonly record struct Token(int Symbol, int Extra, int ExtraBits, int Distance);

  /// <summary>Compresses <paramref name="data"/> into a method-13 stream.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    var tokens = Parse(data);

    var symbolFreq = new int[SymbolCount];
    var distanceFreq = new int[DistanceCodes];
    foreach (var t in tokens) {
      ++symbolFreq[t.Symbol];
      if (t.Distance > 0) ++distanceFreq[DistanceBits(t.Distance)];
    }
    ++symbolFreq[EndOfStream];

    var symbolLengths = CodeLengths(symbolFreq);
    var distanceLengths = CodeLengths(distanceFreq);
    var symbolCodes = Canonical(symbolLengths);
    var distanceCodes = Canonical(distanceLengths);

    var w = new BitWriter();
    w.Byte(0x0F);
    WriteTable(w, symbolLengths);
    WriteTable(w, distanceLengths);

    foreach (var t in tokens) {
      w.Code(symbolCodes[t.Symbol], symbolLengths[t.Symbol]);
      if (t.ExtraBits > 0) w.Bits(t.Extra, t.ExtraBits);
      if (t.Distance == 0) continue;
      var b = DistanceBits(t.Distance);
      w.Code(distanceCodes[b], distanceLengths[b]);
      if (b > 1) w.Bits(t.Distance - 1 - (1 << (b - 1)), b - 1);
    }
    w.Code(symbolCodes[EndOfStream], symbolLengths[EndOfStream]);
    return w.ToArray();
  }

  private static int DistanceBits(int distance) => distance switch {
    1 => 0,
    _ => 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(distance - 1)),
  };

  private static void WriteTable(BitWriter w, int[] lengths) {
    foreach (var l in lengths) {
      var meta = l == 0 ? 31 : l - 1;
      w.Bits(MetaCodes[meta], MetaCodeLengths[meta]);
    }
  }

  // ── LZ parse ─────────────────────────────────────────────────────────────

  private static List<Token> Parse(ReadOnlySpan<byte> data) {
    var tokens = new List<Token>(data.Length / 2 + 16);
    var head = new int[1 << HashBits];
    Array.Fill(head, -1);
    var previous = new int[WindowSize];
    var i = 0;
    while (i < data.Length) {
      var (length, distance) = LongestMatch(data, i, head, previous);
      if (length >= MinMatch) {
        tokens.Add(LengthToken(length, distance));
        for (var k = 0; k < length; ++k) Insert(data, i + k, head, previous);
        i += length;
      } else {
        tokens.Add(new Token(data[i], 0, 0, 0));
        Insert(data, i, head, previous);
        ++i;
      }
    }
    return tokens;
  }

  private static Token LengthToken(int length, int distance) => length switch {
    <= 64 => new Token(0x100 + length - MinMatch, 0, 0, distance),
    <= 65 + 1023 => new Token(0x13E, length - 65, 10, distance),
    _ => new Token(0x13F, length - 65, 15, distance),
  };

  private static int Hash(ReadOnlySpan<byte> data, int i) => ((data[i] << 10) ^ (data[i + 1] << 5) ^ data[i + 2]) & ((1 << HashBits) - 1);

  private static void Insert(ReadOnlySpan<byte> data, int i, int[] head, int[] previous) {
    if (i + MinMatch > data.Length) return;
    var h = Hash(data, i);
    previous[i & (WindowSize - 1)] = head[h];
    head[h] = i;
  }

  private static (int Length, int Distance) LongestMatch(ReadOnlySpan<byte> data, int i, int[] head, int[] previous) {
    if (i + MinMatch > data.Length) return (0, 0);
    var limit = Math.Min(MaxMatch, data.Length - i);
    int bestLength = 0, bestDistance = 0;
    var candidate = head[Hash(data, i)];
    for (var chain = 0; candidate >= 0 && chain < MaxChain; ++chain) {
      var distance = i - candidate;
      if (distance > WindowSize) break;
      if (data[candidate + bestLength] == data[i + bestLength]) {
        var length = 0;
        while (length < limit && data[candidate + length] == data[i + length]) ++length;
        if (length > bestLength) {
          bestLength = length;
          bestDistance = distance;
          if (length == limit) break;
        }
      }
      var next = previous[candidate & (WindowSize - 1)];
      if (next >= candidate) break; // the slot was reused by a newer position
      candidate = next;
    }
    return (bestLength, bestDistance);
  }

  // ── Huffman ──────────────────────────────────────────────────────────────

  /// <summary>Huffman code lengths limited to <see cref="MaxCodeLength"/>; a lone symbol gets length 1.</summary>
  internal static int[] CodeLengths(int[] frequencies) {
    var lengths = new int[frequencies.Length];
    var used = Enumerable.Range(0, frequencies.Length).Where(s => frequencies[s] > 0).ToList();
    if (used.Count == 0) return lengths;
    if (used.Count == 1) {
      lengths[used[0]] = 1;
      return lengths;
    }
    var scaled = (int[])frequencies.Clone();
    for (;;) {
      var queue = new PriorityQueue<int, long>();
      var parent = new List<int>();
      var weight = new List<long>();
      foreach (var s in used) {
        queue.Enqueue(parent.Count, scaled[s]);
        parent.Add(-1);
        weight.Add(scaled[s]);
      }
      while (queue.Count > 1) {
        queue.TryDequeue(out var a, out var wa);
        queue.TryDequeue(out var b, out var wb);
        var node = parent.Count;
        parent.Add(-1);
        weight.Add(wa + wb);
        parent[a] = node;
        parent[b] = node;
        queue.Enqueue(node, wa + wb);
      }
      var tooLong = false;
      for (var k = 0; k < used.Count; ++k) {
        var depth = 0;
        for (var n = k; parent[n] >= 0; n = parent[n]) ++depth;
        lengths[used[k]] = depth;
        tooLong |= depth > MaxCodeLength;
      }
      if (!tooLong) return lengths;
      foreach (var s in used) scaled[s] = (scaled[s] + 1) / 2 + 1;
    }
  }

  /// <summary>Canonical codes as the decoder builds them: shorter first, ties by symbol, starting from all zeros.</summary>
  internal static uint[] Canonical(int[] lengths) {
    var codes = new uint[lengths.Length];
    uint next = 0;
    var remaining = lengths.Count(l => l > 0);
    for (var length = 1; length <= 32 && remaining > 0; ++length) {
      for (var s = 0; s < lengths.Length; ++s) {
        if (lengths[s] != length) continue;
        codes[s] = next++;
        --remaining;
      }
      next <<= 1;
    }
    return codes;
  }

  private sealed class BitWriter {
    private readonly List<byte> _bytes = [];
    private int _current;
    private int _count;

    public void Byte(int value) => this.Bits(value, 8);

    /// <summary>Writes <paramref name="count"/> bits, bit 0 first.</summary>
    public void Bits(int value, int count) {
      for (var i = 0; i < count; ++i) this.Bit((value >> i) & 1);
    }

    /// <summary>Writes a code whose first-read bit is its most significant.</summary>
    public void Code(uint code, int length) {
      for (var i = length - 1; i >= 0; --i) this.Bit((int)((code >> i) & 1));
    }

    private void Bit(int bit) {
      this._current |= bit << this._count;
      if (++this._count < 8) return;
      this._bytes.Add((byte)this._current);
      this._current = this._count = 0;
    }

    public byte[] ToArray() {
      if (this._count > 0) this._bytes.Add((byte)this._current);
      return [.. this._bytes];
    }
  }
}
