#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.InterplayAcm;

/// <summary>
/// Interplay ACM decoder — the lossy sub-band audio codec used by Interplay games
/// (Fallout, Baldur's Gate, …), commonly carried in <c>.acm</c> files and inside
/// <c>.bif</c> archives. This is a faithful port of FFmpeg's
/// <c>libavcodec/interplayacm.c</c> (decode-only; there is no published encoder).
/// <para>
/// The bitstream is a sequence of blocks of <c>rows × cols</c> samples, where
/// <c>cols = 1 &lt;&lt; level</c>. Each block reads a per-block amplitude codebook
/// (a power-of-two <c>count</c> and a step <c>val</c> that build the symmetric
/// <c>midbuf</c> lookup), then fills every column via a 5-bit filler-function index
/// (the 32-entry <see cref="FillerKind"/> dispatch: zero / linear / the k-coders and
/// the t15/t27/t37 packed coders), and finally applies the recursive sub-band
/// "juggle" transform. Output samples are <c>block[i] &gt;&gt; level</c>.
/// </para>
/// <para>
/// Ported verbatim: the filler dispatch table, the <c>map_1bit/2bit_near/2bit_far/3bit</c>
/// amplitude maps, the <c>mul_3x3/3x5/2x11</c> packed-triplet/pair tables, the
/// <c>juggle</c> lifting steps and the <c>juggle_block</c> driver. The header layout
/// follows the standalone <c>.acm</c> container (magic, total samples, channels,
/// sample rate, then the level/rows word) which is FFmpeg's extradata block.
/// </para>
/// </summary>
public static class InterplayAcmCodec {

  /// <summary>The little-endian magic word that opens an Interplay ACM file.</summary>
  public const uint Magic = 0x01032897;

  private enum FillerKind {
    Zero, Bad, Linear, K13, K12, T15, K24, K23, T27, K35, K34, K45, K44, T37,
  }

  // filler_list[0..31]: index → filler function (verbatim from interplayacm.c).
  private static readonly FillerKind[] FillerList = [
    FillerKind.Zero,   FillerKind.Bad,    FillerKind.Bad,    FillerKind.Linear, // 0-3
    FillerKind.Linear, FillerKind.Linear, FillerKind.Linear, FillerKind.Linear, // 4-7
    FillerKind.Linear, FillerKind.Linear, FillerKind.Linear, FillerKind.Linear, // 8-11
    FillerKind.Linear, FillerKind.Linear, FillerKind.Linear, FillerKind.Linear, // 12-15
    FillerKind.Linear, FillerKind.K13,    FillerKind.K12,    FillerKind.T15,    // 16-19
    FillerKind.K24,    FillerKind.K23,    FillerKind.T27,    FillerKind.K35,    // 20-23
    FillerKind.K34,    FillerKind.Bad,    FillerKind.K45,    FillerKind.K44,    // 24-27
    FillerKind.Bad,    FillerKind.T37,    FillerKind.Bad,    FillerKind.Bad,    // 28-31
  ];

  private static readonly int[] Map1Bit = [-1, +1];
  private static readonly int[] Map2BitNear = [-2, -1, +1, +2];
  private static readonly int[] Map2BitFar = [-3, -2, +2, +3];
  private static readonly int[] Map3Bit = [-4, -3, -2, -1, +1, +2, +3, +4];

  private static readonly int[] Mul3x3 = BuildMul3(3);
  private static readonly int[] Mul3x5 = BuildMul3(5);
  private static readonly int[] Mul2x11 = BuildMul2(11);

  private static int[] BuildMul3(int radix) {
    var table = new int[radix * radix * radix];
    for (var x3 = 0; x3 < radix; ++x3)
      for (var x2 = 0; x2 < radix; ++x2)
        for (var x1 = 0; x1 < radix; ++x1)
          table[x1 + x2 * radix + x3 * radix * radix] = x1 | (x2 << 4) | (x3 << 8);
    return table;
  }

  private static int[] BuildMul2(int radix) {
    var table = new int[radix * radix];
    for (var x2 = 0; x2 < radix; ++x2)
      for (var x1 = 0; x1 < radix; ++x1)
        table[x1 + x2 * radix] = x1 | (x2 << 4);
    return table;
  }

  private sealed class State {
    public InterplayAcmBitReader Gb = null!;
    public int Level;
    public int Rows;
    public int Cols;
    public int[] Block = null!;
    public int[] WrapBuf = null!;
    // ampbuf is sized 0x10000 with midbuf pointing into its middle (offset 0x8000),
    // so midbuf supports the negative indices used by the symmetric codebook.
    public int[] AmpBuf = null!;
    public int MidOffset;

    public void SetMid(int index, int value) => this.AmpBuf[this.MidOffset + index] = value;
    public int Mid(int index) => this.AmpBuf[this.MidOffset + index];

    // set_pos(s, r, c, idx): block[(r << level) + c] = midbuf[idx].
    public void SetPos(int row, int col, int idx) => this.Block[(row << this.Level) + col] = this.Mid(idx);
  }

  /// <summary>Parsed Interplay ACM header (the standalone <c>.acm</c> container layout).</summary>
  public readonly record struct Header(uint Magic, uint TotalSamples, int Channels, int SampleRate, int Level, int Rows);

  /// <summary>
  /// Parses the 14-byte Interplay ACM header: magic (u32 LE) | total samples (u32 LE)
  /// | channels (u16 LE) | sample rate (u16 LE) | a u16 word carrying level in the low
  /// 4 bits and rows in the upper 12 bits.
  /// </summary>
  public static Header ParseHeader(ReadOnlySpan<byte> file) {
    if (file.Length < 14)
      throw new InvalidDataException("Interplay ACM file is too short for its 14-byte header.");
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(file);
    if (magic != Magic)
      throw new InvalidDataException("Missing Interplay ACM magic 0x01032897.");

    var totalSamples = BinaryPrimitives.ReadUInt32LittleEndian(file[4..]);
    var channels = BinaryPrimitives.ReadUInt16LittleEndian(file[8..]);
    var sampleRate = BinaryPrimitives.ReadUInt16LittleEndian(file[10..]);
    var word = BinaryPrimitives.ReadUInt16LittleEndian(file[12..]);
    var level = word & 0xF;
    var rows = word >> 4;
    return new Header(magic, totalSamples, channels, sampleRate, level, rows);
  }

  /// <summary>
  /// Decodes a complete Interplay ACM file to interleaved 16-bit signed PCM. The
  /// returned channel count is the header value as-is (many Interplay assets report
  /// 1 even for interleaved stereo); callers may re-interpret it. Decoding stops at
  /// the header's total-sample count, or when the bitstream is exhausted (truncated
  /// files decode as far as they can).
  /// </summary>
  public static (short[] Samples, int Channels, int SampleRate) Decode(ReadOnlySpan<byte> file) {
    var header = ParseHeader(file);
    var channels = header.Channels < 1 ? 1 : header.Channels;

    var s = new State {
      Level = header.Level,
      Rows = header.Rows,
      Cols = 1 << header.Level,
      AmpBuf = new int[0x10000],
      MidOffset = 0x8000,
    };
    s.Block = new int[Math.Max(1, s.Rows * s.Cols)];
    s.WrapBuf = new int[Math.Max(1, 2 * s.Cols - 2)];
    s.Gb = new InterplayAcmBitReader(file[14..].ToArray());

    var blockLen = s.Rows * s.Cols;
    // total samples is across all channels; when zero, decode the whole bitstream.
    var maxSamples = header.TotalSamples == 0 ? long.MaxValue : header.TotalSamples;

    var output = new List<short>();
    while (output.Count < maxSamples && blockLen > 0) {
      DecodeBlock(s);
      var take = (int)Math.Min(blockLen, maxSamples - output.Count);
      for (var i = 0; i < take; ++i)
        output.Add((short)(s.Block[i] >> s.Level));

      // Stop once the reader has been driven past the end of the bitstream; the
      // EOF-tolerant reader would otherwise emit silence forever.
      if (s.Gb.BytePosition >= file.Length - 14)
        break;
    }

    return (output.ToArray(), channels, header.SampleRate <= 0 ? 22050 : header.SampleRate);
  }

  private static void DecodeBlock(State s) {
    var pwr = (int)s.Gb.GetBits(4);
    var val = (int)s.Gb.GetBits(16);
    var count = 1 << pwr;

    var x = 0;
    for (var i = 0; i < count; ++i) {
      s.SetMid(i, x);
      x += val;
    }
    x = -val;
    for (var i = 1; i <= count; ++i) {
      s.SetMid(-i, x);
      x -= val;
    }

    FillBlock(s);
    JuggleBlock(s);
  }

  private static void FillBlock(State s) {
    for (var i = 0; i < s.Cols; ++i) {
      var ind = (int)s.Gb.GetBits(5);
      Fill(s, FillerList[ind], ind, i);
    }
  }

  private static void Fill(State s, FillerKind kind, int ind, int col) {
    switch (kind) {
      case FillerKind.Zero: Zero(s, col); break;
      case FillerKind.Bad: break; // FFmpeg returns AVERROR_INVALIDDATA; tolerate by leaving zeros.
      case FillerKind.Linear: Linear(s, ind, col); break;
      case FillerKind.K13: K13(s, col); break;
      case FillerKind.K12: K12(s, col); break;
      case FillerKind.T15: T15(s, col); break;
      case FillerKind.K24: K24(s, col); break;
      case FillerKind.K23: K23(s, col); break;
      case FillerKind.T27: T27(s, col); break;
      case FillerKind.K35: K35(s, col); break;
      case FillerKind.K34: K34(s, col); break;
      case FillerKind.K45: K45(s, col); break;
      case FillerKind.K44: K44(s, col); break;
      case FillerKind.T37: T37(s, col); break;
    }
  }

  private static void Zero(State s, int col) {
    for (var i = 0; i < s.Rows; ++i)
      s.SetPos(i, col, 0);
  }

  private static void Linear(State s, int ind, int col) {
    var middle = 1 << (ind - 1);
    for (var i = 0; i < s.Rows; ++i) {
      var b = (int)s.Gb.GetBits(ind);
      // set_pos stores midbuf[b - middle]; midbuf indexes the symmetric codebook.
      s.SetPos(i, col, b - middle);
    }
  }

  private static void K13(State s, int col) {
    for (var i = 0; i < s.Rows; ++i) {
      if (s.Gb.GetBit() == 0) {
        s.SetPos(i++, col, 0);
        if (i >= s.Rows) break;
        s.SetPos(i, col, 0);
        continue;
      }
      if (s.Gb.GetBit() == 0) {
        s.SetPos(i, col, 0);
        continue;
      }
      var b = (int)s.Gb.GetBit();
      s.SetPos(i, col, Map1Bit[b]);
    }
  }

  private static void K12(State s, int col) {
    for (var i = 0; i < s.Rows; ++i) {
      if (s.Gb.GetBit() == 0) {
        s.SetPos(i, col, 0);
        continue;
      }
      var b = (int)s.Gb.GetBit();
      s.SetPos(i, col, Map1Bit[b]);
    }
  }

  private static void T15(State s, int col) {
    for (var i = 0; i < s.Rows; ++i) {
      var b = (int)s.Gb.GetBits(5);
      if (b > 26) return; // invalid; FFmpeg errors out.
      var n1 = (Mul3x3[b] & 0x0F) - 1;
      var n2 = ((Mul3x3[b] >> 4) & 0x0F) - 1;
      var n3 = ((Mul3x3[b] >> 8) & 0x0F) - 1;
      s.SetPos(i++, col, n1);
      if (i >= s.Rows) break;
      s.SetPos(i++, col, n2);
      if (i >= s.Rows) break;
      s.SetPos(i, col, n3);
    }
  }

  private static void K24(State s, int col) {
    for (var i = 0; i < s.Rows; ++i) {
      if (s.Gb.GetBit() == 0) {
        s.SetPos(i++, col, 0);
        if (i >= s.Rows) break;
        s.SetPos(i, col, 0);
        continue;
      }
      if (s.Gb.GetBit() == 0) {
        s.SetPos(i, col, 0);
        continue;
      }
      var b = (int)s.Gb.GetBits(2);
      s.SetPos(i, col, Map2BitNear[b]);
    }
  }

  private static void K23(State s, int col) {
    for (var i = 0; i < s.Rows; ++i) {
      if (s.Gb.GetBit() == 0) {
        s.SetPos(i, col, 0);
        continue;
      }
      var b = (int)s.Gb.GetBits(2);
      s.SetPos(i, col, Map2BitNear[b]);
    }
  }

  private static void T27(State s, int col) {
    for (var i = 0; i < s.Rows; ++i) {
      var b = (int)s.Gb.GetBits(7);
      if (b > 124) return;
      var n1 = (Mul3x5[b] & 0x0F) - 2;
      var n2 = ((Mul3x5[b] >> 4) & 0x0F) - 2;
      var n3 = ((Mul3x5[b] >> 8) & 0x0F) - 2;
      s.SetPos(i++, col, n1);
      if (i >= s.Rows) break;
      s.SetPos(i++, col, n2);
      if (i >= s.Rows) break;
      s.SetPos(i, col, n3);
    }
  }

  private static void K35(State s, int col) {
    for (var i = 0; i < s.Rows; ++i) {
      if (s.Gb.GetBit() == 0) {
        s.SetPos(i++, col, 0);
        if (i >= s.Rows) break;
        s.SetPos(i, col, 0);
        continue;
      }
      if (s.Gb.GetBit() == 0) {
        s.SetPos(i, col, 0);
        continue;
      }
      if (s.Gb.GetBit() == 0) {
        var b1 = (int)s.Gb.GetBit();
        s.SetPos(i, col, Map1Bit[b1]);
        continue;
      }
      var b = (int)s.Gb.GetBits(2);
      s.SetPos(i, col, Map2BitFar[b]);
    }
  }

  private static void K34(State s, int col) {
    for (var i = 0; i < s.Rows; ++i) {
      if (s.Gb.GetBit() == 0) {
        s.SetPos(i, col, 0);
        continue;
      }
      if (s.Gb.GetBit() == 0) {
        var b1 = (int)s.Gb.GetBit();
        s.SetPos(i, col, Map1Bit[b1]);
        continue;
      }
      var b = (int)s.Gb.GetBits(2);
      s.SetPos(i, col, Map2BitFar[b]);
    }
  }

  private static void K45(State s, int col) {
    for (var i = 0; i < s.Rows; ++i) {
      if (s.Gb.GetBit() == 0) {
        s.SetPos(i, col, 0);
        ++i;
        if (i >= s.Rows) break;
        s.SetPos(i, col, 0);
        continue;
      }
      if (s.Gb.GetBit() == 0) {
        s.SetPos(i, col, 0);
        continue;
      }
      var b = (int)s.Gb.GetBits(3);
      s.SetPos(i, col, Map3Bit[b]);
    }
  }

  private static void K44(State s, int col) {
    for (var i = 0; i < s.Rows; ++i) {
      if (s.Gb.GetBit() == 0) {
        s.SetPos(i, col, 0);
        continue;
      }
      var b = (int)s.Gb.GetBits(3);
      s.SetPos(i, col, Map3Bit[b]);
    }
  }

  private static void T37(State s, int col) {
    for (var i = 0; i < s.Rows; ++i) {
      var b = (int)s.Gb.GetBits(7);
      if (b > 120) return;
      var n1 = (Mul2x11[b] & 0x0F) - 5;
      var n2 = ((Mul2x11[b] >> 4) & 0x0F) - 5;
      s.SetPos(i++, col, n1);
      if (i >= s.Rows) break;
      s.SetPos(i, col, n2);
    }
  }

  // ── sub-band "juggle" transform ──────────────────────────────────────────────

  private static void JuggleBlock(State s) {
    if (s.Level == 0)
      return;

    var stepSubcount = s.Level > 9 ? 1 : (2048 >> s.Level) - 2;

    var todoCount = s.Rows;
    var blockP = 0; // offset into s.Block
    while (true) {
      var wrapP = 0; // offset into s.WrapBuf
      var subCount = stepSubcount;
      if (subCount > todoCount)
        subCount = todoCount;

      var subLen = s.Cols / 2;
      subCount *= 2;

      Juggle(s, wrapP, blockP, subLen, subCount);
      wrapP += subLen * 2;

      var p = blockP;
      for (var i = 0; i < subCount; ++i) {
        ++s.Block[p];
        p += subLen;
      }

      while (subLen > 1) {
        subLen /= 2;
        subCount *= 2;
        Juggle(s, wrapP, blockP, subLen, subCount);
        wrapP += subLen * 2;
      }

      if (todoCount <= stepSubcount)
        break;

      todoCount -= stepSubcount;
      blockP += stepSubcount << s.Level;
    }
  }

  private static void Juggle(State s, int wrapP, int blockP, int subLen, int subCount) {
    var block = s.Block;
    var wrap = s.WrapBuf;
    for (var i = 0; i < subLen; ++i) {
      var p = blockP;
      var r0 = (uint)wrap[wrapP + 0];
      var r1 = (uint)wrap[wrapP + 1];
      for (var j = 0; j < subCount / 2; ++j) {
        var r2 = (uint)block[p];
        block[p] = (int)(r1 * 2 + (r0 + r2));
        p += subLen;
        var r3 = (uint)block[p];
        block[p] = (int)(r2 * 2 - (r1 + r3));
        p += subLen;
        r0 = r2;
        r1 = r3;
      }

      wrap[wrapP++] = (int)r0;
      wrap[wrapP++] = (int)r1;
      ++blockP;
    }
  }
}
