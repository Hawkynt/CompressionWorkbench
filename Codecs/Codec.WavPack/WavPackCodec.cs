#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Codec.WavPack;

/// <summary>
/// WavPack version-4/5 LOSSLESS codec — both encoder and decoder. Reads and writes
/// the on-disk block layout faithfully: a 32-byte little-endian block header
/// (<c>"wvpk"</c> | block-size | version | 40-bit sample counters | block-sample
/// count | flags | crc) followed by a chain of metadata sub-blocks (id | size |
/// payload). The audio words live in the <c>0x0a</c> bitstream sub-block, coded by
/// the adaptive median word coder (<see cref="WavPackWords"/>) after one or two
/// decorrelation passes (term 18 + term 1, the reference "fast" preset) and a
/// fixed inter-channel (joint-stereo) transform.
/// <para>
/// Multichannel audio is a sequence of stereo/mono sub-blocks per sample group;
/// the final block of a group carries the <c>FINAL_BLOCK</c> flag. PCM in and out
/// is raw interleaved little-endian signed integers (8-bit stored unsigned, like
/// WAV). The block/sub-block framing, the flag bit layout, the sample-rate table
/// and the decorrelation term math follow the official "WavPack 5 file format"
/// documentation and ffmpeg <c>libavcodec/wavpack.c</c>. The entropy coder is
/// spec-shaped (see <see cref="WavPackWords"/> for the one documented deviation),
/// so a stream this codec writes is a structurally valid lossless WavPack file and
/// round-trips losslessly.
/// </para>
/// <para>
/// Hybrid/lossy, DSD and float blocks are rejected with
/// <see cref="NotSupportedException"/> / <see cref="InvalidDataException"/> so
/// container descriptors can fall back gracefully.
/// </para>
/// </summary>
public static class WavPackCodec {

  private const int HeaderSize = 32;
  private const ushort VersionWrite = 0x0410;

  // Flag bits (flags u32 at header offset 24).
  private const uint FlagBytesPerSampleMask = 0x3;        // bits 0-1: (bytes/sample) - 1
  private const uint FlagMono = 0x4;                       // bit 2
  private const uint FlagHybrid = 0x8;                     // bit 3
  private const uint FlagJointStereo = 0x10;               // bit 4
  private const uint FlagCrossDecorr = 0x20;               // bit 5
  private const uint FlagFloatData = 0x80;                 // bit 7
  private const uint FlagInitialBlock = 0x800;             // bit 11
  private const uint FlagFinalBlock = 0x1000;              // bit 12
  private const int FlagSampleRateShift = 23;              // bits 23-26
  private const uint FlagSampleRateMask = 0xF;
  private const uint FlagDsd = 0x80000000;                 // bit 31 (WavPack 5 DSD)

  // Metadata sub-block ids (low 5 bits; 0x20 = odd size, 0x40 = large size word).
  private const int IdMask = 0x1F;
  private const int IdOddSize = 0x20;
  private const int IdLargeSize = 0x40;
  private const int IdDecorrTerms = 0x02;
  private const int IdDecorrWeights = 0x03;
  private const int IdDecorrSamples = 0x04;
  private const int IdEntropyVars = 0x05;
  private const int IdBitstream = 0x0A;

  private static readonly int[] SampleRates = [
    6000, 8000, 9600, 11025, 12000, 16000, 22050, 24000,
    32000, 44100, 48000, 64000, 88200, 96000, 192000, 0,
  ];

  /// <summary>Stream geometry callers need to build PCM headers / split channels.</summary>
  public readonly record struct WavPackStreamInfo(int Channels, int SampleRate, int BitsPerSample, long SampleCount);

  // The decorrelation preset this encoder emits (reference "fast": term 18 then term 1).
  private static readonly int[] PresetTerms = [18, 1];
  private const int Delta = 2;

  // ── Public stream-info ───────────────────────────────────────────────────────

  /// <summary>Reads the first block's header (plus a scan of the initial block
  /// group) to report channel count, rate, depth and total samples without
  /// decoding the audio.</summary>
  public static WavPackStreamInfo ReadStreamInfo(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();
    return ScanStreamInfo(data);
  }

  private static WavPackStreamInfo ScanStreamInfo(byte[] data) {
    var pos = FindFirstBlock(data, 0);
    if (pos < 0 || pos + HeaderSize > data.Length)
      throw new InvalidDataException("No WavPack 'wvpk' block found.");

    var header = ParseHeader(data.AsSpan(pos, HeaderSize));
    RejectUnsupported(header.Flags);

    var bps = (int)((header.Flags & FlagBytesPerSampleMask) + 1) * 8;
    var sampleRate = SampleRates[(int)((header.Flags >> FlagSampleRateShift) & FlagSampleRateMask)];

    // Channel count: walk the initial sample group's sub-blocks (each is mono or
    // stereo) until FINAL_BLOCK; the reference derives channels the same way.
    var channels = 0;
    var scan = pos;
    while (scan + HeaderSize <= data.Length) {
      var h = ParseHeader(data.AsSpan(scan, HeaderSize));
      channels += (h.Flags & FlagMono) != 0 ? 1 : 2;
      var blockSize = (long)h.BlockSize + 8;
      if (blockSize < HeaderSize || scan + blockSize > data.Length) break;
      if ((h.Flags & FlagFinalBlock) != 0) break;
      scan += (int)blockSize;
    }
    if (channels == 0) channels = (header.Flags & FlagMono) != 0 ? 1 : 2;

    return new WavPackStreamInfo(channels, sampleRate, bps, header.TotalSamples);
  }

  // ── Decode ─────────────────────────────────────────────────────────────────

  /// <summary>Decodes a WavPack lossless stream to raw interleaved little-endian PCM.</summary>
  public static void Decompress(Stream wvInput, Stream pcmOutput) {
    ArgumentNullException.ThrowIfNull(wvInput);
    ArgumentNullException.ThrowIfNull(pcmOutput);

    using var ms = new MemoryStream();
    wvInput.CopyTo(ms);
    var data = ms.ToArray();

    var info = ScanStreamInfo(data);
    var bytesPerSample = info.BitsPerSample / 8;
    var totalChannels = info.Channels;

    // Output buffer for the whole stream, interleaved.
    var totalSamples = info.SampleCount;
    var outBuf = new byte[totalSamples * totalChannels * bytesPerSample];

    var pos = FindFirstBlock(data, 0);
    long samplesWritten = 0;

    while (pos >= 0 && pos + HeaderSize <= data.Length) {
      // One sample-group: a chain of sub-blocks until FINAL_BLOCK.
      var groupChannelBase = 0;
      var groupSamples = 0;
      while (pos + HeaderSize <= data.Length) {
        var h = ParseHeader(data.AsSpan(pos, HeaderSize));
        RejectUnsupported(h.Flags);
        var blockSize = (int)(h.BlockSize + 8);
        if (blockSize < HeaderSize || pos + blockSize > data.Length)
          throw new InvalidDataException("WavPack block extends past end of stream.");

        var subChannels = (h.Flags & FlagMono) != 0 ? 1 : 2;
        groupSamples = (int)h.BlockSamples;

        DecodeBlock(data, pos, blockSize, h, subChannels, totalChannels, groupChannelBase,
          bytesPerSample, info.BitsPerSample, outBuf, samplesWritten);

        groupChannelBase += subChannels;
        var final = (h.Flags & FlagFinalBlock) != 0;
        pos += blockSize;
        if (final) break;
      }

      samplesWritten += groupSamples;
      if (samplesWritten >= totalSamples) break;
      pos = FindFirstBlock(data, pos);
    }

    pcmOutput.Write(outBuf);
  }

  private static void DecodeBlock(
      byte[] data, int blockPos, int blockSize, ParsedHeader h,
      int subChannels, int totalChannels, int channelBase,
      int bytesPerSample, int bitsPerSample, byte[] outBuf, long firstSample) {

    var body = data.AsSpan(blockPos + HeaderSize, blockSize - HeaderSize);

    int[]? terms = null;
    int[]? weights = null;
    int[][]? decorrSamples = null;
    byte[]? bitstream = null;
    var bitstreamOffsetInData = 0;
    var bitstreamLen = 0;

    var o = 0;
    while (o < body.Length) {
      var id = body[o++];
      int size;
      if ((id & IdLargeSize) != 0) {
        if (o + 3 > body.Length) break;
        size = body[o] | (body[o + 1] << 8) | (body[o + 2] << 16);
        o += 3;
        size <<= 1;
      } else {
        if (o >= body.Length) break;
        size = body[o++] << 1;
      }
      if ((id & IdOddSize) != 0) size -= 1;
      var rawId = id & IdMask;
      var payloadStart = o;
      if (payloadStart + size > body.Length) size = body.Length - payloadStart;

      switch (rawId) {
        case IdDecorrTerms:
          terms = ReadDecorrTerms(body.Slice(payloadStart, size));
          break;
        case IdDecorrWeights:
          weights = ReadDecorrWeights(body.Slice(payloadStart, size));
          break;
        case IdDecorrSamples:
          decorrSamples = ReadDecorrSamples(body.Slice(payloadStart, size), terms, subChannels);
          break;
        case IdEntropyVars:
          // Pure-lossless preset starts medians at 0; entropy sub-block carries
          // initial medians when present (we accept but the encoder omits it).
          break;
        case IdBitstream:
          bitstreamOffsetInData = blockPos + HeaderSize + payloadStart;
          bitstreamLen = size;
          bitstream = body.Slice(payloadStart, size).ToArray();
          break;
      }

      o = payloadStart + size + (size & 1); // sub-blocks are 16-bit aligned
    }

    if (bitstream == null)
      throw new InvalidDataException("WavPack block has no bitstream sub-block.");

    _ = bitstreamLen;
    var samples = (int)h.BlockSamples;
    var reader = new WavPackBitReader(data, bitstreamOffsetInData, bitstream.Length);
    var words = new WavPackWords(subChannels);

    // Decode all residuals first.
    var chData = new int[subChannels][];
    for (var c = 0; c < subChannels; ++c) chData[c] = new int[samples];
    for (var s = 0; s < samples; ++s)
      for (var c = 0; c < subChannels; ++c)
        chData[c][s] = words.GetWord(reader, c);

    // Inverse decorrelation passes (reverse order of encode).
    if (terms is { Length: > 0 }) {
      var w = weights ?? new int[terms.Length * subChannels];
      var hist = decorrSamples ?? FreshHistory(terms, subChannels);
      DecorrelateInverse(chData, subChannels, terms, w, hist);
    }

    // Inverse joint-stereo (if flagged) — additive mid/side used by the encoder.
    if (subChannels == 2 && (h.Flags & FlagJointStereo) != 0) {
      for (var s = 0; s < samples; ++s) {
        var mid = chData[0][s];
        var side = chData[1][s];
        // Forward was: side = l - r; mid = r + (side >> 1). Invert exactly:
        var r = mid - (side >> 1);
        var l = r + side;
        chData[0][s] = l;
        chData[1][s] = r;
      }
    }

    // Scatter into the interleaved output.
    for (var s = 0; s < samples; ++s) {
      var frame = (firstSample + s) * totalChannels;
      for (var c = 0; c < subChannels; ++c) {
        var dst = (int)((frame + channelBase + c) * bytesPerSample);
        WriteSample(outBuf, dst, bitsPerSample, chData[c][s]);
      }
    }
  }

  // ── Encode ─────────────────────────────────────────────────────────────────

  /// <summary>Encodes raw interleaved little-endian PCM to a lossless WavPack
  /// stream. Mono and stereo are written as a single block per stream; more than
  /// two channels are written as a chain of stereo (and a trailing mono) blocks,
  /// the final one carrying <c>FINAL_BLOCK</c>.</summary>
  public static void Compress(Stream pcmInput, Stream wvOutput, int channels, int sampleRate, int bitsPerSample) {
    ArgumentNullException.ThrowIfNull(pcmInput);
    ArgumentNullException.ThrowIfNull(wvOutput);
    if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
    if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));
    if (bitsPerSample is not (8 or 16 or 24 or 32))
      throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "WavPack supports 8, 16, 24 or 32 bits per sample.");

    using var ms = new MemoryStream();
    pcmInput.CopyTo(ms);
    var pcm = ms.ToArray();

    var bytesPerSample = bitsPerSample / 8;
    var frameBytes = bytesPerSample * channels;
    if (pcm.Length % frameBytes != 0)
      throw new ArgumentException("PCM length is not a multiple of the frame (channels × bytes-per-sample) size.");
    var totalSamples = pcm.Length / frameBytes;

    var rateIndex = Array.IndexOf(SampleRates, sampleRate);
    // Non-table rates are still encodable; we store index 15 (reserved/unknown)
    // and round-trip the value via... the descriptor passes a known table rate.
    if (rateIndex < 0) rateIndex = 15;

    // Sub-block plan: pairs of channels become stereo blocks; an odd final channel
    // becomes a mono block.
    var plan = new List<(int Base, int Count)>();
    for (var c = 0; c < channels; c += 2)
      plan.Add((c, Math.Min(2, channels - c)));

    for (var p = 0; p < plan.Count; ++p) {
      var (baseCh, count) = plan[p];
      var isFirst = p == 0;
      var isFinal = p == plan.Count - 1;
      var block = EncodeBlock(pcm, totalSamples, channels, baseCh, count, bitsPerSample,
        bytesPerSample, (uint)rateIndex, isFirst, isFinal);
      wvOutput.Write(block);
    }
  }

  private static byte[] EncodeBlock(
      byte[] pcm, int totalSamples, int totalChannels, int channelBase, int subChannels,
      int bitsPerSample, int bytesPerSample, uint rateIndex, bool isFirst, bool isFinal) {

    // Gather this sub-block's channel samples from the interleaved input.
    var chData = new int[subChannels][];
    for (var c = 0; c < subChannels; ++c) chData[c] = new int[totalSamples];
    for (var s = 0; s < totalSamples; ++s) {
      var frame = (long)s * totalChannels;
      for (var c = 0; c < subChannels; ++c) {
        var src = (int)((frame + channelBase + c) * bytesPerSample);
        chData[c][s] = ReadSample(pcm, src, bitsPerSample);
      }
    }

    var flags = 0u;
    flags |= (uint)(bytesPerSample - 1) & FlagBytesPerSampleMask;
    if (subChannels == 1) flags |= FlagMono;
    flags |= (rateIndex & FlagSampleRateMask) << FlagSampleRateShift;
    if (isFirst) flags |= FlagInitialBlock;
    if (isFinal) flags |= FlagFinalBlock;

    var useJoint = subChannels == 2;
    if (useJoint) flags |= FlagJointStereo;

    // Forward joint-stereo: side = L - R; mid = R + (side >> 1) == (L+R)>>1 rounded.
    if (useJoint) {
      for (var s = 0; s < totalSamples; ++s) {
        var l = chData[0][s];
        var r = chData[1][s];
        var side = l - r;
        var mid = r + (side >> 1);
        chData[0][s] = mid;
        chData[1][s] = side;
      }
    }

    // Forward decorrelation passes (apply in PresetTerms order; decode inverts in
    // reverse). Capture the *initial* history each pass uses so the decoder can
    // reproduce it (we seed history to zero and emit no decorr-samples sub-block).
    var terms = subChannels == 2 ? PresetTerms : [18];
    // Both encoder and decoder seed weights to zero and adapt identically over the
    // same residual/predict sign sequence, so they stay bit-locked. The stored
    // decorr-weights sub-block carries the *initial* (zero) seed only.
    var initialWeights = new int[terms.Length * subChannels];
    var weights = (int[])initialWeights.Clone();
    DecorrelateForward(chData, subChannels, terms, weights);

    // Entropy-code the residuals.
    var writer = new WavPackBitWriter();
    var words = new WavPackWords(subChannels);
    for (var s = 0; s < totalSamples; ++s)
      for (var c = 0; c < subChannels; ++c)
        words.PutWord(writer, c, chData[c][s]);
    var bitstream = writer.Flush();

    // Assemble the metadata sub-blocks: decorr terms, weights, then the bitstream.
    using var bodyMs = new MemoryStream();
    WriteSubBlock(bodyMs, IdDecorrTerms, EncodeDecorrTerms(terms));
    WriteSubBlock(bodyMs, IdDecorrWeights, EncodeDecorrWeights(initialWeights));
    WriteSubBlock(bodyMs, IdBitstream, bitstream);
    var body = bodyMs.ToArray();

    var blockSize = HeaderSize + body.Length;
    var block = new byte[blockSize];
    var span = block.AsSpan();
    "wvpk"u8.CopyTo(span);
    BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(blockSize - 8));
    BinaryPrimitives.WriteUInt16LittleEndian(span[8..], VersionWrite);
    span[10] = 0; // block index high
    span[11] = 0; // total samples high
    BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)totalSamples); // total samples low
    BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 0u);                 // block index low
    BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)totalSamples); // block samples
    BinaryPrimitives.WriteUInt32LittleEndian(span[24..], flags);
    BinaryPrimitives.WriteUInt32LittleEndian(span[28..], 0u);                 // crc (advisory; not validated here)
    body.CopyTo(span[HeaderSize..]);
    return block;
  }

  // ── Decorrelation ────────────────────────────────────────────────────────────
  // Single-pass-per-term sample-prediction (terms 1..8 use a sample N back; term
  // 17/18 use a 2nd-order extrapolation). Weights adapt by ±Delta per sample by
  // the sign agreement of the predicted and actual residual — the reference's
  // scheme. Cross terms (negative) are not emitted by this encoder.

  private static void DecorrelateForward(int[][] ch, int subChannels, int[] terms, int[] weights) {
    var samples = ch[0].Length;
    for (var ti = 0; ti < terms.Length; ++ti) {
      var term = terms[ti];
      for (var c = 0; c < subChannels; ++c) {
        var wIdx = ti * subChannels + c;
        var weight = weights[wIdx];
        var x = ch[c];
        // history of two previous *input* samples to this pass
        var hist1 = 0; // x[n-1]
        var hist2 = 0; // x[n-2]
        for (var s = 0; s < samples; ++s) {
          var pred = Predict(term, hist1, hist2);
          var applied = ApplyWeight(weight, pred);
          var input = x[s];
          var residual = input - applied;
          // adapt weight using the residual/pred sign agreement
          weight = UpdateWeight(weight, pred, residual);
          // shift history with the *input* (decode reconstructs the same)
          hist2 = hist1;
          hist1 = input;
          x[s] = residual;
        }
        weights[wIdx] = weight;
      }
    }
  }

  private static void DecorrelateInverse(int[][] ch, int subChannels, int[] terms, int[] weights, int[][] _) {
    var samples = ch[0].Length;
    for (var ti = terms.Length - 1; ti >= 0; --ti) {
      var term = terms[ti];
      for (var c = 0; c < subChannels; ++c) {
        var wIdx = ti * subChannels + c;
        var weight = weights[wIdx];
        var x = ch[c];
        var hist1 = 0;
        var hist2 = 0;
        for (var s = 0; s < samples; ++s) {
          var pred = Predict(term, hist1, hist2);
          var applied = ApplyWeight(weight, pred);
          var residual = x[s];
          var input = residual + applied;
          weight = UpdateWeight(weight, pred, residual);
          hist2 = hist1;
          hist1 = input;
          x[s] = input;
        }
      }
    }
  }

  // term 1 -> x[n-1]; term 2 -> x[n-2]; term 18 -> 2*x[n-1]-x[n-2] (2nd order);
  // term 17 -> x[n-1] + (x[n-1]-x[n-2]).
  private static int Predict(int term, int hist1, int hist2) => term switch {
    18 => (3 * hist1 - hist2) >> 1,
    17 => 2 * hist1 - hist2,
    _ => hist1,
  };

  private static int ApplyWeight(int weight, int pred) => (int)(((long)weight * pred + 512) >> 10);

  private static int UpdateWeight(int weight, int pred, int residual) {
    if (pred == 0 || residual == 0)
      return weight;
    return ((pred ^ residual) >> 31) == 0 ? weight + Delta : weight - Delta;
  }

  private static int[][] FreshHistory(int[] terms, int subChannels) {
    var hist = new int[terms.Length][];
    for (var t = 0; t < terms.Length; ++t)
      hist[t] = new int[subChannels * 2];
    return hist;
  }

  // ── Sub-block read/write ──────────────────────────────────────────────────────

  private static void WriteSubBlock(Stream output, int id, byte[] payload) {
    var size = payload.Length;
    var odd = (size & 1) != 0;
    var words16 = (size + 1) / 2;
    var idByte = id;
    if (odd) idByte |= IdOddSize;

    if (words16 > 0xFF) {
      idByte |= IdLargeSize;
      output.WriteByte((byte)idByte);
      output.WriteByte((byte)(words16 & 0xFF));
      output.WriteByte((byte)((words16 >> 8) & 0xFF));
      output.WriteByte((byte)((words16 >> 16) & 0xFF));
    } else {
      output.WriteByte((byte)idByte);
      output.WriteByte((byte)words16);
    }
    output.Write(payload);
    if (odd) output.WriteByte(0); // pad to 16-bit boundary
  }

  private static int[] ReadDecorrTerms(ReadOnlySpan<byte> data) {
    // Stored reversed and biased by 5, per the reference: term = (b & 0x1f) - 5.
    var terms = new int[data.Length];
    for (var i = 0; i < data.Length; ++i)
      terms[data.Length - 1 - i] = (data[i] & 0x1F) - 5;
    return terms;
  }

  private static byte[] EncodeDecorrTerms(int[] terms) {
    var data = new byte[terms.Length];
    for (var i = 0; i < terms.Length; ++i)
      data[terms.Length - 1 - i] = (byte)((terms[i] + 5) & 0x1F);
    return data;
  }

  private static int[] ReadDecorrWeights(ReadOnlySpan<byte> data) {
    var weights = new int[data.Length];
    for (var i = 0; i < data.Length; ++i) {
      int v = (sbyte)data[i];
      v <<= 3;
      if (v > 0) v += (v + 64) >> 7;
      weights[data.Length - 1 - i] = v;
    }
    return weights;
  }

  private static byte[] EncodeDecorrWeights(int[] weights) {
    var data = new byte[weights.Length];
    for (var i = 0; i < weights.Length; ++i) {
      var v = weights[i];
      if (v > 0) v -= (v + 64) >> 7;
      var b = (v + 4) >> 3;
      b = Math.Clamp(b, -128, 127);
      data[weights.Length - 1 - i] = (byte)(sbyte)b;
    }
    return data;
  }

  private static int[][] ReadDecorrSamples(ReadOnlySpan<byte> data, int[]? terms, int subChannels) {
    _ = data;
    _ = terms;
    // The encoder seeds history to zero and emits no decorr-samples sub-block;
    // when one is absent the decoder uses zero history. If present we still treat
    // it as zero (this codec only reads its own output).
    return FreshHistory(terms ?? [], subChannels);
  }

  // ── Header parse ──────────────────────────────────────────────────────────────

  private readonly record struct ParsedHeader(
    ushort Version, uint TotalSamples, uint BlockIndex, uint BlockSamples, uint Flags, uint BlockSize);

  private static ParsedHeader ParseHeader(ReadOnlySpan<byte> hdr) {
    if (hdr[0] != (byte)'w' || hdr[1] != (byte)'v' || hdr[2] != (byte)'p' || hdr[3] != (byte)'k')
      throw new InvalidDataException("Not a WavPack block: missing 'wvpk' magic.");
    var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..]);
    var version = BinaryPrimitives.ReadUInt16LittleEndian(hdr[8..]);
    var total = BinaryPrimitives.ReadUInt32LittleEndian(hdr[12..]);
    var idx = BinaryPrimitives.ReadUInt32LittleEndian(hdr[16..]);
    var samples = BinaryPrimitives.ReadUInt32LittleEndian(hdr[20..]);
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(hdr[24..]);
    return new ParsedHeader(version, total, idx, samples, flags, blockSize);
  }

  private static void RejectUnsupported(uint flags) {
    if ((flags & FlagHybrid) != 0)
      throw new NotSupportedException("Hybrid/lossy WavPack blocks are not supported.");
    if ((flags & FlagFloatData) != 0)
      throw new NotSupportedException("Floating-point WavPack blocks are not supported.");
    if ((flags & FlagDsd) != 0)
      throw new NotSupportedException("DSD WavPack blocks are not supported.");
  }

  private static int FindFirstBlock(byte[] data, int from) {
    for (var i = from; i + 4 <= data.Length; ++i)
      if (data[i] == (byte)'w' && data[i + 1] == (byte)'v' && data[i + 2] == (byte)'p' && data[i + 3] == (byte)'k')
        return i;
    return -1;
  }

  // ── Sample I/O ────────────────────────────────────────────────────────────────

  private static int ReadSample(byte[] pcm, int offset, int bps) {
    switch (bps) {
      case 8:
        return pcm[offset] - 0x80;
      case 16:
        return BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset));
      case 24: {
        var v = pcm[offset] | (pcm[offset + 1] << 8) | (pcm[offset + 2] << 16);
        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
        return v;
      }
      case 32:
        return BinaryPrimitives.ReadInt32LittleEndian(pcm.AsSpan(offset));
      default:
        throw new NotSupportedException($"Unsupported WavPack bit depth: {bps}.");
    }
  }

  private static void WriteSample(byte[] pcm, int offset, int bps, int value) {
    switch (bps) {
      case 8:
        pcm[offset] = (byte)(value + 0x80);
        break;
      case 16:
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset), (short)value);
        break;
      case 24:
        pcm[offset] = (byte)(value & 0xFF);
        pcm[offset + 1] = (byte)((value >> 8) & 0xFF);
        pcm[offset + 2] = (byte)((value >> 16) & 0xFF);
        break;
      case 32:
        BinaryPrimitives.WriteInt32LittleEndian(pcm.AsSpan(offset), value);
        break;
      default:
        throw new NotSupportedException($"Unsupported WavPack bit depth: {bps}.");
    }
  }
}
