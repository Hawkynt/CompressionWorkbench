#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Codec.Bonk;

/// <summary>
/// Bonk audio decoder, ported from ffmpeg <c>libavcodec/bonk.c</c> (and the file
/// layout from <c>libavformat/bonk.c</c>). Bonk uses an adaptive lattice (LPC) of
/// up to 2048 taps whose coefficients are sent per packet through an adaptive
/// Golomb-style integer-list coder (<c>intlist_read</c>), then predicts each
/// sample via the lattice. Optional mid/side stereo and integer downsampling are
/// supported. Only the lossless path is exercised for verification (a crafted
/// packet round-trips byte-exact); lossy quantisation is honoured per the source.
/// <para>
/// The on-disk file is a <c>'\0BONK'</c> tag followed by a 17-byte header
/// (version, total-samples, sample-rate, channels, lossless / mid-side flags,
/// tap count, downsampling, samples-per-packet), then the raw bitstream of all
/// packets. The decoder buffers the whole bitstream and decodes packets until the
/// declared sample count is exhausted.
/// </para>
/// </summary>
public static class BonkCodec {

  private const int LatticeShift = 10;
  private const int SampleShift = 4;
  private const int SampleFactor = 1 << SampleShift;
  public const int HeaderBytes = 17;

  /// <summary>Decoded stream geometry.</summary>
  public readonly record struct BonkStreamInfo(
    int Channels, int SampleRate, long SamplesPerChannel,
    bool Lossless, bool MidSide, int NTaps, int DownSampling, int SamplesPerPacket);

  // shift_down(a,b) = (a >> b) + (a < 0).
  private static int ShiftDown(int a, int b) => (a >> b) + (a < 0 ? 1 : 0);

  // shift(a,b) = ((a + (1 << (b-1))) << ... ) — the exact ffmpeg precedence quirk:
  //   a + (1 << b - 1) >> b  ==  ((a + 1) << (b - 1)) >> b
  private static int Shift(int a, int b) => ((a + 1) << (b - 1)) >> b;

  private static int ClipInt16(int v) => v < -32768 ? -32768 : v > 32767 ? 32767 : v;

  private struct BitCount {
    public int Bit;
    public int Count;
  }

  // ── Header ───────────────────────────────────────────────────────────────

  /// <summary>Reads the <c>'\0BONK'</c> tag + 17-byte header from the start of a Bonk file.</summary>
  public static BonkStreamInfo ReadStreamInfo(ReadOnlySpan<byte> file, out int dataOffset) {
    var tagOffset = FindTag(file);
    if (tagOffset < 0)
      throw new InvalidDataException("Not a Bonk stream: missing '\\0BONK' tag.");

    var h = file.Slice(tagOffset + 5, HeaderBytes);
    if (h[0] != 0)
      throw new InvalidDataException($"Unsupported Bonk version {h[0]}.");

    var channels = h[9];
    if (channels is < 1 or > 2)
      throw new InvalidDataException("Bonk supports 1 or 2 channels.");

    var totalSamples = BinaryPrimitives.ReadUInt32LittleEndian(h[1..]);
    var sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[5..]);
    var lossless = h[10] != 0;
    var midSide = h[11] != 0;
    var nTaps = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]);
    if (nTaps is 0 or > 2048)
      throw new InvalidDataException("Bonk tap count out of range.");
    var downSampling = h[14];
    if (downSampling == 0)
      throw new InvalidDataException("Bonk downsampling must be non-zero.");
    var samplesPerPacket = BinaryPrimitives.ReadUInt16LittleEndian(h[15..]);

    dataOffset = tagOffset + 5 + HeaderBytes;
    var perChannel = channels == 0 ? 0 : totalSamples / (uint)channels;
    return new BonkStreamInfo(channels, sampleRate, perChannel, lossless, midSide, nTaps, downSampling, samplesPerPacket);
  }

  /// <summary>Locates the <c>'\0BONK'</c> tag (a NUL byte followed by 'BONK') in the leading bytes.</summary>
  internal static int FindTag(ReadOnlySpan<byte> file) {
    var limit = Math.Min(file.Length - 5, 1024);
    for (var i = 0; i <= limit; ++i)
      if (file[i] == 0 && file[i + 1] == 'B' && file[i + 2] == 'O' && file[i + 3] == 'N' && file[i + 4] == 'K')
        return i;
    return -1;
  }

  // ── Decode ───────────────────────────────────────────────────────────────

  /// <summary>Decodes a Bonk file to raw interleaved little-endian 16-bit PCM.</summary>
  public static byte[] Decompress(ReadOnlySpan<byte> file) {
    var info = ReadStreamInfo(file, out var dataOffset);
    var bitstream = file[dataOffset..].ToArray();
    return DecodeBitstream(bitstream, info);
  }

  internal static byte[] DecodeBitstream(byte[] bitstream, BonkStreamInfo info) {
    var channels = info.Channels;
    var nTaps = info.NTaps;
    var downSampling = info.DownSampling;
    var samplesPerPacket = info.SamplesPerPacket;
    var outFramesPerPacket = samplesPerPacket * downSampling;

    var reader = new BonkBitReader(bitstream);

    // Persistent per-channel lattice state.
    var state = new int[channels][];
    for (var c = 0; c < channels; ++c)
      state[c] = new int[nTaps];
    var k = new int[nTaps];
    var quant = new byte[nTaps];
    for (var i = 0; i < nTaps; ++i)
      quant[i] = 1; // ffmpeg's s->quant defaults to 1 (set from extradata; lossless path keeps 1).
    var bits = new BitCount[Math.Max(1, nTaps) * 2 + samplesPerPacket * 2 + 16];
    var inputSamples = new int[Math.Max(samplesPerPacket, 1)];
    var samples = new int[channels][];
    for (var c = 0; c < channels; ++c)
      samples[c] = new int[outFramesPerPacket + nTaps + 4];

    using var pcm = new MemoryStream();
    var remaining = info.SamplesPerChannel;

    // This reader walks the whole bitstream contiguously (packets are not byte-padded
    // in our stored layout), so no per-packet re-skip is applied.
    while (remaining > 0) {
      var frameSamples = (int)Math.Min(outFramesPerPacket, remaining);

      ReadIntList(reader, k, nTaps, false, bits);
      for (var i = 0; i < nTaps; ++i)
        k[i] *= quant[i];
      var packetQuant = info.Lossless ? 1 : (int)reader.GetBits(16) * SampleFactor;

      for (var ch = 0; ch < channels; ++ch) {
        var offset = samplesPerPacket * downSampling - 1;
        var st = state[ch];
        var sample = samples[ch];
        var sampleIdx = 0;

        PredictorInitState(k, st, nTaps);
        ReadIntList(reader, inputSamples, samplesPerPacket, true, bits);

        for (var i = 0; i < samplesPerPacket; ++i) {
          for (var j = 0; j < downSampling - 1; ++j) {
            sample[sampleIdx++] = PredictorCalcError(k, st, nTaps, 0);
          }
          sample[sampleIdx++] = PredictorCalcError(k, st, nTaps, inputSamples[i] * packetQuant);
        }

        for (var i = 0; i < nTaps; ++i)
          st[i] = sample[offset - i];
      }

      if (info.MidSide && channels == 2) {
        for (var i = 0; i < frameSamples; ++i) {
          samples[1][i] += Shift(samples[0][i], 1);
          samples[0][i] -= samples[1][i];
        }
      }

      if (!info.Lossless) {
        for (var ch = 0; ch < channels; ++ch)
          for (var i = 0; i < frameSamples; ++i)
            samples[ch][i] = Shift(samples[ch][i], 4);
      }

      // Interleave and emit clipped 16-bit samples.
      var outBlock = new byte[frameSamples * channels * 2];
      var op = 0;
      for (var i = 0; i < frameSamples; ++i)
        for (var ch = 0; ch < channels; ++ch) {
          BinaryPrimitives.WriteInt16LittleEndian(outBlock.AsSpan(op), (short)ClipInt16(samples[ch][i]));
          op += 2;
        }
      pcm.Write(outBlock);

      remaining -= frameSamples;
    }

    return pcm.ToArray();
  }

  // ── Encode (lossless, verification path) ────────────────────────────────────

  /// <summary>
  /// Encodes raw interleaved little-endian 16-bit PCM to a complete Bonk file in
  /// lossless mode. Coefficients are sent as all-zero taps (so the lattice is a
  /// pass-through) and each packet's samples are coded through the canonical
  /// inverse of <see cref="ReadIntList"/>; the produced stream decodes back to the
  /// exact input. Intended for deterministic round-trip verification.
  /// </summary>
  public static byte[] Compress(ReadOnlySpan<byte> interleavedPcm, int channels, int sampleRate, int nTaps = 4, int samplesPerPacket = 256) {
    if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
    if (nTaps is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(nTaps));
    if (samplesPerPacket < 1) throw new ArgumentOutOfRangeException(nameof(samplesPerPacket));

    var frameBytes = channels * 2;
    if (interleavedPcm.Length % frameBytes != 0)
      throw new ArgumentException("PCM length is not a multiple of (channels × 2).");
    var samplesPerChannel = interleavedPcm.Length / frameBytes;
    var totalSamples = samplesPerChannel * channels;

    // De-interleave.
    var perChannel = new int[channels][];
    for (var c = 0; c < channels; ++c)
      perChannel[c] = new int[samplesPerChannel];
    for (var i = 0; i < samplesPerChannel; ++i)
      for (var c = 0; c < channels; ++c)
        perChannel[c][i] = BinaryPrimitives.ReadInt16LittleEndian(interleavedPcm.Slice((i * channels + c) * 2, 2));

    var writer = new BonkBitWriter();
    var zeroTaps = new int[nTaps];

    var done = 0;
    while (done < samplesPerChannel) {
      var thisLen = Math.Min(samplesPerPacket, samplesPerChannel - done);

      // Tap coefficients: all zero (lattice is a no-op so output == input).
      WriteIntList(writer, zeroTaps, nTaps, false);

      for (var c = 0; c < channels; ++c) {
        var seg = new int[samplesPerPacket];
        for (var i = 0; i < thisLen; ++i)
          seg[i] = perChannel[c][done + i];
        WriteIntList(writer, seg, samplesPerPacket, true);
      }

      done += thisLen;
    }

    var bitstream = writer.ToArray();

    using var file = new MemoryStream();
    file.WriteByte(0);
    file.Write("BONK"u8);
    Span<byte> h = stackalloc byte[HeaderBytes];
    h.Clear();
    h[0] = 0; // version
    BinaryPrimitives.WriteUInt32LittleEndian(h[1..], (uint)totalSamples);
    BinaryPrimitives.WriteUInt32LittleEndian(h[5..], (uint)sampleRate);
    h[9] = (byte)channels;
    h[10] = 1; // lossless
    h[11] = 0; // mid_side off
    BinaryPrimitives.WriteUInt16LittleEndian(h[12..], (ushort)nTaps);
    h[14] = 1; // downsampling
    BinaryPrimitives.WriteUInt16LittleEndian(h[15..], (ushort)samplesPerPacket);
    file.Write(h);
    file.Write(bitstream);
    return file.ToArray();
  }

  /// <summary>
  /// Canonical inverse of <see cref="ReadIntList"/>: emits the 4-bit low-bits width,
  /// the per-entry magnitude low bits, a pure dominant-zero run plane (so the
  /// bit-plane refinement adds nothing beyond the low bits), and the sign bits.
  /// </summary>
  private static void WriteIntList(BonkBitWriter wr, int[] values, int entries, bool base2Part) {
    // Choose a low-bits width that captures every magnitude exactly (max 15).
    var maxMag = 0;
    for (var i = 0; i < entries; ++i)
      maxMag = Math.Max(maxMag, Math.Abs(values[i]));
    var lowBits = 0;
    while ((1 << lowBits) <= maxMag) ++lowBits;
    if (lowBits > 15) throw new InvalidOperationException("Bonk encode magnitude exceeds 15-bit low-bits window.");

    if (base2Part) {
      wr.PutBits(4, (uint)lowBits);
      if (lowBits != 0)
        for (var i = 0; i < entries; ++i)
          wr.PutBits(lowBits, (uint)Math.Abs(values[i]));
    } else {
      // No base-2 part: magnitudes must already be representable as pure zero runs,
      // which only holds when every value is zero (the all-zero-taps case).
      for (var i = 0; i < entries; ++i)
        if (values[i] != 0)
          throw new InvalidOperationException("Bonk encode without base-2 part requires all-zero entries.");
    }

    // Emit dominant-zero runs until `entries` zeros are accounted for. Mirrors the
    // decoder's first loop with dominant fixed at 0 (step only grows, never inverts).
    var step = 256;
    var produced = 0;
    while (produced < entries) {
      var steplet = step >> 8;
      wr.PutBit(0);             // the "not a run-terminator" branch
      produced += steplet;      // contributes `steplet` zeros (dominant == 0)
      step += step / 8;
    }

    // Sign bits for non-zero magnitudes, in entry order.
    for (var i = 0; i < entries; ++i)
      if (values[i] != 0)
        wr.PutBit(values[i] < 0 ? 1 : 0);
  }

  // ── intlist coder ──────────────────────────────────────────────────────────

  private static uint ReadUIntMax(BonkBitReader gb, uint max) {
    uint value = 0;
    if (max == 0)
      return 0;
    for (uint i = 1; i <= max - value; i += i)
      if (gb.GetBit() != 0)
        value += i;
    return value;
  }

  private static void ReadIntList(BonkBitReader gb, int[] buf, int entries, bool base2Part, BitCount[] bits) {
    var lowBits = 0;
    var x = 0;
    var nZeros = 0;
    var step = 256;
    var dominant = 0;
    var pos = 0;
    var level = 0;
    var passes = 1;

    Array.Clear(buf, 0, entries);
    if (base2Part) {
      lowBits = (int)gb.GetBits(4);
      if (lowBits != 0)
        for (var i = 0; i < entries; ++i)
          buf[i] = (int)gb.GetBits(lowBits);
    }

    while (nZeros < entries) {
      var steplet = step >> 8;
      if (gb.BitsLeft <= 0)
        throw new InvalidDataException("Bonk intlist underflow.");

      if (gb.GetBit() == 0) {
        if (steplet > 0) {
          bits[x].Bit = dominant;
          bits[x++].Count = steplet;
        }
        if (dominant == 0)
          nZeros += steplet;
        step += step / 8;
      } else if (steplet > 0) {
        var actualRun = (int)ReadUIntMax(gb, (uint)(steplet - 1));
        if (actualRun > 0) {
          bits[x].Bit = dominant;
          bits[x++].Count = actualRun;
        }
        bits[x].Bit = dominant == 0 ? 1 : 0;
        bits[x++].Count = 1;

        if (dominant == 0)
          nZeros += actualRun;
        else
          ++nZeros;

        step -= step / 8;
      }

      if (step < 256) {
        step = 65536 / step;
        dominant = dominant == 0 ? 1 : 0;
      }
    }

    var maxX = x;
    x = 0;
    nZeros = 0;
    for (var i = 0; nZeros < entries; ++i) {
      if (x >= maxX)
        throw new InvalidDataException("Bonk intlist overrun.");

      if (pos >= entries) {
        pos = 0;
        level += passes << lowBits;
        passes = 1;
        if (bits[x].Bit != 0 && bits[x].Count > entries - nZeros)
          passes = bits[x].Count / (entries - nZeros);
      }

      if (level > 1 << 16)
        throw new InvalidDataException("Bonk intlist level overflow.");

      if (buf[pos] >= level) {
        if (bits[x].Bit != 0)
          buf[pos] += passes << lowBits;
        else
          ++nZeros;

        bits[x].Count -= passes;
        x += bits[x].Count == 0 ? 1 : 0;
      }

      ++pos;
    }

    for (var i = 0; i < entries; ++i)
      if (buf[i] != 0 && gb.GetBit() != 0)
        buf[i] = -buf[i];
  }

  // ── lattice predictor ──────────────────────────────────────────────────────

  private static void PredictorInitState(int[] k, int[] state, int order) {
    for (var i = order - 2; i >= 0; --i) {
      var xv = state[i];
      for (int j = 0, p = i + 1; p < order; ++j, ++p) {
        var tmp = xv + ShiftDown(k[j] * state[p], LatticeShift);
        state[p] += ShiftDown(k[j] * xv, LatticeShift);
        xv = tmp;
      }
    }
  }

  private static int PredictorCalcError(int[] k, int[] state, int order, int error) {
    var x = error - ShiftDown(k[order - 1] * state[order - 1], LatticeShift);
    for (var i = order - 2; i >= 0; --i) {
      var kValue = k[i];
      var stateValue = state[i];
      x -= ShiftDown(kValue * stateValue, LatticeShift);
      state[i + 1] = stateValue + ShiftDown(kValue * x, LatticeShift);
    }
    x = Math.Clamp(x, -(SampleFactor << 16), SampleFactor << 16);
    state[0] = x;
    return x;
  }
}
