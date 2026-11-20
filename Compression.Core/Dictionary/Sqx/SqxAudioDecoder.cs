using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Sqx;

/// <summary>
/// SQX method 5 ("audio"): a WAV file as an 18-byte audio header (skip, tail and sample byte
/// counts, channels, bits per sample), the skipped header bytes raw, the coded samples and the
/// tail bytes raw. Format 1.1 codes 8-bit samples with Huffman-coded deltas behind six
/// adaptively chosen predictors and 16-bit samples in 384-sample frames with polynomial
/// prediction; format 2.0 codes 8-bit samples with a range coder over nibble models and an
/// adaptive delta predictor, and 16-bit samples in 4608-sample frames with polynomial or FIR
/// prediction and fixed-width or adaptive Rice residuals. Converted from Squeez-c (MIT)
/// src/audio.c. The file CRC of an audio member covers the audio header as well as the file.
/// </summary>
public sealed class SqxAudioDecoder {
  /// <summary>Bytes of the audio header at the start of every stream.</summary>
  public const int HeaderSize = 18;

  private const int Frame11 = 384, Frame20 = 0x1200;
  private const uint UnaryLimit = 0x100000;

  /// <summary>Decodes one file of <paramref name="originalSize"/> bytes.</summary>
  /// <returns>The file, and the audio header its CRC also covers.</returns>
  /// <exception cref="InvalidDataException">The stream is malformed.</exception>
  /// <exception cref="NotSupportedException">The stream uses a predictor no known archiver writes.</exception>
  public (byte[] Data, byte[] Header) Decode(byte[] packed, long originalSize, int archiveVersion) {
    ArgumentNullException.ThrowIfNull(packed);
    if (packed.Length < HeaderSize || originalSize is <= 0 or > int.MaxValue)
      throw new InvalidDataException("SQX: an audio stream is too short or too large.");
    var bits = new SqxBitReader(packed, 0, packed.Length);
    var header = new byte[HeaderSize];
    for (var i = 0; i < HeaderSize; ++i)
      header[i] = (byte)bits.Read(8);
    var skip = BinaryPrimitives.ReadUInt32LittleEndian(header);
    var tail = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
    var raw = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
    var channels = header[12];
    var sampleBits = header[13];
    if (channels is not (1 or 2) || sampleBits is not (8 or 16) || (long)skip + raw + tail != originalSize)
      throw new InvalidDataException("SQX: the audio header does not describe the file.");

    var output = new Output(new byte[originalSize], packed.Length);
    output.Raw(bits, skip);
    if (archiveVersion >= 20) {
      if (sampleBits == 8) Samples8V20(bits, output, channels, raw);
      else Samples16V20(bits, output, channels, raw);
    } else if (sampleBits == 16) Samples16V11(bits, output, channels, raw);
    else Samples8V11(bits, output, channels, raw);
    output.Raw(bits, tail);
    if (output.Length != originalSize)
      throw new InvalidDataException("SQX: the audio stream does not fill the file.");
    return (output.Buffer, header);
  }

  /// <summary>The decoded file; writes past its end are dropped, reads past the stream fail.</summary>
  private sealed class Output(byte[] buffer, int packedLength) {
    public readonly byte[] Buffer = buffer;
    public int Length;

    public void Put8(byte value) {
      if (this.Length < this.Buffer.Length)
        this.Buffer[this.Length++] = value;
    }

    public void Put16(short value) {
      this.Put8((byte)value);
      this.Put8((byte)((ushort)value >> 8));
    }

    public void Raw(SqxBitReader bits, uint count) {
      for (var i = 0u; i < count; ++i)
        this.Put8((byte)bits.Read(8));
      this.Check(bits);
    }

    public void Check(SqxBitReader bits) {
      if (bits.Position > (packedLength + 16L) * 8)
        throw new InvalidDataException("SQX: the audio stream ends early.");
    }
  }

  // ---------- format 1.1, 8-bit: Huffman-coded deltas behind six predictors ----------

  private sealed class Delta11(uint update) {
    private readonly uint[,] _freqs = new uint[6, 256];
    private readonly byte[] _s = new byte[4];
    private uint _delta, _count;

    public byte Decode(byte c) {
      var s = this._s;
      var cur = this._delta switch {
        0 => c,
        1 => (byte)(s[0] - c),
        2 => (byte)(2 * s[0] - s[1] - c),
        3 => (byte)(3 * s[0] - 3 * s[1] + s[2] - c),
        4 => (byte)(((4 * s[0] - 3 * s[1] + s[2]) >> 1) - c),
        _ => (byte)(4 * s[0] + 6 * s[1] - 4 * s[2] + s[3] - c),
      };
      Span<byte> err = [
        cur, (byte)(s[0] - cur), (byte)(2 * s[0] - s[1] - cur), (byte)(3 * s[0] - 3 * s[1] + s[2] - cur),
        (byte)(((4 * s[0] - 3 * s[1] + s[2]) >> 1) - cur), (byte)(4 * s[0] + 6 * s[1] - 4 * s[2] + s[3] - cur),
      ];
      for (var i = 0; i < 6; ++i)
        ++this._freqs[i, err[i]];
      s[3] = s[2];
      s[2] = s[1];
      s[1] = s[0];
      s[0] = cur;
      if ((this._count & update) == update) {
        this._delta = Fewest(this._freqs);
        Array.Clear(this._freqs);
      }
      ++this._count;
      return cur;
    }
  }

  /// <summary>The predictor whose errors took the fewest distinct values (the first on a tie).</summary>
  private static uint Fewest(uint[,] freqs) {
    uint best = uint.MaxValue, index = 0;
    for (var i = 0u; i < 6; ++i) {
      var count = 0u;
      for (var k = 0; k < 256; ++k)
        if (freqs[i, k] > 0) ++count;
      if (count < best) {
        best = count;
        index = i;
      }
    }
    return index;
  }

  private static void Samples8V11(SqxBitReader bits, Output output, int channels, uint raw) {
    var meta = new SqxHuffman(SqxTables.MetaCodes, SqxTables.MetaCodeBits);
    SqxHuffman[] tables = [new(0x102, SqxTables.CodeBits), new(0x102, SqxTables.CodeBits)];
    var update = channels == 2 ? 511u : 1023u;
    Delta11[] delta = [new(update), new(update)];
    uint block = 0, index = 0;
    var two = false;
    while (raw > 0) {
      if (block == 0) {
        block = bits.Read(14);
        if (channels == 2) {
          two = bits.Read(1) != 0;
          index = 0;
        }
        for (var i = 0; i < meta.Symbols; ++i)
          meta.Lengths[i] = (byte)bits.Read(4);
        if (!meta.Build() || !tables[0].ReadTree(bits, meta) || (channels == 2 && two && !tables[1].ReadTree(bits, meta)))
          throw new InvalidDataException("SQX: an audio block's code tables are malformed.");
        output.Check(bits);
      }
      var symbol = tables[0].Decode(bits);
      --block;
      output.Put8(delta[channels == 2 ? index : 0].Decode((byte)symbol));
      if (channels == 2 && !two)
        index ^= 1;
      --raw;
      if (channels == 2 && two && raw > 0) {
        output.Put8(delta[1].Decode((byte)tables[1].Decode(bits)));
        --raw;
      }
      output.Check(bits);
    }
  }

  // ---------- format 1.1, 16-bit: 384-sample frames, polynomial prediction ----------

  private sealed class Predictor11 {
    public int Start;
    public readonly int[] Last = new int[8];
    public uint Predictor;

    public void Read(SqxBitReader bits, int size, int[] values, Output output) {
      this.Predictor = bits.Read(3);
      var shift = (int)bits.Read(4);
      for (var i = 0; i < size; ++i) {
        var negative = bits.Read(1) == 1;
        var count = 0u;
        while (bits.Read(1) == 0)
          if (++count > UnaryLimit)
            throw new InvalidDataException("SQX: an audio residual never ends.");
        var v = shift > 0 ? bits.Read(shift) : 0;
        if (count > 0)
          v |= count << shift;
        values[i] = negative ? -(int)v : (int)v;
      }
      output.Check(bits);
    }

    // a running correction from the residuals before, as the format's predictors 0, 1, 2 and 4 apply it
    private static int Correction(int last, int beforeLast, int shift, bool half) {
      if (last == 0) return 0;
      var d = Math.Abs(last) >> shift;
      if (half && (last < 0 ? beforeLast < 0 : beforeLast > 0)) d += d >> 1;
      return last < 0 ? -d : d;
    }

    public void Desample(int[] data, int size, int[] output) {
      int le0 = 0, le1 = 0, le1b = 0, le2 = 0, le2b = 0, le4 = 0, le4b = 0;
      var lv = this.Last;
      for (var i = 0; i < size; ++i) {
        var b = data[i];
        int sample;
        switch (this.Predictor) {
          case 0:
            sample = b + Correction(le0, 0, 4, false) + this.Start;
            le0 = b;
            break;
          case 1:
            sample = b + Correction(le1, le1b, 1, true) + lv[0];
            le1b = le1;
            le1 = b;
            break;
          case 2:
            sample = b + Correction(le2, le2b, 1, true) + (2 * lv[0] - lv[1]);
            le2b = le2;
            le2 = b;
            break;
          case 3:
            sample = b + (3 * lv[0] - 3 * lv[1] + lv[2]);
            break;
          case 4:
            sample = b + Correction(le4, le4b, 1, true) + ((4 * lv[0] - 3 * lv[1] + lv[2]) >> 1);
            le4b = le4;
            le4 = b;
            break;
          default:
            sample = b;
            break;
        }
        lv[3] = lv[2];
        lv[2] = lv[1];
        lv[1] = lv[0];
        lv[0] = this.Start = sample;
        output[i] = sample;
      }
    }
  }

  private static void Samples16V11(SqxBitReader bits, Output output, int channels, uint raw) {
    var left = new Predictor11();
    var right = new Predictor11();
    int[] lb = new int[Frame11], rb = new int[Frame11], ls = new int[Frame11], rs = new int[Frame11];
    var bytesPerFrameSample = channels == 2 ? 4u : 2u;
    while (raw >= 4) {
      var last = bits.Read(1) == 1;
      var icd = bits.Read(2);
      if (channels != 2) icd = 0;
      var size = last ? (int)bits.Read(11) : Frame11;
      if (size is 0 or > Frame11 || (uint)size * bytesPerFrameSample > raw)
        throw new InvalidDataException("SQX: an audio frame is malformed.");
      raw -= (uint)size * bytesPerFrameSample;
      left.Read(bits, size, lb, output);
      left.Desample(lb, size, ls);
      if (channels == 1) {
        for (var i = 0; i < size; ++i)
          output.Put16((short)ls[i]);
        continue;
      }
      right.Read(bits, size, rb, output);
      right.Desample(rb, size, rs);
      for (var i = 0; i < size; ++i) {
        int l = ls[i], r = rs[i];
        if (icd == 1) {
          r -= l >> 1;
          l += r;
        } else if (icd == 2) {
          r -= l >> 1;
          l += r >> 1;
        }
        output.Put16((short)l);
        output.Put16((short)r);
      }
    }
  }

  // ---------- format 2.0, 16-bit: 4608-sample frames ----------

  private sealed class Packet {
    public bool Polynomial, Rice, Integrate;
    public uint Predictor, Count, Shift;
    public readonly int[] Coefficients = new int[32];

    public void Read(SqxBitReader bits) {
      if (bits.Read(1) == 0) {
        this.Polynomial = true;
        this.Predictor = bits.Read(3);
        this.Rice = bits.Read(1) != 0;
        return;
      }
      this.Polynomial = false;
      this.Count = bits.Read(5) + 1;
      var width = (int)bits.Read(6);
      if (width > 33)
        throw new InvalidDataException("SQX: an audio filter is malformed.");
      for (var i = 0; i < this.Count; ++i) {
        var negative = bits.Read(1) != 0;
        var v = width > 1 ? bits.Read(width - 1) : 0;
        this.Coefficients[i] = negative ? -(int)v : (int)v;
      }
      this.Shift = bits.Read(4);
      this.Integrate = bits.Read(1) != 0;
      this.Rice = bits.Read(1) != 0;
    }
  }

  private static uint Unary(SqxBitReader bits) {
    var count = 0u;
    while (bits.Peek(1) == 0) {
      bits.Skip(1);
      if (++count > UnaryLimit)
        throw new InvalidDataException("SQX: an audio residual never ends.");
    }
    bits.Skip(1);
    return count;
  }

  private static void FixedResiduals(SqxBitReader bits, int size, int[] output) {
    var width = (int)bits.Read(4);
    for (var i = 0; i < size; ++i) {
      var negative = bits.Read(1) != 0;
      var v = Unary(bits) << width;
      if (width > 0)
        v |= bits.Read(width);
      output[i] = negative ? -(int)v : (int)v;
    }
  }

  private static void RiceResiduals(SqxBitReader bits, int size, int[] output) {
    var k = 14;
    var accumulator = 1u << 14;
    for (var i = 0; i < size; ++i) {
      var negative = bits.Read(1) != 0;
      var unary = Unary(bits);
      var n = k > 5 ? k - 5 : 0;
      var v = n > 0 ? bits.Read(n) | (unary << n) : unary;
      output[i] = negative ? -(int)v : (int)v;
      accumulator += v - (accumulator >> 5);
      if (k > 4 && accumulator < 1u << k)
        --k;
      else if (k < 31 && accumulator >= 1u << (k + 1))
        ++k;
    }
  }

  // |v| with int.MinValue kept as is
  private static int Abs(int v) => v < 0 ? (int)(0u - (uint)v) : v;

  /// <summary>
  /// Polynomial predictor 4: per sample the residual is integrated zero, one or two times,
  /// whichever of three running magnitude estimates (over the last 16 positions) is smallest.
  /// </summary>
  private static void Adaptive(int size, int[] res, int[] output) {
    Span<int> a1 = stackalloc int[16], a2 = stackalloc int[16], a3 = stackalloc int[16];
    int acc1 = 0, acc2 = 0, acc3 = 0, sum = 0, delta = 0, eax = 0, ebp = 0, ecx = 0;
    for (var i = 0; i < size; ++i) {
      var idx = i & 15;
      if (i != 0) (eax, ebp, ecx) = (acc1, acc2, acc3);
      var v = res[i];
      int edx;
      if (eax < ebp && eax < ecx) {
        eax = v - sum;
        ecx = v;
        sum = v;
        edx = eax - delta;
        delta = eax;
      } else if (ebp < ecx) {
        edx = v - delta;
        ecx = v + sum;
        eax = v;
        sum = ecx;
        delta = v;
      } else {
        eax = v + delta;
        edx = v;
        delta = eax;
        ecx = eax + sum;
        sum = ecx;
      }
      output[i] = ecx;
      var m = Abs(ecx);
      acc1 = acc1 - a1[idx] + m;
      a1[idx] = (a1[idx] + m) >> 1;
      m = Abs(eax);
      acc2 = acc2 - a2[idx] + m;
      a2[idx] = (a2[idx] + m) >> 1;
      m = Abs(edx);
      acc3 = acc3 - a3[idx] + m;
      a3[idx] = (a3[idx] + m) >> 1;
    }
  }

  private static void Polynomial(uint predictor, int size, int[] res, int[] output) {
    switch (predictor) {
      case 0:
        Array.Copy(res, output, size);
        break;
      case 1: {
        var s = 0;
        for (var i = 0; i < size; ++i)
          output[i] = s += res[i];
        break;
      }
      case 2: {
        int p1 = 0, p2 = 0;
        for (var i = 0; i < size; ++i) {
          var v = res[i] + 2 * p1 - p2;
          output[i] = v;
          (p2, p1) = (p1, v);
        }
        break;
      }
      case 3: {
        int p1 = 0, p2 = 0, p3 = 0;
        for (var i = 0; i < size; ++i) {
          var v = res[i] + 3 * (p1 - p2) + p3;
          output[i] = v;
          (p3, p2, p1) = (p2, p1, v);
        }
        break;
      }
      case 4:
        Adaptive(size, res, output);
        break;
      default:
        throw new NotSupportedException($"SQX: audio predictor {predictor} is not one any archiver writes.");
    }
  }

  /// <summary>
  /// The FIR path: optionally res[i] += res[i-1] / 2 (on the residuals as read), then the first
  /// <c>count</c> samples as they are and each later one plus the coefficient-weighted sum of the
  /// samples before (64-bit, arithmetically shifted), then out[i] += 3 out[i-1] / 4.
  /// </summary>
  private static void Filters(Packet p, int size, int[] res, int[] output) {
    var n = (int)Math.Min(p.Count, (uint)size);
    if (p.Integrate) {
      var prev = 0;
      for (var i = 0; i < size; ++i) {
        var cur = res[i];
        res[i] = cur + (prev >> 1);
        prev = cur;
      }
    }
    Array.Copy(res, output, n);
    for (var i = n; i < size; ++i) {
      var acc = 0L;
      for (var j = 0; j < n; ++j)
        acc += (long)p.Coefficients[j] * output[i - 1 - j];
      output[i] = res[i] + (int)(acc >> (int)p.Shift);
    }
    var before = 0;
    for (var i = 0; i < size; ++i) {
      var cur = output[i];
      output[i] = cur + ((3 * before) >> 2);
      before = cur;
    }
  }

  private static void Samples16V20(SqxBitReader bits, Output output, int channels, uint raw) {
    int[][] res = [new int[Frame20], new int[Frame20]];
    int[][] smp = [new int[Frame20], new int[Frame20]];
    Packet[] packets = [new(), new()];
    while (raw >= 4) {
      var last = bits.Read(1) == 1;
      var icd = bits.Read(2);
      var size = last ? (int)bits.Read(13) : Frame20;
      if (size is 0 or > Frame20 || (long)size * channels * 2 > raw)
        throw new InvalidDataException("SQX: an audio frame is malformed.");
      raw -= (uint)(size * channels * 2);
      for (var c = 0; c < channels; ++c) {
        var p = packets[c];
        p.Read(bits);
        if (p.Rice) RiceResiduals(bits, size, res[c]);
        else FixedResiduals(bits, size, res[c]);
        output.Check(bits);
        if (p.Polynomial) Polynomial(p.Predictor, size, res[c], smp[c]);
        else Filters(p, size, res[c], smp[c]);
      }
      if (channels == 1) {
        for (var i = 0; i < size; ++i)
          output.Put16((short)smp[0][i]);
        continue;
      }
      // the second channel is the right one, or a difference: [L, L-R], [L+R, L], [L+R/2, L-R/2]
      for (var i = 0; i < size; ++i) {
        int l = smp[0][i], r = smp[1][i];
        short l16 = (short)l, r16 = (short)r, o0, o1;
        switch (icd) {
          case 1:
            o0 = l16;
            o1 = (short)(l16 - r16);
            break;
          case 2:
            o0 = (short)(l16 + r16);
            o1 = l16;
            break;
          case 3: {
            var m = 2 * l + (r & 1);
            o0 = (short)((r + m) >> 1);
            o1 = (short)((m - r) >> 1);
            break;
          }
          default:
            o0 = l16;
            o1 = r16;
            break;
        }
        output.Put16(o0);
        output.Put16(o1);
      }
    }
  }

  // ---------- format 2.0, 8-bit: range-coded nibbles, adaptive delta ----------

  private sealed class Delta20 {
    private readonly uint[,] _freqs = new uint[6, 256];
    private byte _s0, _s1, _s2, _s3;
    private uint _select, _count;

    public byte Decode(uint symbol) {
      byte s0 = this._s0, s1 = this._s1, s2 = this._s2, s3 = this._s3;
      var prediction = this._select switch {
        1 => s0,
        2 => (byte)(2 * s0 + s1),
        3 => (byte)(3 * (s0 + s1) - s2),
        4 => (byte)((4 * s0 - 3 * s1 + s2) >> 1),
        5 => (byte)(6 * (byte)(42 * s2 + s1) + 4 * s0 + s3),
        _ => (byte)0,
      };
      var cur = (byte)(prediction + symbol);
      Span<byte> e = [
        cur, (byte)(cur - s0), (byte)(cur - 2 * s0 - s1), (byte)(cur + s2 - 3 * (s0 + s1)),
        (byte)(cur - ((4 * s0 - 3 * s1 + s2) >> 1)), (byte)(cur + 4 * (byte)(6 * (byte)((byte)(63 * s0) + s2)) - s1 - s3),
      ];
      if (this._count >= 0x240) {
        this._select = Fewest(this._freqs);
        Array.Clear(this._freqs);
        this._count = 0;
      }
      for (var i = 0; i < 6; ++i)
        ++this._freqs[i, e[i]];
      ++this._count;
      (this._s3, this._s2, this._s1, this._s0) = (s2, s1, s0, cur);
      return cur;
    }
  }

  private static void Samples8V20(SqxBitReader bits, Output output, int channels, uint raw) {
    if (raw % channels != 0)
      throw new InvalidDataException("SQX: an 8-bit audio stream does not hold whole sample frames.");
    // per channel: 256 contexts (the previous byte) of a high-nibble model and 16 low-nibble models
    var models = new SqxRangeModel[channels][][];
    for (var c = 0; c < channels; ++c) {
      models[c] = new SqxRangeModel[256][];
      for (var x = 0; x < 256; ++x) {
        models[c][x] = new SqxRangeModel[17];
        for (var m = 0; m < 17; ++m)
          models[c][x][m] = new SqxRangeModel(16, 4, 0x800, true, 4);
      }
    }
    Delta20[] delta = [new(), new()];
    var rc = new SqxRangeDecoder();
    rc.Start(bits);
    var context = 0;
    for (var left = raw; left >= channels; left -= (uint)channels) {
      for (var c = 0; c < channels; ++c) {
        var set = models[c][context];
        var high = rc.Decode(set[0], bits);
        var symbol = (int)((high << 4) | rc.Decode(set[high + 1], bits));
        context = symbol;
        output.Put8(delta[c].Decode((uint)symbol));
      }
      output.Check(bits);
    }
  }
}
