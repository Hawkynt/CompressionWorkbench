namespace Compression.Core.Dictionary.Crush;

/// <summary>
/// CRUSH 1.00, Ilya Muravyov's LZ77 file compressor, converted from his public-domain
/// <c>crush.cpp</c> (http://sourceforge.net/projects/crush/). Output matches the reference
/// program byte for byte at each of its three levels.
/// </summary>
/// <remarks>
/// A CRUSH file is a sequence of blocks of at most 64 MiB, each a 32-bit little-endian
/// uncompressed size followed by a bit stream (least significant bit first) padded to a byte.
/// A token starts with one bit: <c>0</c> and eight bits are a literal; <c>1</c> is a match whose
/// length minus three is coded in one of six prefix classes (<c>1 xx</c>, <c>01 xx</c>,
/// <c>001 xx</c>, <c>0001 xxx</c>, <c>00001 xxxxx</c>, <c>00000</c> + nine bits; 3..593), then a
/// four-bit slot and the offset minus one in a 2 MiB window.
/// </remarks>
public static class CrushCodec {
  private const int WBits = 21;
  private const int WSize = 1 << WBits;
  private const int WMask = WSize - 1;
  private const int SlotBits = 4;
  private const int NumSlots = 1 << SlotBits;
  private const int ABits = 2, BBits = 2, CBits = 2, DBits = 3, EBits = 5, FBits = 9;
  private const int A = 1 << ABits;
  private const int B = (1 << BBits) + A;
  private const int C = (1 << CBits) + B;
  private const int D = (1 << DBits) + C;
  private const int E = (1 << EBits) + D;
  private const int F = (1 << FBits) + E;
  private const int MinMatch = 3;
  private const int MaxMatch = F - 1 + MinMatch;
  private const int BufSize = 1 << 26;
  private const int TooFar = 1 << 16;
  private const int Hash1Len = MinMatch;
  private const int Hash2Len = MinMatch + 1;
  private const int Hash1Bits = 21;
  private const int Hash2Bits = 24;
  private const int Hash1Mask = (1 << Hash1Bits) - 1;
  private const int Hash2Mask = (1 << Hash2Bits) - 1;
  private const int Hash1Shift = (Hash1Bits + Hash1Len - 1) / Hash1Len;
  private const int Hash2Shift = (Hash2Bits + Hash2Len - 1) / Hash2Len;

  /// <summary>Default level, the reference's <c>c</c> command.</summary>
  public const int DefaultLevel = 1;

  /// <summary>Compresses <paramref name="data"/> into a CRUSH file.</summary>
  /// <param name="data">The bytes to compress.</param>
  /// <param name="level">0 (<c>cf</c>, fast), 1 (<c>c</c>) or 2 (<c>cx</c>, maximum).</param>
  /// <returns>The CRUSH file; empty input gives an empty file, as with the reference.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data, int level = DefaultLevel) {
    ArgumentOutOfRangeException.ThrowIfNegative(level);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 2);
    using var output = new MemoryStream(data.Length / 2 + 16);
    var maxChain = level switch { 0 => 4, 1 => 256, _ => 1 << 12 };
    byte[]? previousBlock = null;
    for (var start = 0; start < data.Length; start += BufSize) {
      var block = data.Slice(start, Math.Min(BufSize, data.Length - start));
      CompressBlock(block, previousBlock, maxChain, level, output);
      previousBlock = block.Length == BufSize ? block.ToArray() : null;
    }

    return output.ToArray();
  }

  private static void CompressBlock(ReadOnlySpan<byte> block, byte[]? previousBlock, int maxChain, int level, MemoryStream output) {
    var size = block.Length;
    Span<byte> sizeBytes = stackalloc byte[4];
    BitConverter.TryWriteBytes(sizeBytes, size);
    output.Write(sizeBytes);

    // The reference reads a few bytes past the block when it hashes; its buffer still holds
    // the previous block there (zeros before the first).
    var buf = new byte[size + MaxMatch + 8];
    block.CopyTo(buf);
    if (previousBlock != null)
      previousBlock.AsSpan(size, Math.Min(previousBlock.Length - size, buf.Length - size)).CopyTo(buf.AsSpan(size));

    var head = new HeadTable(size);
    // Indexed by position & WMask, which stays below the block size in blocks under 2 MiB.
    var prev = new int[Math.Min(WSize, size)];
    var bits = new BitWriter(output);

    var h1 = 0;
    var h2 = 0;
    for (var i = 0; i < Hash1Len; ++i)
      h1 = UpdateHash1(h1, buf[i]);
    for (var i = 0; i < Hash2Len; ++i)
      h2 = UpdateHash2(h2, buf[i]);

    var p = 0;
    while (p < size) {
      var len = MinMatch - 1;
      var offset = WSize;
      var maxMatch = Math.Min(MaxMatch, size - p);
      var limit = Math.Max(p - WSize, 0);

      var s = head.Get(h1);
      if (s >= limit && buf[s] == buf[p]) {
        var l = 0;
        while (++l < maxMatch)
          if (buf[s + l] != buf[p + l])
            break;
        if (l > len) {
          len = l;
          offset = p - s;
        }
      }

      if (len < MaxMatch) {
        var chain = maxChain;
        s = head.Get(h2 + (1 << Hash1Bits));
        while (chain-- != 0 && s >= limit) {
          if (buf[s + len] == buf[p + len] && buf[s] == buf[p]) {
            var l = 0;
            while (++l < maxMatch)
              if (buf[s + l] != buf[p + l])
                break;
            if (l > len + Penalty((p - s) >> 4, offset)) {
              len = l;
              offset = p - s;
            }

            if (l == maxMatch)
              break;
          }

          s = prev[s & WMask];
        }
      }

      if (len == MinMatch && offset > TooFar)
        len = 0;

      if (level >= 2 && len >= MinMatch && len < maxMatch) {
        var nextP = p + 1;
        var maxLazy = Math.Min(len + 4, maxMatch);
        var chain = maxChain;
        s = head.Get(UpdateHash2(h2, buf[nextP + Hash2Len - 1]) + (1 << Hash1Bits));
        while (chain-- != 0 && s >= limit) {
          if (buf[s + len] == buf[nextP + len] && buf[s] == buf[nextP]) {
            var l = 0;
            while (++l < maxLazy)
              if (buf[s + l] != buf[nextP + l])
                break;
            if (l > len + Penalty(nextP - s, offset)) {
              len = 0;
              break;
            }

            if (l == maxLazy)
              break;
          }

          s = prev[s & WMask];
        }
      }

      if (len >= MinMatch) {
        bits.Put(1, 1);
        var l = len - MinMatch;
        if (l < A) {
          bits.Put(1, 1);
          bits.Put(ABits, l);
        } else if (l < B) {
          bits.Put(2, 1 << 1);
          bits.Put(BBits, l - A);
        } else if (l < C) {
          bits.Put(3, 1 << 2);
          bits.Put(CBits, l - B);
        } else if (l < D) {
          bits.Put(4, 1 << 3);
          bits.Put(DBits, l - C);
        } else if (l < E) {
          bits.Put(5, 1 << 4);
          bits.Put(EBits, l - D);
        } else {
          bits.Put(5, 0);
          bits.Put(FBits, l - E);
        }

        --offset;
        var log = WBits - NumSlots;
        while (offset >= 2 << log)
          ++log;
        bits.Put(SlotBits, log - (WBits - NumSlots));
        if (log > WBits - NumSlots)
          bits.Put(log, offset - (1 << log));
        else
          bits.Put(WBits - (NumSlots - 1), offset);
      } else {
        len = 1;
        bits.Put(9, buf[p] << 1);
      }

      while (len-- != 0) {
        head.Set(h1, p);
        prev[p & WMask] = head.Get(h2 + (1 << Hash1Bits));
        head.Set(h2 + (1 << Hash1Bits), p);
        ++p;
        h1 = UpdateHash1(h1, buf[p + Hash1Len - 1]);
        h2 = UpdateHash2(h2, buf[p + Hash2Len - 1]);
      }
    }

    bits.Flush();
  }

  /// <summary>Decompresses a CRUSH file.</summary>
  /// <param name="data">The whole file.</param>
  /// <returns>The decompressed bytes.</returns>
  /// <exception cref="InvalidDataException">The file is truncated or corrupt.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    var pos = 0;
    while (pos < data.Length) {
      if (pos + 4 > data.Length)
        throw new InvalidDataException("CRUSH file ends inside a block size.");
      var size = BitConverter.ToInt32(data.Slice(pos, 4));
      pos += 4;
      if (size is < 1 or > BufSize)
        throw new InvalidDataException($"CRUSH block size {size} is outside 1..{BufSize}.");

      var buf = new byte[size];
      var bits = new BitReader(data, pos);
      var p = 0;
      while (p < size) {
        if (bits.Get(1) != 0) {
          int len;
          if (bits.Get(1) != 0)
            len = bits.Get(ABits);
          else if (bits.Get(1) != 0)
            len = bits.Get(BBits) + A;
          else if (bits.Get(1) != 0)
            len = bits.Get(CBits) + B;
          else if (bits.Get(1) != 0)
            len = bits.Get(DBits) + C;
          else if (bits.Get(1) != 0)
            len = bits.Get(EBits) + D;
          else
            len = bits.Get(FBits) + E;

          var log = bits.Get(SlotBits) + (WBits - NumSlots);
          var s = ~(log > WBits - NumSlots ? bits.Get(log) + (1 << log) : bits.Get(WBits - (NumSlots - 1))) + p;
          len += MinMatch;
          if (s < 0)
            throw new InvalidDataException("CRUSH match reaches before the start of the block.");
          if (len > size - p)
            throw new InvalidDataException("CRUSH match runs past the end of the block.");
          while (len-- != 0)
            buf[p++] = buf[s++];
        } else
          buf[p++] = (byte)bits.Get(8);
      }

      output.Write(buf);
      pos = bits.BytePosition;
    }

    return output.ToArray();
  }

  private static int UpdateHash1(int h, int c) => ((h << Hash1Shift) + c) & Hash1Mask;

  private static int UpdateHash2(int h, int c) => ((h << Hash2Shift) + c) & Hash2Mask;

  private static int Penalty(int a, int b) {
    var p = 0;
    while (a > b) {
      a >>= 3;
      ++p;
    }

    return p;
  }

  /// <summary>
  /// The reference's <c>head</c> array (2^21 + 2^24 entries, all -1 at the start of a block).
  /// Small blocks touch few entries, so they use a dictionary instead of 72 MiB of table.
  /// </summary>
  private sealed class HeadTable(int blockSize) {
    private const int Entries = (1 << Hash1Bits) + (1 << Hash2Bits);
    private readonly int[]? _array = blockSize >= 1 << 20 ? NewArray() : null;
    private readonly Dictionary<int, int>? _map = blockSize >= 1 << 20 ? null : new Dictionary<int, int>(Math.Max(16, blockSize * 2));

    private static int[] NewArray() {
      var array = new int[Entries];
      array.AsSpan().Fill(-1);
      return array;
    }

    public int Get(int index) => _array != null ? _array[index] : _map!.GetValueOrDefault(index, -1);

    public void Set(int index, int value) {
      if (_array != null)
        _array[index] = value;
      else
        _map![index] = value;
    }
  }

  /// <summary>The reference's LSB-first bit writer.</summary>
  private sealed class BitWriter(Stream output) {
    private int _buffer;
    private int _count;

    public void Put(int n, int x) {
      _buffer |= x << _count;
      _count += n;
      while (_count >= 8) {
        output.WriteByte((byte)_buffer);
        _buffer >>= 8;
        _count -= 8;
      }
    }

    public void Flush() {
      Put(7, 0);
      _buffer = _count = 0;
    }
  }

  /// <summary>The reference's LSB-first bit reader; it reads whole bytes only as needed.</summary>
  private ref struct BitReader(ReadOnlySpan<byte> data, int start) {
    private readonly ReadOnlySpan<byte> _data = data;
    private int _buffer;
    private int _count;

    public int BytePosition { get; private set; } = start;

    public int Get(int n) {
      while (_count < n) {
        if (BytePosition >= _data.Length)
          throw new InvalidDataException("CRUSH bit stream is truncated.");
        _buffer |= _data[BytePosition++] << _count;
        _count += 8;
      }

      var x = _buffer & ((1 << n) - 1);
      _buffer >>= n;
      _count -= n;
      return x;
    }
  }
}
