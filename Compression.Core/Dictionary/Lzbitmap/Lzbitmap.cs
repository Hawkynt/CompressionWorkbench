namespace Compression.Core.Dictionary.Lzbitmap;

/// <summary>
/// Apple's LZBITMAP compression (libcompression <c>COMPRESSION_LZBITMAP</c>, decmpfs methods 13/14).
/// </summary>
/// <remarks>
/// <para>
/// A stream is the magic <c>"ZBM\x09"</c> followed by chunks of at most 32 KiB of output, ended
/// by an empty chunk. Every chunk starts with its stored length and its decompressed length
/// (24-bit little-endian each). A chunk whose stored length is its decompressed length plus six
/// holds the bytes verbatim. Otherwise three more 24-bit offsets name, inside the chunk, the
/// period bytes, the explicit bitmaps and the nibble stream; literal bytes follow the 15-byte
/// header and the chunk ends with twelve 10-bit "top" bitmaps packed into 17 bytes.
/// </para>
/// <para>
/// Output is produced eight bytes at a time. Each nibble picks a bitmap — 3-14 one of the top
/// bitmaps, 0-2 the next explicit bitmap with a new period of that many bytes — and a following
/// 0xF nibble run repeats it (4 + the sum of the nibbles until one is not 0xF). A set bit takes a
/// literal byte, a clear bit copies the byte one period back, so a period reaches into earlier
/// chunks. The period starts at 8 in every chunk.
/// </para>
/// <para>
/// Converted from libzbitmap (Ernesto A. Fernández, Corellium LLC, MIT, revision 574abeae; see
/// THIRD-PARTY-NOTICE.libzbitmap.txt), which reverse-engineered the format by black-box testing
/// Apple's implementation. The compressor keeps libzbitmap's pattern search and top-bitmap
/// choice; Apple's own compressor chooses differently, so the output is valid LZBITMAP but not
/// Apple's bytes.
/// </para>
/// </remarks>
public static class Lzbitmap {

  private static ReadOnlySpan<byte> Magic => "ZBM\x09"u8;
  private const int MaxChunk = 0x8000;
  private const int ChunkHeader = 6;
  private const int CompressedHeader = 15;
  private const int TopCount = 12;
  private const int TopBytes = 17;

  private static int U24(ReadOnlySpan<byte> s) => s[0] | (s[1] << 8) | (s[2] << 16);

  private static void PutU24(Span<byte> d, int value) {
    d[0] = (byte)value;
    d[1] = (byte)(value >> 8);
    d[2] = (byte)(value >> 16);
  }

  // ── Decompression ──────────────────────────────────────────────────

  /// <summary>Decompresses a whole LZBITMAP stream.</summary>
  /// <exception cref="InvalidDataException">The stream is malformed or truncated.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> src) {
    if (src.Length < Magic.Length || !src[..Magic.Length].SequenceEqual(Magic))
      throw new InvalidDataException("LZBITMAP stream does not start with ZBM\\x09.");

    // First pass: the chunk headers alone give the output length.
    long total = 0;
    var pos = Magic.Length;
    while (true) {
      var (length, decompressed) = ChunkLengths(src, pos);
      total += decompressed;
      pos += length;
      if (decompressed == 0) break;
    }
    if (total > Array.MaxLength) throw new InvalidDataException("LZBITMAP output exceeds the managed buffer limit.");

    var output = new byte[total];
    pos = Magic.Length;
    var written = 0;
    while (true) {
      var (length, decompressed) = ChunkLengths(src, pos);
      if (decompressed == 0) break;
      var chunk = src.Slice(pos, length);
      if (length == decompressed + ChunkHeader)
        chunk[ChunkHeader..].CopyTo(output.AsSpan(written));
      else
        DecodeChunk(chunk, decompressed, output, written);
      written += decompressed;
      pos += length;
    }
    return output;
  }

  private static (int Length, int Decompressed) ChunkLengths(ReadOnlySpan<byte> src, int pos) {
    if (pos + ChunkHeader > src.Length) throw new InvalidDataException("LZBITMAP chunk header is truncated.");
    var length = U24(src[pos..]);
    var decompressed = U24(src[(pos + 3)..]);
    if (length < ChunkHeader || pos + length > src.Length) throw new InvalidDataException("LZBITMAP chunk overruns the stream.");
    if (decompressed > MaxChunk) throw new InvalidDataException("LZBITMAP chunk decompresses to more than 32 KiB.");
    return (length, decompressed);
  }

  private static void DecodeChunk(ReadOnlySpan<byte> chunk, int decompressed, byte[] output, int start) {
    var end = chunk.Length;
    if (end < CompressedHeader || end < TopBytes) throw new InvalidDataException("LZBITMAP chunk is too short.");
    var meta1 = U24(chunk[6..]);
    var meta2 = U24(chunk[9..]);
    var meta3 = U24(chunk[12..]);
    if (meta1 >= end || meta2 >= end || meta3 >= end) throw new InvalidDataException("LZBITMAP metadata offset lies outside the chunk.");
    var data = CompressedHeader;
    var nibble = meta3 * 2;                        // nibble index: low nibble first

    // The twelve top bitmaps: ten bits each (bitmap, then period byte count), LSB first.
    Span<byte> topBitmap = stackalloc byte[TopCount];
    Span<byte> topPeriodBytes = stackalloc byte[TopCount];
    var bit = (end - TopBytes) * 8;
    for (var i = 0; i < TopCount; ++i) {
      int value = 0;
      for (var b = 0; b < 10; ++b, ++bit)
        value |= ((chunk[bit >> 3] >> (bit & 7)) & 1) << b;
      topBitmap[i] = (byte)value;
      topPeriodBytes[i] = (byte)(value >> 8);
      if (topPeriodBytes[i] > 2) throw new InvalidDataException("LZBITMAP top bitmap has a period of more than two bytes.");
    }

    var period = 8;
    var written = 0;
    while (written < decompressed) {
      var number = ReadNibble(chunk, ref nibble);
      var repeat = 1;
      if (decompressed - written > 8) {            // the last group never carries a count
        var next = ReadNibble(chunk, ref nibble);
        if (next != 0xF) {
          --nibble;
        } else {
          repeat = 4;
          while (next == 0xF) {
            next = ReadNibble(chunk, ref nibble);
            repeat += next;
            if (repeat > ushort.MaxValue) throw new InvalidDataException("LZBITMAP repetition count overflows.");
          }
        }
      }

      for (var r = 0; r < repeat; ++r) {
        if (number == 0xF) throw new InvalidDataException("LZBITMAP bitmap number 0xF is a repetition marker.");
        byte bitmap, periodBytes;
        if (number > 2) {
          bitmap = topBitmap[number - 3];
          periodBytes = topPeriodBytes[number - 3];
        } else {
          if (meta2 >= end) throw new InvalidDataException("LZBITMAP explicit bitmaps are truncated.");
          bitmap = chunk[meta2++];
          periodBytes = (byte)number;
        }

        if (periodBytes > 0) {
          period = 0;
          for (var i = 0; i < periodBytes; ++i) {
            if (meta1 >= end) throw new InvalidDataException("LZBITMAP periods are truncated.");
            period |= chunk[meta1++] << (i * 8);
          }
        }
        if (period == 0) throw new InvalidDataException("LZBITMAP period is zero.");

        for (var i = 0; i < 8 && written < decompressed; ++i, ++written) {
          var at = start + written;
          if ((bitmap & (1 << i)) != 0) {
            if (data >= end) throw new InvalidDataException("LZBITMAP literals are truncated.");
            output[at] = chunk[data++];
          } else {
            if (at < period) throw new InvalidDataException("LZBITMAP period reaches before the start of the output.");
            output[at] = output[at - period];
          }
        }
      }
    }
  }

  private static int ReadNibble(ReadOnlySpan<byte> chunk, ref int nibble) {
    if (nibble >> 1 >= chunk.Length) throw new InvalidDataException("LZBITMAP nibble stream is truncated.");
    var value = (chunk[nibble >> 1] >> ((nibble & 1) * 4)) & 0xF;
    ++nibble;
    return value;
  }

  // ── Compression ────────────────────────────────────────────────────

  /// <summary>Compresses <paramref name="src"/> into an LZBITMAP stream.</summary>
  public static byte[] Compress(ReadOnlySpan<byte> src) {
    using var output = new MemoryStream(src.Length / 2 + 64);
    output.Write(Magic);
    var state = new ChunkEncoder();
    var preread = 0;
    while (true) {
      var length = Math.Min(src.Length - preread, MaxChunk);
      var chunk = state.Encode(src, preread, length);
      output.Write(chunk);
      if (length == 0) break;
      preread += length;
    }
    return output.ToArray();
  }

  /// <summary>Thrown inside a chunk when compressing it would not fit its verbatim size.</summary>
  private sealed class CantCompress : Exception;

  private sealed class ChunkEncoder {
    private readonly List<(byte Bitmap, byte PeriodBytes)> _bitmaps = new(MaxChunk / 8 + 1);
    private readonly List<int> _periods = new(MaxChunk / 8 + 1);
    private readonly long[] _usecounts = new long[1 << 10];
    private readonly (byte Bitmap, byte PeriodBytes)[] _top = new (byte, byte)[TopCount];
    private byte[] _dest = [];
    private int _written;
    private int _limit;
    private int _period;

    private static int Key((byte Bitmap, byte PeriodBytes) b) => (b.Bitmap << 2) | b.PeriodBytes;

    public byte[] Encode(ReadOnlySpan<byte> src, int preread, int length) {
      _limit = length + ChunkHeader;               // a chunk never grows past its verbatim form
      _dest = new byte[Math.Max(_limit, CompressedHeader + TopBytes)];
      try {
        BuildCompressed(src, preread, length);
      } catch (CantCompress) {
        _written = ChunkHeader + length;
        _dest = new byte[_written];
        src.Slice(preread, length).CopyTo(_dest.AsSpan(ChunkHeader));
      }
      PutU24(_dest, _written);
      PutU24(_dest.AsSpan(3), length);
      return _dest.AsSpan(0, _written).ToArray();
    }

    private void Put(byte value) {
      if (_written >= _limit) throw new CantCompress();
      _dest[_written++] = value;
    }

    private void BuildCompressed(ReadOnlySpan<byte> src, int preread, int length) {
      if (length < CompressedHeader) throw new CantCompress();   // no room for the 15-byte header
      _written = CompressedHeader;
      _bitmaps.Clear();
      _periods.Clear();
      Array.Clear(_usecounts);
      Array.Clear(_top);

      var read = 0;
      if (preread == 0) {
        // The first eight bytes of a stream are literals under an all-ones bitmap.
        _bitmaps.Add((0xFF, 0));
        _periods.Add(0);
        _usecounts[Key((0xFF, 0))] = 1;
        if (length < 8) throw new CantCompress();
        for (var i = 0; i < 8; ++i) Put(src[i]);
        read = 8;
      }
      _period = 8;

      while (read < length) {
        FindPattern(src, preread, read, length);
        var (bitmap, _) = _bitmaps[^1];
        for (var i = 0; i < 8 && read < length; ++i, ++read) {
          if (_written >= _limit) throw new CantCompress();
          if ((bitmap & (1 << i)) != 0) Put(src[preread + read]);
        }
      }
      BuildMetadata();
    }

    private static int DifferingBytes(ReadOnlySpan<byte> src, int candidate, ulong needle) {
      var bytes = BitConverterLittle(src, candidate) ^ needle;
      var diff = 0;
      for (var i = 0; i < 8; ++i, bytes >>= 8)
        if ((bytes & 0xFF) != 0) ++diff;
      return diff;
    }

    private static ulong BitConverterLittle(ReadOnlySpan<byte> src, int at)
      => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(src[at..]);

    private void FindPattern(ReadOnlySpan<byte> src, int preread, int read, int length) {
      var s = preread + read;
      var count = Math.Min(8, length - read);
      ulong needle = 0;
      for (var i = 0; i < count; ++i) needle |= (ulong)src[s + i] << (i * 8);

      // Cost: changed bytes plus the bytes a new period takes.
      var best = s - _period;
      var bestCost = DifferingBytes(src, best, needle);
      if (bestCost > 1) {
        var split = s - Math.Min(0xFF, s);
        var found = false;
        for (var at = split; at <= s - 8; ++at) {
          var diff = DifferingBytes(src, at, needle);
          if (diff + 1 < bestCost) {
            best = at;
            bestCost = diff + 1;
            if (bestCost == 1) { found = true; break; }
          }
        }
        if (!found && bestCost != 2) {
          // Two-byte periods: only candidates starting with the same byte.
          for (var at = s - Math.Min(0xFFFF, s); at < split;) {
            var next = src[at..split].IndexOf(src[s]);
            if (next < 0) break;
            next += at;
            var diff = DifferingBytes(src, next, needle);
            if (diff + 2 < bestCost) {
              best = next;
              bestCost = diff + 2;
              if (bestCost == 2) break;
            }
            at = next + 1;
          }
        }
      }

      byte bitmap = 0;
      for (var i = 0; i < count; ++i)
        if (src[best + i] != src[s + i]) bitmap |= (byte)(1 << i);
      var period = s - best;
      var periodBytes = (byte)(period == _period ? 0 : period <= 0xFF ? 1 : 2);
      _bitmaps.Add((bitmap, periodBytes));
      _periods.Add(period);
      ++_usecounts[Key((bitmap, periodBytes))];
      _period = period;
    }

    private void BuildMetadata() {
      FindTopBitmaps();

      PutU24(_dest.AsSpan(6), _written);
      for (var i = 0; i < _bitmaps.Count; ++i) {
        var periodBytes = _bitmaps[i].PeriodBytes;
        if (periodBytes == 0) continue;
        Put((byte)_periods[i]);
        if (periodBytes == 2) Put((byte)(_periods[i] >> 8));
      }

      PutU24(_dest.AsSpan(9), _written);
      foreach (var b in _bitmaps)
        if (_usecounts[Key(b)] != 0) Put(b.Bitmap);

      PutU24(_dest.AsSpan(12), _written);
      WriteNibbles();

      if (_limit - _written < TopBytes) throw new CantCompress();
      var bit = _written * 8;
      for (var i = 0; i < TopCount; ++i) {
        var value = _top[i].Bitmap | (_top[i].PeriodBytes << 8);
        for (var b = 0; b < 10; ++b, ++bit)
          if (((value >> b) & 1) != 0) _dest[bit >> 3] |= (byte)(1 << (bit & 7));
      }
      _written += TopBytes;
    }

    private void FindTopBitmaps() {
      // The twelve most used (bitmap, period byte count) pairs, by use count, descending;
      // ties keep first-seen order.
      var tops = new (int Index, long Uses)[TopCount];
      for (var i = 0; i < TopCount; ++i) tops[i] = (-1, 0);
      for (var n = 0; n < _bitmaps.Count; ++n) {
        var uses = _usecounts[Key(_bitmaps[n])];
        int i;
        for (i = 0; i < TopCount; ++i) {
          if (tops[i].Index >= 0 && _bitmaps[tops[i].Index] == _bitmaps[n]) goto next;
          if (tops[i].Uses < uses) break;
        }
        if (i == TopCount) continue;
        Array.Copy(tops, i, tops, i + 1, TopCount - i - 1);
        tops[i] = (n, uses);
        next:;
      }
      for (var i = 0; i < TopCount; ++i) {
        if (tops[i].Uses == 0) {
          _top[i] = (0, 0);
          continue;
        }
        _top[i] = _bitmaps[tops[i].Index];
        _usecounts[Key(_top[i])] = 0;           // a top bitmap is not stored explicitly
      }
    }

    private int NumberFor(int index) {
      var b = _bitmaps[index];
      if (_usecounts[Key(b)] == 0)
        for (var i = 0; i < TopCount; ++i)
          if (_top[i] == b) return i + 3;
      return b.PeriodBytes;
    }

    private void WriteNibbles() {
      var start = _written;
      var nibble = start * 2;
      void PutNibble(int value) {
        if (nibble >> 1 >= _limit) throw new CantCompress();
        if ((nibble & 1) == 0) _dest[nibble >> 1] = (byte)value;
        else _dest[nibble >> 1] |= (byte)(value << 4);
        ++nibble;
      }

      for (var i = 0; i < _bitmaps.Count;) {
        var number = NumberFor(i);
        var repeat = 1;
        while (i + repeat < _bitmaps.Count && NumberFor(i + repeat) == number) ++repeat;
        PutNibble(number);
        if (repeat <= 3) {
          for (var j = 1; j < repeat; ++j) PutNibble(number);
        } else {
          PutNibble(0xF);
          var last = 0xF;
          for (var left = repeat - 4; left > 0; left -= last) {
            last = Math.Min(left, 0xF);
            PutNibble(last);
          }
          if (last == 0xF) PutNibble(0);
        }
        i += repeat;
      }
      _written = (nibble + 1) >> 1;
    }
  }
}
