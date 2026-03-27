#pragma warning disable CS1591
namespace FileFormat.Csc;

/// <summary>
/// CSC: Context Stream Compression by Fu Siyuan.
/// Format: 10-byte big-endian property header + 4-byte uncompressed size, then range-coded LZ77.
/// Header layout: uint32 dict_size | uint24 csc_blocksize | uint24 raw_blocksize | uint32 actual_size
/// </summary>
public static class CscStream {

  private const int DictSize = 65536;
  private const int CscBlockSize = 65536;

  // LZ77 parameters
  private const int MinMatch = 3;
  private const int MaxMatch = 258;

  // Range coder constants
  private const uint RcTop = 0x01000000u;
  private const int ProbMax = 4096;

  // ── Public API ────────────────────────────────────────────────────────────

  public static void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();
    var size = data.Length;

    // 10-byte property header (big-endian) + 4-byte uncompressed size
    WriteUint32Be(output, (uint)DictSize);
    WriteUint24Be(output, (uint)CscBlockSize);
    WriteUint24Be(output, (uint)Math.Min(size, 0xFFFFFF));
    WriteUint32Be(output, (uint)size);

    if (size == 0) return;

    var enc = new RangeEncoder(output);
    CompressLz77(data, enc);
    enc.Flush();
  }

  public static void Decompress(Stream input, Stream output) {
    // 10-byte header + 4-byte actual size
    Span<byte> hdr = stackalloc byte[14];
    input.ReadExactly(hdr);
    var size = (int)(((uint)hdr[10] << 24) | ((uint)hdr[11] << 16) |
                     ((uint)hdr[12] << 8) | (uint)hdr[13]);
    if (size == 0) return;

    var dec = new RangeDecoder(input);
    var result = DecompressLz77(dec, size);
    output.Write(result);
  }

  // ── LZ77 Compress (hash chain) ────────────────────────────────────────────

  private static void CompressLz77(byte[] data, RangeEncoder enc) {
    var n = data.Length;
    const int hashBits = 16;
    const int hashSize = 1 << hashBits;
    var head = new int[hashSize];
    var prev = new int[n];
    Array.Fill(head, -1);
    Array.Fill(prev, -1);

    // Probabilities:
    //   [0]      = is-match flag
    //   [1..8]   = length bits (len - MinMatch, 8 bits)
    //   [9..24]  = dist bits (dist - 1, 16 bits)
    //   [25..32] = literal bits
    var probs = new uint[33];
    Array.Fill(probs, (uint)(ProbMax / 2));

    var i = 0;
    while (i < n) {
      var bestLen = 0;
      var bestDist = 0;

      if (i + MinMatch <= n) {
        var h = Hash3(data, i, n, hashBits);
        var maxDist = Math.Min(i, DictSize);
        var maxLen = Math.Min(MaxMatch, n - i);
        var cur = head[h];
        var limit = 64;

        while (cur >= 0 && i - cur <= maxDist && limit-- > 0) {
          var mlen = 0;
          while (mlen < maxLen && data[cur + mlen] == data[i + mlen]) mlen++;
          if (mlen > bestLen) {
            bestLen = mlen;
            bestDist = i - cur;
            if (bestLen == maxLen) break;
          }
          cur = prev[cur];
        }

        // Insert current position into hash chain
        prev[i] = head[h];
        head[h] = i;
      }

      if (bestLen >= MinMatch) {
        enc.EncodeBit(1, ref probs[0]);
        EncodeUint8(enc, (byte)(bestLen - MinMatch), probs, 1);
        EncodeUint16(enc, (ushort)(bestDist - 1), probs, 9);
        // Lazily insert skipped positions into hash chain
        for (var k = 1; k < bestLen; k++) {
          if (i + k + MinMatch <= n) {
            var h2 = Hash3(data, i + k, n, hashBits);
            prev[i + k] = head[h2];
            head[h2] = i + k;
          }
        }
        i += bestLen;
      } else {
        enc.EncodeBit(0, ref probs[0]);
        EncodeUint8(enc, data[i], probs, 25);
        i++;
      }
    }
  }

  private static void EncodeUint8(RangeEncoder enc, byte val, uint[] probs, int baseIdx) {
    for (var bit = 7; bit >= 0; bit--)
      enc.EncodeBit((val >> bit) & 1, ref probs[baseIdx + (7 - bit)]);
  }

  private static void EncodeUint16(RangeEncoder enc, ushort val, uint[] probs, int baseIdx) {
    for (var bit = 15; bit >= 0; bit--)
      enc.EncodeBit((val >> bit) & 1, ref probs[baseIdx + (15 - bit)]);
  }

  private static int Hash3(byte[] data, int pos, int n, int hashBits) {
    if (pos + 2 >= n) return (int)((uint)data[pos] * 0x9E3779B1u >> (32 - hashBits));
    var v = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16));
    return (int)((v * 0x9E3779B1u) >> (32 - hashBits));
  }

  // ── LZ77 Decompress ──────────────────────────────────────────────────────

  private static byte[] DecompressLz77(RangeDecoder dec, int size) {
    var probs = new uint[33];
    Array.Fill(probs, (uint)(ProbMax / 2));

    var buf = new byte[size];
    var pos = 0;

    while (pos < size) {
      var isMatch = dec.DecodeBit(ref probs[0]);
      if (isMatch == 1) {
        var lenMinus3 = DecodeUint8(dec, probs, 1);
        var distMinus1 = DecodeUint16(dec, probs, 9);
        var len = lenMinus3 + MinMatch;
        var dist = distMinus1 + 1;
        var src = pos - dist;
        if (src < 0) throw new InvalidDataException("Invalid CSC back-reference.");
        var end = Math.Min(pos + len, size);
        // Byte-by-byte copy handles overlapping (e.g. run-length with dist=1)
        while (pos < end)
          buf[pos++] = buf[src++];
      } else {
        var lit = DecodeUint8(dec, probs, 25);
        buf[pos++] = (byte)lit;
      }
    }

    return buf;
  }

  private static int DecodeUint8(RangeDecoder dec, uint[] probs, int baseIdx) {
    var val = 0;
    for (var bit = 7; bit >= 0; bit--)
      val = (val << 1) | dec.DecodeBit(ref probs[baseIdx + (7 - bit)]);
    return val;
  }

  private static int DecodeUint16(RangeDecoder dec, uint[] probs, int baseIdx) {
    var val = 0;
    for (var bit = 15; bit >= 0; bit--)
      val = (val << 1) | dec.DecodeBit(ref probs[baseIdx + (15 - bit)]);
    return val;
  }

  // ── I/O Helpers ──────────────────────────────────────────────────────────

  private static void WriteUint32Be(Stream s, uint v) {
    s.WriteByte((byte)(v >> 24));
    s.WriteByte((byte)(v >> 16));
    s.WriteByte((byte)(v >> 8));
    s.WriteByte((byte)v);
  }

  private static void WriteUint24Be(Stream s, uint v) {
    s.WriteByte((byte)(v >> 16));
    s.WriteByte((byte)(v >> 8));
    s.WriteByte((byte)v);
  }

  // ── Range Coder ──────────────────────────────────────────────────────────

  private sealed class RangeEncoder {
    private uint _low;
    private uint _high = 0xFFFFFFFFu;
    private readonly Stream _out;

    public RangeEncoder(Stream output) => _out = output;

    public void EncodeBit(int bit, ref uint prob) {
      var range = _high - _low + 1;
      var mid = _low + (uint)((ulong)range * prob / ProbMax) - 1;
      if (mid >= _high) mid = _high - 1;

      if (bit == 0) {
        _high = mid;
        prob += ((uint)ProbMax - prob) >> 5;
      } else {
        _low = mid + 1;
        prob -= prob >> 5;
      }

      Normalize();
    }

    private void Normalize() {
      while ((_low ^ _high) < RcTop) {
        _out.WriteByte((byte)(_high >> 24));
        _low <<= 8;
        _high = (_high << 8) | 0xFFu;
      }
    }

    public void Flush() {
      for (var i = 0; i < 4; i++) {
        _out.WriteByte((byte)(_high >> 24));
        _high <<= 8;
      }
    }
  }

  private sealed class RangeDecoder {
    private uint _low;
    private uint _high = 0xFFFFFFFFu;
    private uint _code;
    private readonly Stream _in;

    public RangeDecoder(Stream input) {
      _in = input;
      for (var i = 0; i < 4; i++)
        _code = (_code << 8) | (uint)Math.Max(0, input.ReadByte());
    }

    public int DecodeBit(ref uint prob) {
      var range = _high - _low + 1;
      var mid = _low + (uint)((ulong)range * prob / ProbMax) - 1;
      if (mid >= _high) mid = _high - 1;

      int bit;
      if (_code <= mid) {
        bit = 0;
        _high = mid;
        prob += ((uint)ProbMax - prob) >> 5;
      } else {
        bit = 1;
        _low = mid + 1;
        prob -= prob >> 5;
      }

      Normalize();
      return bit;
    }

    private void Normalize() {
      while ((_low ^ _high) < RcTop) {
        var b = _in.ReadByte();
        _code = (_code << 8) | (uint)(b < 0 ? 0xFF : b);
        _low <<= 8;
        _high = (_high << 8) | 0xFFu;
      }
    }
  }
}
