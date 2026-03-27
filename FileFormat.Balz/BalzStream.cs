#pragma warning disable CS1591
namespace FileFormat.Balz;

/// <summary>
/// BALZ: ROLZ compressor by Ilya Muravyov.
/// Format: 4-byte big-endian uncompressed size, then arithmetic-coded ROLZ bitstream.
/// </summary>
public static class BalzStream {

  // ROLZ parameters
  private const int WindowSize = 65536;
  private const int TabSize = 256;        // entries per context table
  private const int MinMatch = 3;
  private const int MaxMatch = 258;

  // Arithmetic coder range constants
  private const uint Top = 0x01000000u;
  private const int ProbBits = 12;
  private const int ProbMax = 1 << ProbBits;  // 4096
  private const int ProbInit = ProbMax / 2;   // 2048

  // ── Public API ────────────────────────────────────────────────────────────

  public static void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();

    // Write 4-byte big-endian uncompressed size
    var size = data.Length;
    output.WriteByte((byte)(size >> 24));
    output.WriteByte((byte)(size >> 16));
    output.WriteByte((byte)(size >> 8));
    output.WriteByte((byte)size);

    if (data.Length == 0) return;

    var enc = new ArithEncoder(output);
    CompressRolz(data, enc);
    enc.Flush();
  }

  public static void Decompress(Stream input, Stream output) {
    // Read 4-byte big-endian uncompressed size
    int b0 = input.ReadByte(), b1 = input.ReadByte(), b2 = input.ReadByte(), b3 = input.ReadByte();
    if (b0 < 0 || b1 < 0 || b2 < 0 || b3 < 0) throw new InvalidDataException("Truncated BALZ header.");
    var size = (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;

    if (size == 0) return;

    var dec = new ArithDecoder(input);
    var result = DecompressRolz(dec, size);
    output.Write(result);
  }

  // ── ROLZ Compress ────────────────────────────────────────────────────────

  private static void CompressRolz(byte[] data, ArithEncoder enc) {
    // Per-context (previous byte) circular tables of positions
    var tables = new int[256][];
    var heads = new int[256];
    for (var c = 0; c < 256; c++) {
      tables[c] = new int[TabSize];
      Array.Fill(tables[c], -1);
    }

    // Adaptive probability model: one prob per bit type
    // [0] = is-match bit, [1..8] = literal bits, [9..16] = index bits, [17..24] = length bits
    var probs = new int[25];
    Array.Fill(probs, ProbInit);

    var ctx = 0; // previous byte context (initially 0)

    var i = 0;
    while (i < data.Length) {
      // Try to find best match in this context's table
      var tab = tables[ctx];
      var bestLen = 0;
      var bestIdx = 0;

      for (var j = 0; j < TabSize; j++) {
        var pos = tab[j];
        if (pos < 0) continue;
        var maxLen = Math.Min(MaxMatch, data.Length - i);
        var len = 0;
        while (len < maxLen && data[pos + len] == data[i + len]) len++;
        if (len > bestLen) {
          bestLen = len;
          bestIdx = j;
          if (bestLen == MaxMatch) break;
        }
      }

      // Store current position in table before encoding
      tab[heads[ctx] & (TabSize - 1)] = i;
      heads[ctx] = (heads[ctx] + 1) & (TabSize - 1);

      if (bestLen >= MinMatch) {
        // Encode match: bit 1
        enc.EncodeBit(1, ref probs[0]);
        // Encode 8-bit table index
        EncodeUint8(enc, (byte)bestIdx, probs, 9);
        // Encode length - MinMatch as 8 bits (0..255 = MinMatch..MinMatch+255)
        EncodeUint8(enc, (byte)(bestLen - MinMatch), probs, 17);
        ctx = data[i + bestLen - 1];
        i += bestLen;
      } else {
        // Encode literal: bit 0
        enc.EncodeBit(0, ref probs[0]);
        // Encode 8 literal bits
        EncodeUint8(enc, data[i], probs, 1);
        ctx = data[i];
        i++;
      }
    }
  }

  private static void EncodeUint8(ArithEncoder enc, byte val, int[] probs, int baseIdx) {
    for (var bit = 7; bit >= 0; bit--)
      enc.EncodeBit((val >> bit) & 1, ref probs[baseIdx + (7 - bit)]);
  }

  // ── ROLZ Decompress ──────────────────────────────────────────────────────

  private static byte[] DecompressRolz(ArithDecoder dec, int size) {
    var tables = new int[256][];
    var heads = new int[256];
    for (var i = 0; i < 256; i++) {
      tables[i] = new int[TabSize];
      Array.Fill(tables[i], -1);
    }

    var probs = new int[25];
    Array.Fill(probs, ProbInit);

    var result = new List<byte>(size);
    var ctx = 0;

    while (result.Count < size) {
      var isMatch = dec.DecodeBit(ref probs[0]);

      if (isMatch == 1) {
        var idx = DecodeUint8(dec, probs, 9);
        var lenMinus3 = DecodeUint8(dec, probs, 17);
        var len = lenMinus3 + MinMatch;
        var pos = tables[ctx][idx];
        if (pos < 0) throw new InvalidDataException("Invalid BALZ match reference.");
        // Store current position (before copying), matching compressor behavior
        tables[ctx][heads[ctx] & (TabSize - 1)] = result.Count;
        heads[ctx] = (heads[ctx] + 1) & (TabSize - 1);
        var lastByte = 0;
        for (var k = 0; k < len && result.Count < size; k++) {
          var b = result[pos + k];
          result.Add(b);
          lastByte = b;
        }
        ctx = lastByte;
      } else {
        var literal = DecodeUint8(dec, probs, 1);
        // Store position of where this literal will be placed
        tables[ctx][heads[ctx] & (TabSize - 1)] = result.Count;
        heads[ctx] = (heads[ctx] + 1) & (TabSize - 1);
        result.Add((byte)literal);
        ctx = literal;
      }
    }

    return [.. result];
  }

  private static int DecodeUint8(ArithDecoder dec, int[] probs, int baseIdx) {
    var val = 0;
    for (var bit = 7; bit >= 0; bit--) {
      var b = dec.DecodeBit(ref probs[baseIdx + (7 - bit)]);
      val = (val << 1) | b;
    }
    return val;
  }

  // ── Arithmetic Coder ─────────────────────────────────────────────────────

  private sealed class ArithEncoder {
    private uint _low;
    private uint _high = 0xFFFFFFFFu;
    private readonly Stream _out;

    public ArithEncoder(Stream output) => _out = output;

    public void EncodeBit(int bit, ref int prob) {
      var range = _high - _low + 1;
      var mid = _low + (ulong)range * (uint)prob / (uint)ProbMax - 1;
      if (mid >= _high) mid = _high - 1;
      var umid = (uint)mid;

      if (bit == 0) {
        _high = umid;
        prob += (ProbMax - prob) >> 4;
      } else {
        _low = umid + 1;
        prob -= prob >> 4;
      }

      Normalize();
    }

    private void Normalize() {
      while ((_low ^ _high) < Top) {
        _out.WriteByte((byte)(_high >> 24));
        _low <<= 8;
        _high = (_high << 8) | 0xFFu;
      }
    }

    public void Flush() {
      // Emit enough bytes to fully determine the value
      for (var i = 0; i < 4; i++) {
        _out.WriteByte((byte)(_high >> 24));
        _high <<= 8;
      }
    }
  }

  private sealed class ArithDecoder {
    private uint _low;
    private uint _high = 0xFFFFFFFFu;
    private uint _code;
    private readonly Stream _in;

    public ArithDecoder(Stream input) {
      _in = input;
      for (var i = 0; i < 4; i++)
        _code = (_code << 8) | (uint)Math.Max(0, input.ReadByte());
    }

    public int DecodeBit(ref int prob) {
      var range = _high - _low + 1;
      var mid = _low + (ulong)range * (uint)prob / (uint)ProbMax - 1;
      if (mid >= _high) mid = _high - 1;
      var umid = (uint)mid;

      int bit;
      if (_code <= umid) {
        bit = 0;
        _high = umid;
        prob += (ProbMax - prob) >> 4;
      } else {
        bit = 1;
        _low = umid + 1;
        prob -= prob >> 4;
      }

      Normalize();
      return bit;
    }

    private void Normalize() {
      while ((_low ^ _high) < Top) {
        var b = _in.ReadByte();
        _code = (_code << 8) | (uint)(b < 0 ? 0xFF : b);
        _low <<= 8;
        _high = (_high << 8) | 0xFFu;
      }
    }
  }
}
