// Compact Pro RLE and LZH decoding, converted from XADMaster (https://github.com/MacPaw/XADMaster,
// XADCompactProRLEHandle.m / XADCompactProLZHHandle.m, LGPL-2.1-or-later, Copyright (c) 2017-present
// MacPaw Way Ltd. and the original The Unarchiver authors). See THIRD-PARTY-NOTICE.XADMaster.txt.

namespace FileFormat.CompactPro;

/// <summary>Compact Pro's two fork codecs: the mandatory RLE layer and the optional LZH layer beneath it.</summary>
internal static class CompactProCodec {
  private const byte Escape = 0x81;
  private const int LzhBlockSize = 0x1FFF0;
  private const int LzhWindow = 8192;

  /// <summary>Decodes a fork: LZH (when <paramref name="lzh"/>) feeding RLE, until <paramref name="size"/> bytes.</summary>
  public static byte[] DecodeFork(ReadOnlySpan<byte> packed, long size, bool lzh) {
    if (size > int.MaxValue)
      throw new NotSupportedException("Compact Pro forks larger than 2 GiB are not supported.");
    IEnumerator<byte> source = lzh ? new LzhDecoder(packed.ToArray()).GetEnumerator() : Bytes(packed.ToArray()).GetEnumerator();
    try {
      return Rle(source, (int)size);
    } catch (InvalidOperationException e) {
      throw new InvalidDataException("Corrupt Compact Pro fork data.", e);
    }
  }

  private static IEnumerable<byte> Bytes(byte[] data) {
    foreach (var b in data)
      yield return b;
  }

  private static byte[] Rle(IEnumerator<byte> input, int size) {
    var output = new byte[size];
    int saved = 0, repeat = 0;
    var halfEscaped = false;
    int Next() => input.MoveNext() ? input.Current : throw new InvalidDataException("Compact Pro RLE stream ended early.");

    for (var pos = 0; pos < size; ++pos) {
      if (repeat > 0) {
        --repeat;
        output[pos] = (byte)saved;
        continue;
      }
      int b;
      if (halfEscaped) {
        b = Escape;
        halfEscaped = false;
      } else
        b = Next();

      if (b != Escape) {
        output[pos] = (byte)(saved = b);
        continue;
      }
      b = Next();
      if (b == 0x82) {
        b = Next();
        if (b != 0) {
          repeat = b - 2;
          if (repeat < 0)
            throw new InvalidDataException("Compact Pro RLE run shorter than two.");
          output[pos] = (byte)saved;
        } else {
          repeat = 1;
          saved = 0x82;
          output[pos] = Escape;
        }
      } else if (b == Escape) {
        halfEscaped = true;
        output[pos] = (byte)(saved = Escape);
      } else {
        repeat = 1;
        saved = b;
        output[pos] = Escape;
      }
    }
    return output;
  }

  /// <summary>
  /// LZSS over three canonical Huffman codes (literals, lengths, offset high bits) rebuilt every
  /// 0x1FFF0 "cost units"; between blocks the stream skips to a byte boundary and then 2 or 3 bytes,
  /// depending on the parity of the block's byte length — a quirk of the original implementation.
  /// </summary>
  private sealed class LzhDecoder(byte[] data) : IEnumerable<byte> {
    private long _bitPos;

    private int BytePos => (int)((this._bitPos + 7) >> 3);

    private int NextByte() {
      // XAD reads whole bytes at the byte the bit reader has reached (rounded up).
      var p = this.BytePos;
      if (p >= data.Length)
        throw new InvalidOperationException("Compact Pro LZH stream ended.");
      this._bitPos = (long)(p + 1) * 8;
      return data[p];
    }

    private int NextBit() {
      var byteIndex = (int)(this._bitPos >> 3);
      if (byteIndex >= data.Length)
        throw new InvalidOperationException("Compact Pro LZH stream ended.");
      var bit = (data[byteIndex] >> (7 - (int)(this._bitPos & 7))) & 1;
      ++this._bitPos;
      return bit;
    }

    private int NextBits(int n) {
      var v = 0;
      for (var i = 0; i < n; ++i)
        v = (v << 1) | this.NextBit();
      return v;
    }

    private void SkipToByteBoundary() => this._bitPos = (long)this.BytePos * 8;

    private (int[] Lengths, Dictionary<(int Len, int Code), int> Codes) ReadCode(int size) {
      var numBytes = this.NextByte();
      if (numBytes * 2 > size)
        throw new InvalidOperationException("Compact Pro LZH code table too long.");
      var lengths = new int[size];
      for (var i = 0; i < numBytes; ++i) {
        var v = this.NextByte();
        lengths[2 * i] = v >> 4;
        lengths[2 * i + 1] = v & 0x0F;
      }
      // canonical, shortest codes first and all-zero
      var codes = new Dictionary<(int, int), int>();
      var code = 0;
      for (var len = 1; len <= 15; ++len) {
        for (var s = 0; s < size; ++s)
          if (lengths[s] == len)
            codes[(len, code++)] = s;
        code <<= 1;
      }
      if (codes.Count == 0)
        throw new InvalidOperationException("Compact Pro LZH empty code.");
      return (lengths, codes);
    }

    private int ReadSymbol(Dictionary<(int Len, int Code), int> codes) {
      var code = 0;
      for (var len = 1; len <= 15; ++len) {
        code = (code << 1) | this.NextBit();
        if (codes.TryGetValue((len, code), out var symbol))
          return symbol;
      }
      throw new InvalidOperationException("Invalid Compact Pro LZH code.");
    }

    public IEnumerator<byte> GetEnumerator() {
      var window = new byte[LzhWindow];
      long pos = 0;
      var blockCount = LzhBlockSize;
      var blockStart = 0;
      Dictionary<(int, int), int>? literals = null, lengths = null, offsets = null;
      while (true) {
        int length, offset;
        bool isLiteral;
        int literal = 0;
        try {
          if (blockCount >= LzhBlockSize) {
            if (blockStart != 0) {
              this.SkipToByteBoundary();
              var skip = ((this.BytePos - blockStart) & 1) != 0 ? 3 : 2;
              this._bitPos += skip * 8;
            }
            literals = this.ReadCode(256).Codes;
            lengths = this.ReadCode(64).Codes;
            offsets = this.ReadCode(128).Codes;
            blockCount = 0;
            blockStart = this.BytePos;
          }
          if (this.NextBit() == 1) {
            blockCount += 2;
            literal = this.ReadSymbol(literals!);
            isLiteral = true;
            length = offset = 0;
          } else {
            blockCount += 3;
            length = this.ReadSymbol(lengths!);
            offset = (this.ReadSymbol(offsets!) << 6) | this.NextBits(6);
            isLiteral = false;
          }
        } catch (InvalidOperationException) {
          yield break; // end of stream, as the reference decoder treats any read failure
        }
        if (isLiteral) {
          window[pos & (LzhWindow - 1)] = (byte)literal;
          ++pos;
          yield return (byte)literal;
          continue;
        }
        var source = pos - offset;
        for (var i = 0; i < length; ++i) {
          var b = window[(source + i) & (LzhWindow - 1)];
          window[pos & (LzhWindow - 1)] = b;
          ++pos;
          yield return b;
        }
      }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();
  }

  /// <summary>
  /// RLE-encodes a fork the way <see cref="Rle"/> decodes it: runs of four or more bytes (other than the
  /// escape 0x81 and 0x82) become "byte, 81 82 count"; a literal 0x81 is followed by its successor,
  /// with "81 82 00" standing for the pair 0x81 0x82; a trailing 0x81 gets a pad byte the decoder reads
  /// but never outputs.
  /// </summary>
  public static byte[] EncodeRle(ReadOnlySpan<byte> data) {
    var output = new List<byte>(data.Length + 16);
    var i = 0;
    while (i < data.Length) {
      var v = data[i];
      var run = 1;
      while (i + run < data.Length && data[i + run] == v && run < 255)
        ++run;
      if (v != Escape && v != 0x82 && run >= 4) {
        output.Add(v);
        output.Add(Escape);
        output.Add(0x82);
        output.Add((byte)run);
        i += run;
        continue;
      }
      if (v != Escape) {
        output.Add(v);
        ++i;
        continue;
      }
      // literal 0x81
      if (i + 1 < data.Length && data[i + 1] == 0x82) {
        output.Add(Escape);
        output.Add(0x82);
        output.Add(0x00);
        i += 2;
        continue;
      }
      output.Add(Escape);
      ++i;
      if (i == data.Length)
        output.Add(0x00); // pad: read by the decoder, never output
    }
    return [.. output];
  }
}
