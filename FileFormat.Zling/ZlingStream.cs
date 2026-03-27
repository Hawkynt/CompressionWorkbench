#pragma warning disable CS1591
namespace FileFormat.Zling;

/// <summary>
/// Zling: ROLZ + Huffman block compressor by Zhang Li.
/// Format: blocks of (uint8 flag, uint32 LE encpos, uint32 LE rlen, uint32 LE olen, olen bytes).
/// flag=1 means more data follows, flag=0 is the final block.
/// </summary>
public static class ZlingStream {

  // ROLZ: 256 contexts, 16 entries each
  private const int CtxCount = 256;
  private const int TabLen = 16;
  private const int MinMatch = 3;
  private const int MaxMatch = 258;
  private const int BlockSize = 65536;

  // Token encoding: values 0..255 = literals, 256..511 = match (len-3 encoded in low 8 bits),
  // distances are encoded separately as 16-bit values following the match token.

  public static void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();

    var encpos = 0;
    while (encpos <= data.Length) {
      var blockStart = encpos;
      var blockEnd = Math.Min(encpos + BlockSize, data.Length);
      var rlen = blockEnd - blockStart;
      var isLast = blockEnd >= data.Length;

      // ROLZ compress this block
      var tokens = new List<ushort>();
      var dists = new List<ushort>();
      RolzCompress(data, blockStart, blockEnd, tokens, dists);

      // Huffman encode tokens + dists
      var encoded = HuffmanEncode(tokens, dists);

      // Write block header
      output.WriteByte(isLast ? (byte)0 : (byte)1);
      WriteUint32Le(output, (uint)encpos);
      WriteUint32Le(output, (uint)rlen);
      WriteUint32Le(output, (uint)encoded.Length);
      output.Write(encoded);

      encpos = blockEnd;
      if (isLast) break;
    }
  }

  public static void Decompress(Stream input, Stream output) {
    while (true) {
      var flag = input.ReadByte();
      if (flag < 0) break;

      var encpos = ReadUint32Le(input);
      var rlen = ReadUint32Le(input);
      var olen = ReadUint32Le(input);

      var encoded = new byte[olen];
      input.ReadExactly(encoded);

      var block = HuffmanDecode(encoded, (int)rlen);
      output.Write(block);

      if (flag == 0) break;
    }
  }

  // ── ROLZ Compress ────────────────────────────────────────────────────────

  private static void RolzCompress(byte[] data, int start, int end, List<ushort> tokens, List<ushort> dists) {
    // tables[ctx][slot] = absolute position
    var tables = new int[CtxCount, TabLen];
    var heads = new int[CtxCount];
    for (var c = 0; c < CtxCount; c++)
      for (var j = 0; j < TabLen; j++)
        tables[c, j] = -1;

    var ctx = 0;
    var i = start;

    while (i < end) {
      var bestLen = 0;
      var bestSlot = 0;

      // Search context table
      for (var j = 0; j < TabLen; j++) {
        var pos = tables[ctx, j];
        if (pos < 0) continue;
        // Bound both i+mlen and pos+mlen to stay within [start, end)
        var maxLen = Math.Min(MaxMatch, Math.Min(end - i, end - pos));
        var mlen = 0;
        while (mlen < maxLen && data[pos + mlen] == data[i + mlen]) mlen++;
        if (mlen > bestLen) {
          bestLen = mlen;
          bestSlot = j;
          if (bestLen == MaxMatch) break;
        }
      }

      // Store current position
      tables[ctx, heads[ctx]] = i;
      heads[ctx] = (heads[ctx] + 1) % TabLen;

      if (bestLen >= MinMatch) {
        // Match token: 256 + (len - MinMatch)
        tokens.Add((ushort)(256 + (bestLen - MinMatch)));
        dists.Add((ushort)bestSlot);
        ctx = data[i + bestLen - 1];
        i += bestLen;
      } else {
        // Literal token
        tokens.Add(data[i]);
        ctx = data[i];
        i++;
      }
    }
  }

  // ── ROLZ Decompress ──────────────────────────────────────────────────────

  private static byte[] RolzDecompress(ushort[] tokens, ushort[] dists, int rlen) {
    var tables = new int[CtxCount, TabLen];
    var heads = new int[CtxCount];
    for (var c = 0; c < CtxCount; c++)
      for (var j = 0; j < TabLen; j++)
        tables[c, j] = -1;

    var result = new List<byte>(rlen);
    var ctx = 0;
    var di = 0;

    foreach (var token in tokens) {
      if (token < 256) {
        // Literal
        tables[ctx, heads[ctx]] = result.Count;
        heads[ctx] = (heads[ctx] + 1) % TabLen;
        result.Add((byte)token);
        ctx = token;
      } else {
        var len = (token - 256) + MinMatch;
        var slot = dists[di++];
        var pos = tables[ctx, slot];
        if (pos < 0) throw new InvalidDataException("Invalid Zling ROLZ reference.");
        tables[ctx, heads[ctx]] = result.Count;
        heads[ctx] = (heads[ctx] + 1) % TabLen;
        var lastByte = 0;
        for (var k = 0; k < len; k++) {
          var b = result[pos + k];
          result.Add(b);
          lastByte = b;
        }
        ctx = lastByte;
      }
    }

    return [.. result];
  }

  // ── Huffman Encode ────────────────────────────────────────────────────────
  // Interleave token stream and dist stream. We pack everything as a simple
  // canonical Huffman stream. Format:
  //   uint16 LE token_count
  //   uint16 LE dist_count
  //   then (token_count + dist_count) values encoded as canonical Huffman
  // Code lengths stored as: uint16 LE sym_count (512), then 512 bytes of lengths

  private static byte[] HuffmanEncode(List<ushort> tokens, List<ushort> dists) {
    // Build frequency table over symbols 0..511 (tokens) + 0..15 (dists mapped to 512..527)
    var freq = new int[528];
    foreach (var t in tokens) freq[t]++;
    foreach (var d in dists) freq[512 + d]++;

    // Build Huffman tree using simple priority queue
    var lengths = BuildLengths(freq, 528);

    // Canonical code assignment
    var (codes, codeLens) = BuildCanonicalCodes(lengths, 528);

    using var ms = new MemoryStream();
    WriteUint32Le(ms, (uint)tokens.Count);
    WriteUint32Le(ms, (uint)dists.Count);

    // Write lengths table (528 bytes)
    for (var i = 0; i < 528; i++) ms.WriteByte((byte)lengths[i]);

    // Write encoded bitstream
    var bw = new BitWriter(ms);
    foreach (var t in tokens) bw.Write(codes[t], codeLens[t]);
    foreach (var d in dists) bw.Write(codes[512 + d], codeLens[512 + d]);
    bw.Flush();

    return ms.ToArray();
  }

  private static byte[] HuffmanDecode(byte[] encoded, int rlen) {
    using var ms = new MemoryStream(encoded);

    var tokenCount = (int)ReadUint32Le(ms);
    var distCount = (int)ReadUint32Le(ms);

    // Read lengths
    var lengths = new int[528];
    for (var i = 0; i < 528; i++) {
      var b = ms.ReadByte();
      if (b < 0) break;
      lengths[i] = b;
    }

    // Build decode table from canonical codes
    var (codes, codeLens) = BuildCanonicalCodes(lengths, 528);
    var decodeTable = BuildDecodeTable(codes, codeLens, 528);

    var br = new BitReader(ms);
    var tokens = new ushort[tokenCount];
    var dists = new ushort[distCount];

    for (var i = 0; i < tokenCount; i++) tokens[i] = (ushort)DecodeSymbol(br, decodeTable, codeLens);
    for (var i = 0; i < distCount; i++) dists[i] = (ushort)(DecodeSymbol(br, decodeTable, codeLens) - 512);

    return RolzDecompress(tokens, dists, rlen);
  }

  // ── Huffman helpers ───────────────────────────────────────────────────────

  private static int[] BuildLengths(int[] freq, int n) {
    // Collect symbols with non-zero frequency
    var syms = new List<int>();
    for (var i = 0; i < n; i++)
      if (freq[i] > 0) syms.Add(i);

    var lengths = new int[n];
    if (syms.Count == 0) return lengths;
    if (syms.Count == 1) { lengths[syms[0]] = 1; return lengths; }

    // Build Huffman tree with a flat node array.
    // Nodes 0..syms.Count-1 are leaves; higher indices are internal nodes.
    // Each node: (freq, parent=-1 initially, isRight=false)
    var nodeCount = syms.Count;
    var nodeFreq = new int[2 * nodeCount];
    var parent   = new int[2 * nodeCount];
    var isRight  = new bool[2 * nodeCount];
    for (var i = 0; i < nodeCount; i++) {
      nodeFreq[i] = freq[syms[i]];
      parent[i]   = -1;
    }

    // Priority queue (min-heap by freq) over active node indices
    var pq = new SortedSet<(int freq, int id)>(Comparer<(int freq, int id)>.Create(
      (a, b) => a.freq != b.freq ? a.freq.CompareTo(b.freq) : a.id.CompareTo(b.id)));
    for (var i = 0; i < nodeCount; i++) pq.Add((nodeFreq[i], i));

    var nextId = nodeCount;
    while (pq.Count > 1) {
      var aEntry = pq.Min; pq.Remove(aEntry);
      var bEntry = pq.Min; pq.Remove(bEntry);
      var combined = aEntry.freq + bEntry.freq;
      nodeFreq[nextId] = combined;
      parent[nextId]   = -1;
      parent[aEntry.id] = nextId; isRight[aEntry.id] = false;
      parent[bEntry.id] = nextId; isRight[bEntry.id] = true;
      pq.Add((combined, nextId));
      nextId++;
    }

    // Walk from each leaf to root to compute depth, then cap at 15
    for (var i = 0; i < syms.Count; i++) {
      var depth = 0;
      var cur = i;
      while (parent[cur] >= 0) { depth++; cur = parent[cur]; }
      lengths[syms[i]] = Math.Min(depth, 15);
    }

    // Fix canonical validity after capping: ensure Kraft sum <= 1
    // (simple approach: inflate any zero-length used symbols to 15, then re-normalise)
    FixLengthsAfterCap(lengths, freq, n);
    return lengths;
  }

  // After capping at 15 the Kraft inequality may be violated.
  // We use the standard package-merge / length-limited redistribution:
  // increase lengths on symbols until the Kraft sum <= 1,
  // then decrease lengths on heavy symbols until sum == 1.
  private static void FixLengthsAfterCap(int[] lengths, int[] freq, int n) {
    // Recompute Kraft sum * 2^15 as integer; target == 2^15
    const int MaxLen = 15;
    const int Target = 1 << MaxLen;

    // Collect active symbols (length > 0) sorted by freq ascending (lightest first)
    while (true) {
      long kraft = 0;
      for (var i = 0; i < n; i++)
        if (lengths[i] > 0) kraft += 1 << (MaxLen - lengths[i]);
      if (kraft == Target) return;

      if (kraft > Target) {
        // Over-full: increase length of lightest (least frequent) symbols
        // Find symbol with smallest freq among those at length < MaxLen
        var best = -1;
        for (var i = 0; i < n; i++)
          if (lengths[i] > 0 && lengths[i] < MaxLen)
            if (best < 0 || freq[i] < freq[best]) best = i;
        if (best < 0) return; // can't fix
        lengths[best]++;
      } else {
        // Under-full: decrease length of heaviest symbols
        var best = -1;
        for (var i = 0; i < n; i++)
          if (lengths[i] > 1)
            if (best < 0 || freq[i] > freq[best]) best = i;
        if (best < 0) return;
        lengths[best]--;
      }
    }
  }

  private static (uint[] codes, int[] codeLens) BuildCanonicalCodes(int[] lengths, int n) {
    // Sort symbols by length then by symbol value
    var maxLen = 0;
    for (var i = 0; i < n; i++) if (lengths[i] > maxLen) maxLen = lengths[i];

    var codes = new uint[n];
    var codeLens = new int[n];
    if (maxLen == 0) return (codes, codeLens);

    // Count codes per length
    var bl_count = new int[maxLen + 1];
    for (var i = 0; i < n; i++) if (lengths[i] > 0) bl_count[lengths[i]]++;

    // Find starting code for each length
    var next_code = new uint[maxLen + 2];
    uint code = 0;
    for (var bits = 1; bits <= maxLen; bits++) {
      code = (code + (uint)bl_count[bits - 1]) << 1;
      next_code[bits] = code;
    }

    // Assign codes
    for (var i = 0; i < n; i++) {
      var len = lengths[i];
      if (len > 0) {
        codes[i] = next_code[len]++;
        codeLens[i] = len;
      }
    }

    return (codes, codeLens);
  }

  private static Dictionary<(uint code, int len), int> BuildDecodeTable(uint[] codes, int[] codeLens, int n) {
    var table = new Dictionary<(uint, int), int>();
    for (var i = 0; i < n; i++)
      if (codeLens[i] > 0) table[(codes[i], codeLens[i])] = i;
    return table;
  }

  private static int DecodeSymbol(BitReader br, Dictionary<(uint code, int len), int> table, int[] codeLens) {
    uint accum = 0;
    for (var len = 1; len <= 15; len++) {
      accum = (accum << 1) | (uint)br.ReadBit();
      if (table.TryGetValue((accum, len), out var sym)) return sym;
    }
    throw new InvalidDataException("Invalid Huffman code in Zling stream.");
  }

  // ── Bit I/O ───────────────────────────────────────────────────────────────

  private sealed class BitWriter {
    private uint _buf;
    private int _bits;
    private readonly Stream _out;

    public BitWriter(Stream output) => _out = output;

    public void Write(uint code, int len) {
      _buf = (_buf << len) | code;
      _bits += len;
      while (_bits >= 8) {
        _bits -= 8;
        _out.WriteByte((byte)(_buf >> _bits));
      }
    }

    public void Flush() {
      if (_bits > 0) {
        _out.WriteByte((byte)(_buf << (8 - _bits)));
        _bits = 0;
      }
    }
  }

  private sealed class BitReader {
    private uint _buf;
    private int _bits;
    private readonly Stream _in;

    public BitReader(Stream input) => _in = input;

    public int ReadBit() {
      if (_bits == 0) {
        var b = _in.ReadByte();
        _buf = (uint)(b < 0 ? 0 : b);
        _bits = 8;
      }
      _bits--;
      return (int)((_buf >> _bits) & 1);
    }
  }

  // ── I/O Helpers ───────────────────────────────────────────────────────────

  private static void WriteUint32Le(Stream s, uint v) {
    s.WriteByte((byte)v);
    s.WriteByte((byte)(v >> 8));
    s.WriteByte((byte)(v >> 16));
    s.WriteByte((byte)(v >> 24));
  }

  private static void WriteUint16Le(Stream s, ushort v) {
    s.WriteByte((byte)v);
    s.WriteByte((byte)(v >> 8));
  }

  private static uint ReadUint32Le(Stream s) {
    var b0 = s.ReadByte(); var b1 = s.ReadByte();
    var b2 = s.ReadByte(); var b3 = s.ReadByte();
    return (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
  }

  private static int ReadUint16Le(Stream s) {
    var b0 = s.ReadByte(); var b1 = s.ReadByte();
    return b0 | (b1 << 8);
  }
}
