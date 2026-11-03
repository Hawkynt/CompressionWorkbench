using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Balz;

/// <summary>
/// BALZ 1.20, Ilya Muravyov's ROLZ file compressor, converted from his public-domain
/// <c>balz.cpp</c> (http://sourceforge.net/projects/balz/). Output matches the reference
/// program byte for byte in both its normal (<c>c</c>) and maximum (<c>cx</c>) modes.
/// </summary>
/// <remarks>
/// A <c>.balz</c> file is the byte <c>0xBA</c>, the uncompressed length as a 64-bit
/// little-endian integer, and one binary arithmetic-coded stream. The input is processed in
/// blocks of 32 MiB, each run through an E8/E9 call-address filter once a <c>PE\0\0</c>
/// signature has been seen. A symbol is a literal (0..255) or a match length (256 + 0..255, for
/// 3..258 bytes) coded under the previous byte, followed for matches by a 7-bit index into a
/// 128-entry table of earlier positions kept per two-byte context.
/// </remarks>
public static class BalzCodec {
  private const byte Magic = 0xBA;
  private const int TabBits = 7;
  private const int TabSize = 1 << TabBits;
  private const int TabMask = TabSize - 1;
  private const int MinMatch = 3;
  private const int MaxMatch = 255 + MinMatch;
  private const int BufBits = 25;
  private const int BufSize = 1 << BufBits;
  private const uint BufMask = BufSize - 1;

  /// <summary>Compresses <paramref name="data"/> into a <c>.balz</c> file.</summary>
  /// <param name="data">The bytes to compress.</param>
  /// <param name="maximum"><see langword="true"/> for the reference's <c>cx</c> mode, which
  /// weighs shorter matches against the one that would follow them.</param>
  /// <returns>The <c>.balz</c> file.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data, bool maximum = false) {
    using var output = new MemoryStream(data.Length / 2 + 16);
    output.WriteByte(Magic);
    Span<byte> length = stackalloc byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(length, data.Length);
    output.Write(length);

    var coder = new Coder(output);
    // The reference keeps one 32 MiB buffer; bytes past a short block are what the previous
    // block left there, and its hash reads up to three of them.
    var buf = new byte[Math.Min(data.Length, BufSize) + 8];
    var cnt = new int[1 << 16];
    Span<int> bestIdx = stackalloc int[MaxMatch + 1];
    for (var start = 0; start < data.Length; start += BufSize) {
      var n = Math.Min(BufSize, data.Length - start);
      data.Slice(start, n).CopyTo(buf);
      E8E9(buf, n, forward: true);
      var tab = new uint[1 << 16][];

      var p = 0;
      while (p < 2 && p < n)
        coder.EncodeSymbol(buf[p++], 0);

      while (p < n) {
        var c2 = buf[p - 2] | buf[p - 1] << 8;
        var hash = Hash(buf, p);
        var len = MinMatch - 1;
        var idx = TabSize;
        var maxMatch = Math.Min(n - p, MaxMatch);
        var entries = tab[c2];
        if (entries != null)
          for (var x = 0; x < TabSize; ++x) {
            var d = entries[(cnt[c2] - x) & TabMask];
            if (d == 0)
              break;
            if ((d & ~BufMask) != hash)
              continue;
            var s = (int)(d & BufMask);
            if (buf[s + len] != buf[p + len] || buf[s] != buf[p])
              continue;
            var l = 0;
            while (++l < maxMatch)
              if (buf[s + l] != buf[p + l])
                break;
            if (l > len) {
              for (var i = l; i > len; --i)
                bestIdx[i] = x;
              idx = x;
              len = l;
              if (l == maxMatch)
                break;
            }
          }

        if (maximum && len >= MinMatch) {
          var sum = Points(len, idx) + PointsAt(buf, tab, cnt, p + len, n);
          if (sum < Points(len + MaxMatch, 0)) {
            var lookahead = len;
            for (var i = 1; i < lookahead; ++i) {
              var tmp = Points(i, bestIdx[i]) + PointsAt(buf, tab, cnt, p + i, n);
              if (tmp > sum) {
                sum = tmp;
                len = i;
              }
            }

            idx = bestIdx[len];
          }
        }

        (tab[c2] ??= new uint[TabSize])[++cnt[c2] & TabMask] = hash | (uint)p;

        if (len >= MinMatch) {
          coder.EncodeSymbol(256 - MinMatch + len, buf[p - 1]);
          coder.EncodeIndex(idx, buf[p - 2]);
          p += len;
        } else {
          coder.EncodeSymbol(buf[p], buf[p - 1]);
          ++p;
        }
      }
    }

    coder.Flush();
    return output.ToArray();
  }

  /// <summary>Decompresses a <c>.balz</c> file.</summary>
  /// <param name="data">The whole file.</param>
  /// <returns>The decompressed bytes.</returns>
  /// <exception cref="InvalidDataException">The file is not BALZ, or is truncated or corrupt.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 9 || data[0] != Magic)
      throw new InvalidDataException("Not in BALZ format.");
    var remaining = BinaryPrimitives.ReadInt64LittleEndian(data[1..]);
    if (remaining < 0 || remaining > Array.MaxLength)
      throw new InvalidDataException($"BALZ length {remaining} is out of range.");

    using var result = new MemoryStream((int)Math.Min(remaining, 1 << 20));
    var coder = new Decoder(data[9..]);
    var tab = new int[1 << 16][];
    var cnt = new int[1 << 16];
    var buf = new byte[Math.Min(remaining, BufSize) + 8];
    while (remaining > 0) {
      var n = (int)Math.Min(remaining, BufSize);
      var p = 0;
      while (p < 2 && p < n) {
        var t = coder.DecodeSymbol(0);
        if (t >= 256)
          throw new InvalidDataException("BALZ block starts with a match.");
        buf[p++] = (byte)t;
      }

      while (p < n) {
        var tmp = p;
        var c2 = buf[p - 2] | buf[p - 1] << 8;
        var t = coder.DecodeSymbol(buf[p - 1]);
        if (t >= 256) {
          var len = t - 256 + MinMatch;
          var s = (tab[c2] ??= new int[TabSize])[(cnt[c2] - coder.DecodeIndex(buf[p - 2])) & TabMask];
          if (s >= p || len > n - p)
            throw new InvalidDataException("BALZ match points outside the block decoded so far.");
          while (len-- > 0)
            buf[p++] = buf[s++];
        } else
          buf[p++] = (byte)t;

        (tab[c2] ??= new int[TabSize])[++cnt[c2] & TabMask] = tmp;
      }

      E8E9(buf, p, forward: false);
      result.Write(buf, 0, p);
      remaining -= p;
    }

    return result.ToArray();
  }

  private static uint Hash(byte[] buf, int p)
    => ((buf[p] | (uint)buf[p + 1] << 8 | (uint)buf[p + 2] << 16) * 2654435769u) & ~BufMask;

  private static int Points(int len, int x) => len >= MinMatch ? (len << TabBits) - x : ((MinMatch - 1) << TabBits) - 8;

  private static int PointsAt(byte[] buf, uint[]?[] tab, int[] cnt, int p, int n) {
    var c2 = buf[p - 2] | buf[p - 1] << 8;
    var hash = Hash(buf, p);
    var len = MinMatch - 1;
    var idx = TabSize;
    var maxMatch = Math.Min(n - p, MaxMatch);
    var entries = tab[c2];
    if (entries != null)
      for (var x = 0; x < TabSize; ++x) {
        var d = entries[(cnt[c2] - x) & TabMask];
        if (d == 0)
          break;
        if ((d & ~BufMask) != hash)
          continue;
        var s = (int)(d & BufMask);
        if (buf[s + len] != buf[p + len] || buf[s] != buf[p])
          continue;
        var l = 0;
        while (++l < maxMatch)
          if (buf[s + l] != buf[p + l])
            break;
        if (l > len) {
          idx = x;
          len = l;
          if (l == maxMatch)
            break;
        }
      }

    return Points(len, idx);
  }

  /// <summary>The reference's x86 call/jump filter, applied after the first <c>PE\0\0</c>.</summary>
  private static void E8E9(byte[] buf, int n, bool forward) {
    var end = n - 8;
    var p = 0;
    while (BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(p)) != 0x4550 && ++p < end) {
    }

    while (p < end) {
      if ((buf[p++] & 254) != 0xE8)
        continue;
      var addr = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(p));
      if (forward) {
        if (addr >= -p && addr < n - p)
          addr += p;
        else if (addr > 0 && addr < n)
          addr -= n;
      } else if (addr < 0) {
        if (addr + p >= 0)
          addr += n;
      } else if (addr < n)
        addr -= p;

      BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(p), addr);
      p += 4;
    }
  }

  /// <summary>Two-rate probability counters: 256 contexts of 512 symbol nodes and 128 index nodes.</summary>
  private sealed class Model {
    private readonly ushort[] _symbolFast = Init(256 * 512);
    private readonly ushort[] _symbolSlow = Init(256 * 512);
    private readonly ushort[] _indexFast = Init(256 * TabSize);
    private readonly ushort[] _indexSlow = Init(256 * TabSize);

    private static ushort[] Init(int count) {
      var counters = new ushort[count];
      counters.AsSpan().Fill(1 << 15);
      return counters;
    }

    public (ushort[] Fast, ushort[] Slow) Symbols => (_symbolFast, _symbolSlow);

    public (ushort[] Fast, ushort[] Slow) Indexes => (_indexFast, _indexSlow);

    public static uint P(ushort[] fast, ushort[] slow, int i) => (uint)fast[i] + slow[i];

    public static void Update(ushort[] fast, ushort[] slow, int i, int bit) {
      if (bit != 0) {
        fast[i] += (ushort)((fast[i] ^ 65535) >> 3);
        slow[i] += (ushort)((slow[i] ^ 65535) >> 6);
      } else {
        fast[i] -= (ushort)(fast[i] >> 3);
        slow[i] -= (ushort)(slow[i] >> 6);
      }
    }
  }

  private sealed class Coder(Stream output) {
    private readonly Model _model = new();
    private uint _low;
    private uint _high = uint.MaxValue;

    public void EncodeSymbol(int t, int c1) {
      var (fast, slow) = _model.Symbols;
      var ctx = 1;
      while (ctx < 512) {
        var bit = (t & 256) != 0 ? 1 : 0;
        t += t;
        Encode(bit, fast, slow, c1 * 512 + ctx);
        ctx += ctx + bit;
      }
    }

    public void EncodeIndex(int x, int c2) {
      var (fast, slow) = _model.Indexes;
      var ctx = 1;
      while (ctx < TabSize) {
        var bit = (x & (TabSize >> 1)) != 0 ? 1 : 0;
        x += x;
        Encode(bit, fast, slow, c2 * TabSize + ctx);
        ctx += ctx + bit;
      }
    }

    private void Encode(int bit, ushort[] fast, ushort[] slow, int i) {
      var mid = _low + (uint)(((ulong)(_high - _low) * (Model.P(fast, slow, i) << 15)) >> 32);
      if (bit != 0)
        _high = mid;
      else
        _low = mid + 1;
      Model.Update(fast, slow, i, bit);
      while ((_low ^ _high) < 1u << 24) {
        output.WriteByte((byte)(_low >> 24));
        _low <<= 8;
        _high = _high << 8 | 255;
      }
    }

    public void Flush() {
      for (var i = 0; i < 4; ++i) {
        output.WriteByte((byte)(_low >> 24));
        _low <<= 8;
      }
    }
  }

  private ref struct Decoder {
    private readonly ReadOnlySpan<byte> _data;
    private readonly Model _model = new();
    private int _pos;
    private uint _code;
    private uint _low;
    private uint _high = uint.MaxValue;

    public Decoder(ReadOnlySpan<byte> data) {
      _data = data;
      for (var i = 0; i < 4; ++i)
        _code = _code << 8 | Next();
    }

    public int DecodeSymbol(int c1) {
      var (fast, slow) = _model.Symbols;
      var ctx = 1;
      while (ctx < 512)
        ctx += ctx + Decode(fast, slow, c1 * 512 + ctx);
      return ctx - 512;
    }

    public int DecodeIndex(int c2) {
      var (fast, slow) = _model.Indexes;
      var ctx = 1;
      while (ctx < TabSize)
        ctx += ctx + Decode(fast, slow, c2 * TabSize + ctx);
      return ctx - TabSize;
    }

    private int Decode(ushort[] fast, ushort[] slow, int i) {
      var mid = _low + (uint)(((ulong)(_high - _low) * (Model.P(fast, slow, i) << 15)) >> 32);
      var bit = _code <= mid ? 1 : 0;
      if (bit != 0)
        _high = mid;
      else
        _low = mid + 1;
      Model.Update(fast, slow, i, bit);
      while ((_low ^ _high) < 1u << 24) {
        _code = _code << 8 | Next();
        _low <<= 8;
        _high = _high << 8 | 255;
      }

      return bit;
    }

    private uint Next() => _pos < _data.Length
      ? _data[_pos++]
      : throw new InvalidDataException("BALZ stream is truncated.");
  }
}
