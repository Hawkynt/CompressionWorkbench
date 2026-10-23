using Compression.Core.Entropy.Huffman;
using Compression.Registry;

namespace Compression.Core.Dictionary.ArcDistill;

/// <summary>
/// PAK "Distill" (ARC/PAK method 11, NoGate PAK 2.x): LZSS over an 8 KiB window with one
/// transmitted Huffman tree for literals, lengths and the end code, and a fixed prefix
/// code for offset high bits whose low-bit count grows with the output position.
/// </summary>
/// <remarks>
/// <para>
/// Layout, bits least-significant first: a 16-bit node count <c>N</c> (2…0x274), an 8-bit
/// field width <c>w</c>, then <c>N</c> numbers of <c>w</c> bits forming the tree — entry
/// pairs <c>(n, n+1)</c> are the 0- and 1-child of internal node <c>n</c>, the root is the
/// pair at <c>N − 2</c>, and a child value <c>v ≥ N</c> is the leaf symbol <c>v − N</c>.
/// Symbols 0–255 are literals, 256 ends the data, <c>s ≥ 257</c> copies <c>s − 254</c> bytes.
/// After a length, a fixed 64-symbol offset code gives the high part; then 0–7 raw bits,
/// as many as the output position needs (0 below 4, 7 from 4036 on); distance =
/// <c>(high &lt;&lt; bits) + raw + 1</c>.
/// </para>
/// <para>
/// Converted from The Unarchiver's <c>XADARCDistillHandle</c> (XADMaster, LGPL-2.1); the
/// offset code is copied exactly. See THIRD-PARTY-NOTICE.XADMaster.txt. The encoder is original.
/// </para>
/// </remarks>
public sealed class ArcDistillBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_ArcDistill";
  /// <inheritdoc/>
  public string DisplayName => "PAK Distill";
  /// <inheritdoc/>
  public string Description => "PAK/ARC method 11: 8 KiB LZSS with a transmitted Huffman tree and position-scaled offsets";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => Encode(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => Decode(data, int.MaxValue);

  private const int EndSymbol = 256;
  private const int MinMatch = 3;
  private const int MaxMatch = 64;
  private const int MaxNodes = 0x274;
  private const int WindowSize = 8192;

  // Offset prefix code: codes are given least-significant bit first.
  private static readonly int[] OffsetLengths = [
    3, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5, 6, 6, 6, 6,
    6, 6, 6, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7,
    7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
    8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
  ];

  private static readonly int[] OffsetCodes = [
    0x00, 0x02, 0x04, 0x0c, 0x01, 0x06, 0x0a, 0x0e,
    0x11, 0x16, 0x1a, 0x1e, 0x05, 0x09, 0x0d, 0x15,
    0x19, 0x1d, 0x25, 0x29, 0x2d, 0x35, 0x39, 0x3d,
    0x03, 0x07, 0x0b, 0x13, 0x17, 0x1b, 0x23, 0x27,
    0x2b, 0x33, 0x37, 0x3b, 0x43, 0x47, 0x4b, 0x53,
    0x57, 0x5b, 0x63, 0x67, 0x6b, 0x73, 0x77, 0x7b,
    0x0f, 0x1f, 0x2f, 0x3f, 0x4f, 0x5f, 0x6f, 0x7f,
    0x8f, 0x9f, 0xaf, 0xbf, 0xcf, 0xdf, 0xef, 0xff,
  ];

  /// <summary>(length, LSB-first code) → offset symbol.</summary>
  private static readonly Dictionary<(int, int), int> OffsetLookup = BuildOffsetLookup();

  private static Dictionary<(int, int), int> BuildOffsetLookup() {
    var d = new Dictionary<(int, int), int>();
    for (var i = 0; i < 64; ++i) d[(OffsetLengths[i], OffsetCodes[i])] = i;
    return d;
  }

  /// <summary>Raw offset bits for a match starting at output <paramref name="position"/>.</summary>
  private static int ExtraBits(long position) => position switch {
    >= 0x1000 - 0x3c => 7,
    >= 0x800 - 0x3c => 6,
    >= 0x400 - 0x3c => 5,
    >= 0x200 - 0x3c => 4,
    >= 0x100 - 0x3c => 3,
    >= 0x80 - 0x3c => 2,
    >= 0x40 - 0x3c => 1,
    _ => 0,
  };

  // ── decoder ─────────────────────────────────────────────────────────

  private ref struct Reader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private long _bit;

    public int Bit() {
      if (this._bit >= (long)this._data.Length * 8)
        throw new InvalidDataException("Distill: data ends before the end code.");
      var b = (this._data[(int)(this._bit >> 3)] >> (int)(this._bit & 7)) & 1;
      ++this._bit;
      return b;
    }

    public int Bits(int n) {
      var v = 0;
      for (var i = 0; i < n; ++i) v |= this.Bit() << i;
      return v;
    }
  }

  /// <summary>
  /// Decodes a Distill stream, stopping at its end code or after
  /// <paramref name="maxOutput"/> bytes, whichever comes first.
  /// </summary>
  /// <exception cref="InvalidDataException">The tree is malformed or the data truncated.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> data, int maxOutput) {
    var r = new Reader(data);
    var numNodes = r.Bits(16);
    var width = r.Bits(8);
    if (numNodes is < 2 or > MaxNodes || width is < 1 or > 16)
      throw new InvalidDataException($"Distill: tree header out of range ({numNodes} nodes, {width}-bit fields).");
    var nodes = new int[numNodes];
    for (var i = 0; i < numNodes; ++i) nodes[i] = r.Bits(width);
    var root = numNodes - 2;

    var output = new byte[Math.Min(maxOutput, Math.Max(256, data.Length * 4))];
    var pos = 0;
    while (pos < maxOutput) {
      // Walk the transmitted tree; a value below numNodes points at a child pair.
      var node = root;
      var depth = 0;
      int symbol;
      while (true) {
        var next = nodes[node + r.Bit()];
        if (next >= numNodes) { symbol = next - numNodes; break; }
        if (next + 1 >= numNodes || ++depth > 64)
          throw new InvalidDataException("Distill: malformed Huffman tree.");
        node = next;
      }

      if (symbol < 256) {
        Grow(ref output, pos + 1, maxOutput);
        output[pos++] = (byte)symbol;
        continue;
      }
      if (symbol == EndSymbol)
        break;

      var length = symbol - 0x101 + 3;
      var code = 0;
      var offsetSymbol = -1;
      for (var len = 1; len <= 8; ++len) {
        code |= r.Bit() << (len - 1);
        if (OffsetLookup.TryGetValue((len, code), out offsetSymbol)) break;
      }
      if (offsetSymbol < 0)
        throw new InvalidDataException("Distill: invalid offset code.");
      var extra = ExtraBits(pos);
      var distance = (offsetSymbol << extra) + r.Bits(extra) + 1;
      if (distance > pos)
        throw new InvalidDataException("Distill: match reaches before the start of the data.");
      var end = Math.Min(maxOutput, pos + length);
      Grow(ref output, end, maxOutput);
      for (; pos < end; ++pos) output[pos] = output[pos - distance];
    }
    return output.AsSpan(0, pos).ToArray();
  }

  private static void Grow(ref byte[] buffer, int needed, int max) {
    if (needed <= buffer.Length) return;
    var size = (int)Math.Min(max, Math.Max(needed, (long)buffer.Length * 2));
    Array.Resize(ref buffer, size);
  }

  // ── encoder ─────────────────────────────────────────────────────────

  private readonly record struct Token(int Symbol, int Distance, int Position);

  /// <summary>Encodes <paramref name="data"/> as a Distill stream.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    var tokens = Parse(data);
    var maxSymbol = 0x101 + MaxMatch - 3;
    var freq = new long[maxSymbol + 1];
    foreach (var t in tokens) ++freq[t.Symbol];
    if (freq.Count(f => f > 0) < 2) freq[0] = Math.Max(freq[0], 1); // a tree needs two leaves

    var lengths = DeterministicHuffman.BuildCodeLengths(freq);
    HuffmanTree.LimitCodeLengths(lengths, 32);

    // Canonical codes, then the explicit tree they describe.
    var codes = new uint[lengths.Length];
    uint c = 0;
    for (var len = 1; len <= 32; ++len) {
      for (var s = 0; s < lengths.Length; ++s)
        if (lengths[s] == len) codes[s] = c++;
      c <<= 1;
    }

    // Tree nodes in memory: children[node] = (left, right); leaves are ~symbol.
    var left = new List<int> { 0 };
    var right = new List<int> { 0 };
    void Insert(int symbol, uint code, int length) {
      var node = 0;
      for (var i = length - 1; i >= 0; --i) {
        var bit = (code >> i) & 1;
        var list = bit == 0 ? left : right;
        if (i == 0) { list[node] = ~symbol; return; }
        if (list[node] == 0) {
          left.Add(0);
          right.Add(0);
          list[node] = left.Count - 1;
        }
        node = list[node];
      }
    }
    for (var s = 0; s < lengths.Length; ++s)
      if (lengths[s] > 0) Insert(s, codes[s], lengths[s]);

    // Slots: post-order, so the root's pair is last (index numNodes - 2).
    var internalCount = left.Count;
    var numNodes = 2 * internalCount;
    if (numNodes > MaxNodes)
      throw new InvalidOperationException("Distill: tree exceeds the format's node limit.");
    var slot = new int[internalCount];
    var next = 0;
    void Assign(int node) {
      if (left[node] > 0) Assign(left[node]);
      if (right[node] > 0) Assign(right[node]);
      slot[node] = next;
      next += 2;
    }
    Assign(0);
    var table = new int[numNodes];
    for (var n = 0; n < internalCount; ++n) {
      table[slot[n]] = left[n] < 0 ? numNodes + ~left[n] : slot[left[n]];
      table[slot[n] + 1] = right[n] < 0 ? numNodes + ~right[n] : slot[right[n]];
    }
    var width = 1;
    while ((1 << width) <= numNodes + maxSymbol) ++width;

    var w = new LsbWriter(data.Length / 2 + 1024);
    w.Write(numNodes, 16);
    w.Write(width, 8);
    foreach (var v in table) w.Write(v, width);
    foreach (var t in tokens) {
      for (var i = lengths[t.Symbol] - 1; i >= 0; --i) w.Write((int)((codes[t.Symbol] >> i) & 1), 1);
      if (t.Symbol <= EndSymbol) continue;
      var extra = ExtraBits(t.Position);
      var high = (t.Distance - 1) >> extra;
      w.Write(OffsetCodes[high], OffsetLengths[high]);
      w.Write((t.Distance - 1) & ((1 << extra) - 1), extra);
    }
    return w.ToArray();
  }

  private static List<Token> Parse(ReadOnlySpan<byte> data) {
    var tokens = new List<Token>(data.Length / 2 + 2);
    const int hashBits = 14;
    var head = new int[1 << hashBits];
    var prev = new int[WindowSize];
    Array.Fill(head, -1);
    static int Hash(ReadOnlySpan<byte> d, int i) => ((d[i] << 6) ^ (d[i + 1] << 3) ^ d[i + 2]) & ((1 << hashBits) - 1);
    void Insert(ReadOnlySpan<byte> d, int i) {
      if (i + MinMatch > d.Length) return;
      var h = Hash(d, i);
      prev[i & (WindowSize - 1)] = head[h];
      head[h] = i;
    }

    var pos = 0;
    while (pos < data.Length) {
      int bestLen = 0, bestDist = 0;
      if (pos + MinMatch <= data.Length) {
        var maxDist = Math.Min(WindowSize, 64 << ExtraBits(pos));
        var maxLen = Math.Min(MaxMatch, data.Length - pos);
        var cand = head[Hash(data, pos)];
        for (var chain = 0; cand >= 0 && chain < 64; ++chain) {
          var dist = pos - cand;
          if (dist > maxDist) break;
          var l = 0;
          while (l < maxLen && data[cand + l] == data[pos + l]) ++l;
          if (l > bestLen) { bestLen = l; bestDist = dist; if (l == maxLen) break; }
          var nextCand = prev[cand & (WindowSize - 1)];
          if (nextCand >= cand) break;
          cand = nextCand;
        }
      }
      if (bestLen >= MinMatch) {
        tokens.Add(new(0x101 + bestLen - 3, bestDist, pos));
        for (var k = 0; k < bestLen; ++k) Insert(data, pos + k);
        pos += bestLen;
      } else {
        tokens.Add(new(data[pos], 0, pos));
        Insert(data, pos);
        ++pos;
      }
    }
    tokens.Add(new(EndSymbol, 0, pos));
    return tokens;
  }

  private sealed class LsbWriter(int capacity) {
    private readonly List<byte> _bytes = new(capacity);
    private ulong _acc;
    private int _bits;

    public void Write(int value, int count) {
      this._acc |= (ulong)((uint)value & (uint)((1L << count) - 1)) << this._bits;
      this._bits += count;
      while (this._bits >= 8) {
        this._bytes.Add((byte)this._acc);
        this._acc >>= 8;
        this._bits -= 8;
      }
    }

    public byte[] ToArray() {
      if (this._bits > 0) {
        this._bytes.Add((byte)this._acc);
        this._acc = 0;
        this._bits = 0;
      }
      return [.. this._bytes];
    }
  }
}
