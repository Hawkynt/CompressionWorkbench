#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Codec.WavPack;

/// <summary>
/// WavPack version-4/5 LOSSLESS codec — both encoder and decoder. Reads and writes
/// the on-disk block layout faithfully: a 32-byte little-endian block header
/// (<c>"wvpk"</c> | block-size | version | 40-bit sample counters | block-sample
/// count | flags | crc) followed by a chain of metadata sub-blocks (id | size |
/// payload). The audio words live in the <c>0x0a</c> bitstream sub-block, coded by
/// the adaptive median word coder (<see cref="WavPackWords"/>) after the
/// decorrelation passes described by the <c>0x02</c>/<c>0x03</c>/<c>0x04</c>
/// sub-blocks and the inter-channel (joint-stereo) transform.
/// <para>
/// The entropy coder, the median adaptation, the truncated-binary tail, the
/// cross-word <c>holding_one</c>/<c>holding_zero</c> state machine and the
/// all-zero <c>zeros_acc</c> run coding are ported faithfully from the reference
/// <c>read_words.c</c>/<c>write_words.c</c>, so this decoder reads streams written
/// by reference WavPack encoders and the encoder writes a structurally
/// byte-compatible stream. On decode the reference decorrelation is honoured:
/// terms (0x02), restored weights (0x03, <c>restore_weight</c>), seeded samples
/// (0x04, <c>wp_exp2s</c> per term type incl. negative cross terms), and entropy
/// medians (0x05, <c>wp_exp2s</c>). Joint stereo, the magnitude/shift fields and
/// extended 32-bit integer handling follow <c>unpack.c</c>.
/// </para>
/// <para>
/// Hybrid/lossy, DSD and float blocks are rejected with
/// <see cref="NotSupportedException"/> / <see cref="InvalidDataException"/> so
/// container descriptors can fall back gracefully. They are still parsed past
/// safely (the block framing is honoured).
/// </para>
/// </summary>
public static class WavPackCodec {

  private const int HeaderSize = 32;
  private const ushort VersionWrite = 0x0410;

  // Flag bits (flags u32 at header offset 24), per the public wavpack.h.
  private const uint FlagBytesPerSampleMask = 0x3;        // bits 0-1: (bytes/sample) - 1
  private const uint FlagMono = 0x4;                       // bit 2  MONO_FLAG
  private const uint FlagHybrid = 0x8;                     // bit 3  HYBRID_FLAG
  private const uint FlagJointStereo = 0x10;               // bit 4  JOINT_STEREO
  private const uint FlagCrossDecorr = 0x20;               // bit 5  CROSS_DECORR
  private const uint FlagFloatData = 0x80;                 // bit 7  FLOAT_DATA
  private const uint FlagInt32Data = 0x100;                // bit 8  INT32_DATA
  private const int FlagShiftLsb = 13;                     // bits 13-17 SHIFT
  private const uint FlagShiftMask = 0x1F;
  private const int FlagMagLsb = 18;                       // bits 18-22 MAG (max magnitude)
  private const uint FlagMagMask = 0x1F;
  private const uint FlagInitialBlock = 0x800;             // bit 11
  private const uint FlagFinalBlock = 0x1000;              // bit 12
  // A block has to stay small enough for the reference decoder's block buffer.
  // Measured against wvunpack 5.9: a 130,352-byte block reads, a 146,556-byte one
  // is refused outright, so the ceiling sits at 128 KiB. Budget well under it and
  // assume the worst case, that a block does not compress at all.
  private const int MaxBlockPayloadBytes = 96 * 1024;
  private const int MinBlockSamples = 4096;

  private const int FlagMagnitudeShift = 18;               // bits 18-22
  private const uint FlagMagnitudeMask = 0x1F;
  private const int FlagSampleRateShift = 23;              // bits 23-26
  private const uint FlagSampleRateMask = 0xF;
  private const uint FlagFalseStereo = 0x40000000;         // bit 30 FALSE_STEREO
  private const uint FlagDsd = 0x80000000;                 // bit 31 (WavPack 5 DSD)

  // Metadata sub-block ids (low 5 bits; 0x20 = odd size, 0x40 = large size word).
  // Metadata sub-block id byte: the low six bits are the id (bit 5 marks it as
  // optional data a decoder may skip), bit 6 says the payload has an odd byte
  // count, and bit 7 says the size field is three words wide instead of one.
  private const int IdMask = 0x3F;
  private const int IdOddSize = 0x40;
  private const int IdLargeSize = 0x80;
  private const int IdDecorrTerms = 0x02;
  private const int IdDecorrWeights = 0x03;
  private const int IdDecorrSamples = 0x04;
  private const int IdEntropyVars = 0x05;
  private const int IdFloatInfo = 0x08;
  private const int IdInt32Info = 0x09;
  private const int IdBitstream = 0x0A;
  private const int IdChannelInfo = 0x0D;
  private const int IdWvxBitstream = 0x0C;

  private const int MaxTerm = 8;

  private static readonly int[] SampleRates = [
    6000, 8000, 9600, 11025, 12000, 16000, 22050, 24000,
    32000, 44100, 48000, 64000, 88200, 96000, 192000, 0,
  ];

  /// <summary>Stream geometry callers need to build PCM headers / split channels.
  /// <paramref name="IsFloat"/> is true for IEEE-754 32-bit float streams (the
  /// <c>FLOAT_DATA</c> flag), in which case the decoded PCM is raw little-endian
  /// 32-bit floats rather than signed integers.</summary>
  public readonly record struct WavPackStreamInfo(
    int Channels, int SampleRate, int BitsPerSample, long SampleCount, bool IsFloat = false);

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
    var isFloat = (header.Flags & FlagFloatData) != 0;
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

    return new WavPackStreamInfo(channels, sampleRate, bps, header.TotalSamples, isFloat);
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
    int[]? deltas = null;
    int[]? weightsA = null;
    int[]? weightsB = null;
    int[][]? samplesA = null;
    int[][]? samplesB = null;
    var bitstreamOffsetInData = 0;
    var bitstreamLen = 0;
    var haveBitstream = false;
    Int32Info int32Info = default;
    WavPackFloat.FloatInfo floatInfo = default;
    var haveFloatInfo = false;
    var wvxOffsetInData = 0;
    var wvxLen = 0;
    var haveWvx = false;

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
      var payload = body.Slice(payloadStart, size);

      switch (rawId) {
        case IdDecorrTerms:
          ReadDecorrTerms(payload, out terms, out deltas);
          break;
        case IdDecorrWeights:
          ReadDecorrWeights(payload, terms, subChannels, out weightsA, out weightsB);
          break;
        case IdDecorrSamples:
          ReadDecorrSamples(payload, terms, subChannels, out samplesA, out samplesB);
          break;
        case IdEntropyVars:
          // Decoded lazily once the word coder exists (needs the Entropy objects).
          break;
        case IdInt32Info:
          int32Info = ReadInt32Info(payload);
          break;
        case IdFloatInfo:
          floatInfo = WavPackFloat.ReadFloatInfo(payload);
          haveFloatInfo = true;
          break;
        case IdBitstream:
          bitstreamOffsetInData = blockPos + HeaderSize + payloadStart;
          bitstreamLen = size;
          haveBitstream = true;
          break;
        case IdWvxBitstream:
          wvxOffsetInData = blockPos + HeaderSize + payloadStart;
          wvxLen = size;
          haveWvx = true;
          break;
      }

      o = payloadStart + size + (size & 1); // sub-blocks are 16-bit aligned
    }

    if (!haveBitstream)
      throw new InvalidDataException("WavPack block has no bitstream sub-block.");

    var samples = (int)h.BlockSamples;
    var reader = new WavPackBitReader(data, bitstreamOffsetInData, bitstreamLen);
    var words = new WavPackWords(subChannels);

    // 0x05 entropy vars: seed the medians via wp_exp2s. When absent the reference
    // (init_words) leaves them at zero, which is exactly the CLEAR default.
    ApplyEntropyVars(body, words, subChannels);

    // Decode the whole block in one buffer pass (get_words_lossless).
    var chData = new int[subChannels][];
    for (var c = 0; c < subChannels; ++c) chData[c] = new int[samples];
    words.GetWordsLossless(reader, chData, samples);

    // Inverse decorrelation: apply the passes in their stored order (the array is
    // already reversed relative to packing, so iterate front-to-back like unpack.c).
    if (terms is { Length: > 0 }) {
      var dA = weightsA ?? new int[terms.Length];
      var dB = weightsB ?? new int[terms.Length];
      var sA = samplesA ?? FreshSamples(terms.Length);
      var sB = samplesB ?? FreshSamples(terms.Length);
      var dl = deltas ?? Filled(terms.Length, Delta);
      if (subChannels == 2)
        DecorrelateStereo(chData, terms, dl, dA, dB, sA, sB, samples);
      else
        DecorrelateMono(chData[0], terms, dl, dA, sA, samples);
    }

    // Inverse joint-stereo (reference: left += (right -= (left >> 1))).
    if (subChannels == 2 && (h.Flags & FlagJointStereo) != 0) {
      var l = chData[0];
      var r = chData[1];
      for (var s = 0; s < samples; ++s)
        l[s] += r[s] -= l[s] >> 1;
    }

    // Extended 32-bit / shifted-sample fix-up (reference fixup_samples, lossless path).
    ApplyIntFixup(chData, subChannels, samples, h.Flags, int32Info);

    // IEEE float restoration: the decoded integers are the "lossy" 24-bit values;
    // float_values rebuilds the original IEEE-754 bit patterns (using the wvx
    // extension stream when present). A float block with no FLOAT_INFO sub-block is
    // malformed and unsupported (so container probes fall back gracefully).
    if ((h.Flags & FlagFloatData) != 0) {
      if (!haveFloatInfo)
        throw new NotSupportedException("Floating-point WavPack block has no FLOAT_INFO sub-block.");

      // The wvx payload is a 4-byte little-endian CRC followed by the extension
      // bitstream (same LSB-first bit order as the main bitstream).
      WavPackBitReader? wvx = null;
      if (haveWvx && wvxLen > 4)
        wvx = new WavPackBitReader(data, wvxOffsetInData + 4, wvxLen - 4);

      // The reference consumes the extension bits in interleaved (L,R,L,R…) order,
      // so the channels are restored together over one interleaved value array.
      var interleaved = new int[samples * subChannels];
      for (var s = 0; s < samples; ++s)
        for (var c = 0; c < subChannels; ++c)
          interleaved[s * subChannels + c] = chData[c][s];

      WavPackFloat.FloatValues(interleaved, floatInfo, wvx, minShiftedZeros: 0, maxShiftedOnes: 0);

      for (var s = 0; s < samples; ++s)
        for (var c = 0; c < subChannels; ++c)
          chData[c][s] = interleaved[s * subChannels + c];
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
  public static void Compress(Stream pcmInput, Stream wvOutput, int channels, int sampleRate, int bitsPerSample, bool isFloat = false) {
    ArgumentNullException.ThrowIfNull(pcmInput);
    ArgumentNullException.ThrowIfNull(wvOutput);
    if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
    if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));
    if (bitsPerSample is not (8 or 16 or 24 or 32))
      throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "WavPack supports 8, 16, 24 or 32 bits per sample.");
    if (isFloat && bitsPerSample != 32)
      throw new ArgumentException("WavPack float data requires 32-bit samples.", nameof(bitsPerSample));

    using var ms = new MemoryStream();
    pcmInput.CopyTo(ms);
    var pcm = ms.ToArray();

    var bytesPerSample = bitsPerSample / 8;
    var frameBytes = bytesPerSample * channels;
    if (pcm.Length % frameBytes != 0)
      throw new ArgumentException("PCM length is not a multiple of the frame (channels × bytes-per-sample) size.");
    var totalSamples = pcm.Length / frameBytes;

    var rateIndex = Array.IndexOf(SampleRates, sampleRate);
    if (rateIndex < 0) rateIndex = 15;

    // Sub-block plan: pairs of channels become stereo blocks; an odd final channel
    // becomes a mono block.
    var plan = new List<(int Base, int Count)>();
    for (var c = 0; c < channels; c += 2)
      plan.Add((c, Math.Min(2, channels - c)));

    // Split the stream in time as well as across channels. The reference decoder
    // reads a block into a fixed buffer, so one block per stream stops being
    // readable as soon as the audio is a few seconds long — measured, the ceiling
    // is on the block's byte count rather than on its sample count.
    var blockSamples = ChooseBlockSamples(sampleRate, bytesPerSample, plan);

    for (var offset = 0; offset < totalSamples; offset += blockSamples) {
      var count = Math.Min(blockSamples, totalSamples - offset);
      for (var p = 0; p < plan.Count; ++p) {
        var (baseCh, subChannels) = plan[p];
        var isFirst = p == 0;
        var isFinal = p == plan.Count - 1;
        var block = EncodeBlock(pcm, totalSamples, offset, count, channels, baseCh, subChannels,
          bitsPerSample, bytesPerSample, (uint)rateIndex, isFirst, isFinal, isFloat);
        wvOutput.Write(block);
      }
    }

    // An empty stream still needs one block, or there is no header to read back.
    if (totalSamples == 0)
      for (var p = 0; p < plan.Count; ++p) {
        var (baseCh, subChannels) = plan[p];
        wvOutput.Write(EncodeBlock(pcm, 0, 0, 0, channels, baseCh, subChannels,
          bitsPerSample, bytesPerSample, (uint)rateIndex, p == 0, p == plan.Count - 1, isFloat));
      }
  }

  /// <summary>
  /// Samples per block: a quarter second, which is what the reference encoder
  /// emits at CD rates, held down further when a frame is wide enough that a
  /// quarter second of incompressible audio would overrun the reference's block
  /// buffer.
  /// </summary>
  private static int ChooseBlockSamples(
      int sampleRate, int bytesPerSample, List<(int Base, int Count)> plan) {
    var widestFrame = 0;
    foreach (var (_, count) in plan)
      widestFrame = Math.Max(widestFrame, count * bytesPerSample);

    var byteBudget = MaxBlockPayloadBytes / Math.Max(1, widestFrame);
    return Math.Max(1, Math.Min(Math.Max(sampleRate / 4, MinBlockSamples), byteBudget));
  }

  private static byte[] EncodeBlock(
      byte[] pcm, int streamSamples, int sampleOffset, int totalSamples,
      int totalChannels, int channelBase, int subChannels,
      int bitsPerSample, int bytesPerSample, uint rateIndex, bool isFirst, bool isFinal, bool isFloat) {

    // Gather this sub-block's channel samples from the interleaved input.
    var chData = new int[subChannels][];
    for (var c = 0; c < subChannels; ++c) chData[c] = new int[totalSamples];
    for (var s = 0; s < totalSamples; ++s) {
      var frame = (long)(sampleOffset + s) * totalChannels;
      for (var c = 0; c < subChannels; ++c) {
        var src = (int)((frame + channelBase + c) * bytesPerSample);
        chData[c][s] = ReadSample(pcm, src, bitsPerSample);
      }
    }

    // Float pre-scan (reference scan_float_data): reduce the raw IEEE-754 bit
    // patterns to the lossy signed integers the integer coder handles, capturing
    // the parameters and the originals needed to losslessly restore them. The scan
    // runs over the interleaved sub-block buffer, exactly as the reference does.
    WavPackFloat.FloatInfo floatInfo = default;
    int[]? floatOriginals = null;
    if (isFloat) {
      var interleaved = new int[totalSamples * subChannels];
      for (var s = 0; s < totalSamples; ++s)
        for (var c = 0; c < subChannels; ++c)
          interleaved[s * subChannels + c] = chData[c][s];

      floatOriginals = WavPackFloat.ScanFloatData(interleaved, out floatInfo);

      for (var s = 0; s < totalSamples; ++s)
        for (var c = 0; c < subChannels; ++c)
          chData[c][s] = interleaved[s * subChannels + c];

      if (!WavPackFloat.NeedsExtensionStream(floatInfo))
        floatOriginals = null; // integer-derived floats restore without an extension stream
    }

    var flags = 0u;
    flags |= (uint)(bytesPerSample - 1) & FlagBytesPerSampleMask;
    if (subChannels == 1) flags |= FlagMono;
    if (isFloat) flags |= FlagFloatData;
    flags |= (rateIndex & FlagSampleRateMask) << FlagSampleRateShift;
    if (isFirst) flags |= FlagInitialBlock;
    if (isFinal) flags |= FlagFinalBlock;

    // The block CRC covers the samples as the decoder will finally see them, so
    // it has to be taken before joint-stereo and decorrelation rewrite them.
    var crc = ComputeBlockCrc(chData, subChannels, totalSamples);

    // How many bits the largest magnitude in the block occupies. The reference
    // decoder sizes its work from this field, and a block that under-reports it
    // decodes to silence rather than to an error.
    flags |= (ComputeMagnitude(chData, subChannels, totalSamples) & FlagMagnitudeMask)
      << FlagMagnitudeShift;

    var useJoint = subChannels == 2;
    if (useJoint) flags |= FlagJointStereo;

    // Forward joint-stereo (reference pack.c: bptr[1] += ((bptr[0] -= bptr[1]) >> 1)).
    // Stores side = L - R in channel 0 and mid = R + (side >> 1) in channel 1, the
    // exact inverse of the decode un-do (left += (right -= (left >> 1))).
    if (useJoint)
      for (var s = 0; s < totalSamples; ++s)
        chData[1][s] += (chData[0][s] -= chData[1][s]) >> 1;

    // Forward decorrelation. We emit real decorr-weights and decorr-samples
    // sub-blocks: the weights/history are seeded to zero here, but a warm-up pass
    // primes them so the stored sub-blocks carry meaningful (non-trivial) state
    // that the decoder must honour to reconstruct the signal.
    var terms = subChannels == 2 ? PresetTerms : [18];
    var deltas = Filled(terms.Length, Delta);
    var weightsA = new int[terms.Length];
    var weightsB = new int[terms.Length];
    var samplesA = FreshSamples(terms.Length);
    var samplesB = FreshSamples(terms.Length);

    if (subChannels == 2)
      DecorrelateStereoForward(chData, terms, deltas, weightsA, weightsB, samplesA, samplesB, totalSamples);
    else
      DecorrelateMonoForward(chData[0], terms, deltas, weightsA, samplesA, totalSamples);

    // Entropy-code the residuals (buffer-at-once, matching send_words_lossless).
    var writer = new WavPackBitWriter();
    var words = new WavPackWords(subChannels);
    // The medians as they stand at the *start* of the block — what a decoder has
    // to seed its own coder with. Captured before the residuals move them.
    var entropyVars = EncodeEntropyVars(words, subChannels);
    words.SendWordsLossless(writer, chData, totalSamples);
    words.FlushFinal(writer);
    // The bitstream is a run of 16-bit words, so it always ends on an even byte
    // count. Letting it end odd sets the sub-block's odd-size flag, which the
    // reference decoder never expects there and refuses the file over.
    var bitstream = writer.FlushEven();

    // Assemble the metadata sub-blocks: decorr terms, weights, samples, then the
    // bitstream. The weights/samples sub-blocks here carry the *initial* (zero)
    // seed used at the start of the block, which the decoder applies verbatim.
    using var bodyMs = new MemoryStream();
    WriteSubBlock(bodyMs, IdDecorrTerms, EncodeDecorrTerms(terms, deltas));
    WriteSubBlock(bodyMs, IdDecorrWeights, EncodeDecorrWeights(subChannels, new int[terms.Length], new int[terms.Length]));
    WriteSubBlock(bodyMs, IdDecorrSamples, EncodeDecorrSamples(subChannels, terms, FreshSamples(terms.Length), FreshSamples(terms.Length)));
    // Our own reader treats a missing entropy-vars sub-block as "all medians
    // cleared", which is what they are here — but that leniency is ours alone,
    // and a decoder is entitled to reject a block that omits them.
    WriteSubBlock(bodyMs, IdEntropyVars, entropyVars);
    // Beyond stereo the channels are spread over several blocks, and nothing in
    // those blocks says how many there are in total or where each sits. The
    // initial block of the group has to carry that, or a decoder reads the first
    // block's two channels and makes nonsense of the rest.
    if (isFirst && totalChannels > 2)
      WriteSubBlock(bodyMs, IdChannelInfo, EncodeChannelInfo(totalChannels));
    if (isFloat)
      WriteSubBlock(bodyMs, IdFloatInfo, WavPackFloat.WriteFloatInfo(floatInfo));
    WriteSubBlock(bodyMs, IdBitstream, bitstream);

    // The wvx extension sub-block carries the lossless float refinement bits,
    // prefixed by a 32-bit CRC of the original f32 values (reference send_float_data).
    if (floatOriginals != null) {
      var wvxWriter = new WavPackBitWriter();
      WavPackFloat.SendFloatData(wvxWriter, floatOriginals, floatInfo);
      var wvxBits = wvxWriter.FlushEven();
      var wvxPayload = new byte[4 + wvxBits.Length];
      BinaryPrimitives.WriteUInt32LittleEndian(wvxPayload, WavPackFloat.ComputeCrc(floatOriginals));
      wvxBits.CopyTo(wvxPayload.AsSpan(4));
      WriteSubBlock(bodyMs, IdWvxBitstream, wvxPayload);
    }

    var body = bodyMs.ToArray();

    var blockSize = HeaderSize + body.Length;
    var block = new byte[blockSize];
    var span = block.AsSpan();
    "wvpk"u8.CopyTo(span);
    BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(blockSize - 8));
    BinaryPrimitives.WriteUInt16LittleEndian(span[8..], VersionWrite);
    span[10] = 0; // block index high
    span[11] = 0; // total samples high
    BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)streamSamples);  // total samples low
    BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)sampleOffset);   // block index low
    BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)totalSamples);   // block samples
    BinaryPrimitives.WriteUInt32LittleEndian(span[24..], flags);
    BinaryPrimitives.WriteUInt32LittleEndian(span[28..], crc);               // block check value
    body.CopyTo(span[HeaderSize..]);
    return block;
  }

  // ── Decorrelation (faithful to unpack.c / pack.c) ────────────────────────────
  // apply_weight(weight, sample) = (weight*sample + 512) >> 10 (64-bit safe).
  // update_weight(weight, delta, source, result): nudge ±delta on sign agreement.

  private static int ApplyWeight(int weight, int sample) => (int)(((long)weight * sample + 512) >> 10);

  private static int UpdateWeight(int weight, int delta, int source, int result) {
    if (source == 0 || result == 0)
      return weight;
    var s = (source ^ result) >> 31;
    return (delta ^ s) + (weight - s);
  }

  private static int UpdateWeightClip(int weight, int delta, int source, int result) {
    if (source == 0 || result == 0)
      return weight;
    var s = (source ^ result) >> 31;
    weight = (weight ^ s) + (delta - s);
    if (weight > 1024) weight = 1024;
    return (weight ^ s) - s;
  }

  // Inverse stereo decorrelation: reconstructs the channels in place. The samples
  // arrays carry the per-term history (samples_A/B), seeded from 0x04 or zero.
  private static void DecorrelateStereo(
      int[][] ch, int[] terms, int[] deltas, int[] wA, int[] wB,
      int[][] sA, int[][] sB, int sampleCount) {

    var left = ch[0];
    var right = ch[1];

    for (var ti = 0; ti < terms.Length; ++ti) {
      var term = terms[ti];
      var delta = deltas[ti];
      var weightA = wA[ti];
      var weightB = wB[ti];
      var saA = sA[ti];
      var saB = sB[ti];

      switch (term) {
        case 18:
          for (var i = 0; i < sampleCount; ++i) {
            var sam = saA[0] + ((saA[0] - saA[1]) >> 1);
            saA[1] = saA[0];
            var tmp = left[i];
            left[i] = saA[0] = ApplyWeight(weightA, sam) + tmp;
            weightA = UpdateWeight(weightA, delta, sam, tmp);

            sam = saB[0] + ((saB[0] - saB[1]) >> 1);
            saB[1] = saB[0];
            tmp = right[i];
            right[i] = saB[0] = ApplyWeight(weightB, sam) + tmp;
            weightB = UpdateWeight(weightB, delta, sam, tmp);
          }
          break;

        case 17:
          for (var i = 0; i < sampleCount; ++i) {
            var sam = 2 * saA[0] - saA[1];
            saA[1] = saA[0];
            var tmp = left[i];
            left[i] = saA[0] = ApplyWeight(weightA, sam) + tmp;
            weightA = UpdateWeight(weightA, delta, sam, tmp);

            sam = 2 * saB[0] - saB[1];
            saB[1] = saB[0];
            tmp = right[i];
            right[i] = saB[0] = ApplyWeight(weightB, sam) + tmp;
            weightB = UpdateWeight(weightB, delta, sam, tmp);
          }
          break;

        case -1:
          for (var i = 0; i < sampleCount; ++i) {
            var sam = left[i] + ApplyWeight(weightA, saA[0]);
            weightA = UpdateWeightClip(weightA, delta, saA[0], left[i]);
            left[i] = sam;
            saA[0] = right[i] + ApplyWeight(weightB, sam);
            weightB = UpdateWeightClip(weightB, delta, sam, right[i]);
            right[i] = saA[0];
          }
          break;

        case -2:
          for (var i = 0; i < sampleCount; ++i) {
            var sam = right[i] + ApplyWeight(weightB, saB[0]);
            weightB = UpdateWeightClip(weightB, delta, saB[0], right[i]);
            right[i] = sam;
            saB[0] = left[i] + ApplyWeight(weightA, sam);
            weightA = UpdateWeightClip(weightA, delta, sam, left[i]);
            left[i] = saB[0];
          }
          break;

        case -3:
          for (var i = 0; i < sampleCount; ++i) {
            var samA = left[i] + ApplyWeight(weightA, saA[0]);
            weightA = UpdateWeightClip(weightA, delta, saA[0], left[i]);
            var samB = right[i] + ApplyWeight(weightB, saB[0]);
            weightB = UpdateWeightClip(weightB, delta, saB[0], right[i]);
            left[i] = saB[0] = samA;
            right[i] = saA[0] = samB;
          }
          break;

        default: { // terms 1..8
          var m = 0;
          var k = term & (MaxTerm - 1);
          for (var i = 0; i < sampleCount; ++i) {
            var sam = saA[m];
            saA[k] = ApplyWeight(weightA, sam) + left[i];
            weightA = UpdateWeight(weightA, delta, sam, left[i]);
            left[i] = saA[k];

            sam = saB[m];
            saB[k] = ApplyWeight(weightB, sam) + right[i];
            weightB = UpdateWeight(weightB, delta, sam, right[i]);
            right[i] = saB[k];

            m = (m + 1) & (MaxTerm - 1);
            k = (k + 1) & (MaxTerm - 1);
          }
          break;
        }
      }

      wA[ti] = weightA;
      wB[ti] = weightB;
    }
  }

  private static void DecorrelateMono(
      int[] buffer, int[] terms, int[] deltas, int[] wA, int[][] sA, int sampleCount) {

    for (var ti = 0; ti < terms.Length; ++ti) {
      var term = terms[ti];
      var delta = deltas[ti];
      var weightA = wA[ti];
      var saA = sA[ti];

      switch (term) {
        case 18:
          for (var i = 0; i < sampleCount; ++i) {
            var sam = saA[0] + ((saA[0] - saA[1]) >> 1);
            saA[1] = saA[0];
            saA[0] = ApplyWeight(weightA, sam) + buffer[i];
            weightA = UpdateWeight(weightA, delta, sam, buffer[i]);
            buffer[i] = saA[0];
          }
          break;

        case 17:
          for (var i = 0; i < sampleCount; ++i) {
            var sam = 2 * saA[0] - saA[1];
            saA[1] = saA[0];
            saA[0] = ApplyWeight(weightA, sam) + buffer[i];
            weightA = UpdateWeight(weightA, delta, sam, buffer[i]);
            buffer[i] = saA[0];
          }
          break;

        default: { // terms 1..8
          var m = 0;
          var k = term & (MaxTerm - 1);
          for (var i = 0; i < sampleCount; ++i) {
            var sam = saA[m];
            saA[k] = ApplyWeight(weightA, sam) + buffer[i];
            weightA = UpdateWeight(weightA, delta, sam, buffer[i]);
            buffer[i] = saA[k];
            m = (m + 1) & (MaxTerm - 1);
            k = (k + 1) & (MaxTerm - 1);
          }
          break;
        }
      }

      wA[ti] = weightA;
    }
  }

  // Forward (encode) decorrelation, a faithful port of pack.c decorr_stereo_pass /
  // decorr_mono_pass. It is the exact bit-level inverse of the decode passes, so
  // applying the terms in the reverse of their stored order produces residuals the
  // decoder un-does perfectly. (Stored order is decode order; we iterate back.)
  private static void DecorrelateStereoForward(
      int[][] ch, int[] terms, int[] deltas, int[] wA, int[] wB,
      int[][] sA, int[][] sB, int sampleCount) {

    var left = ch[0];
    var right = ch[1];

    for (var ti = terms.Length - 1; ti >= 0; --ti) {
      var term = terms[ti];
      var delta = deltas[ti];
      var weightA = wA[ti];
      var weightB = wB[ti];
      var saA = sA[ti];
      var saB = sB[ti];

      switch (term) {
        case 18:
          for (var i = 0; i < sampleCount; ++i) {
            var sam = saA[0] + ((saA[0] - saA[1]) >> 1);
            saA[1] = saA[0];
            var tmp = (saA[0] = left[i]) - ApplyWeight(weightA, sam);
            left[i] = tmp;
            weightA = UpdateWeight(weightA, delta, sam, tmp);

            sam = saB[0] + ((saB[0] - saB[1]) >> 1);
            saB[1] = saB[0];
            tmp = (saB[0] = right[i]) - ApplyWeight(weightB, sam);
            right[i] = tmp;
            weightB = UpdateWeight(weightB, delta, sam, tmp);
          }
          break;

        case 17:
          for (var i = 0; i < sampleCount; ++i) {
            var sam = 2 * saA[0] - saA[1];
            saA[1] = saA[0];
            var tmp = (saA[0] = left[i]) - ApplyWeight(weightA, sam);
            left[i] = tmp;
            weightA = UpdateWeight(weightA, delta, sam, tmp);

            sam = 2 * saB[0] - saB[1];
            saB[1] = saB[0];
            tmp = (saB[0] = right[i]) - ApplyWeight(weightB, sam);
            right[i] = tmp;
            weightB = UpdateWeight(weightB, delta, sam, tmp);
          }
          break;

        default: { // terms 1..8 (negative cross terms are not emitted by this encoder)
          var m = 0;
          var k = term & (MaxTerm - 1);
          for (var i = 0; i < sampleCount; ++i) {
            var sam = saA[m];
            var tmp = (saA[k] = left[i]) - ApplyWeight(weightA, sam);
            left[i] = tmp;
            weightA = UpdateWeight(weightA, delta, sam, tmp);

            sam = saB[m];
            tmp = (saB[k] = right[i]) - ApplyWeight(weightB, sam);
            right[i] = tmp;
            weightB = UpdateWeight(weightB, delta, sam, tmp);

            m = (m + 1) & (MaxTerm - 1);
            k = (k + 1) & (MaxTerm - 1);
          }
          break;
        }
      }

      wA[ti] = weightA;
      wB[ti] = weightB;
    }
  }

  private static void DecorrelateMonoForward(
      int[] buffer, int[] terms, int[] deltas, int[] wA, int[][] sA, int sampleCount) {

    for (var ti = terms.Length - 1; ti >= 0; --ti) {
      var term = terms[ti];
      var delta = deltas[ti];
      var weightA = wA[ti];
      var saA = sA[ti];

      switch (term) {
        case 18:
          for (var i = 0; i < sampleCount; ++i) {
            var sam = saA[0] + ((saA[0] - saA[1]) >> 1);
            saA[1] = saA[0];
            var tmp = (saA[0] = buffer[i]) - ApplyWeight(weightA, sam);
            buffer[i] = tmp;
            weightA = UpdateWeight(weightA, delta, sam, tmp);
          }
          break;

        case 17:
          for (var i = 0; i < sampleCount; ++i) {
            var sam = 2 * saA[0] - saA[1];
            saA[1] = saA[0];
            var tmp = (saA[0] = buffer[i]) - ApplyWeight(weightA, sam);
            buffer[i] = tmp;
            weightA = UpdateWeight(weightA, delta, sam, tmp);
          }
          break;

        default: { // terms 1..8
          var m = 0;
          var k = term & (MaxTerm - 1);
          for (var i = 0; i < sampleCount; ++i) {
            var sam = saA[m];
            var tmp = (saA[k] = buffer[i]) - ApplyWeight(weightA, sam);
            buffer[i] = tmp;
            weightA = UpdateWeight(weightA, delta, sam, tmp);
            m = (m + 1) & (MaxTerm - 1);
            k = (k + 1) & (MaxTerm - 1);
          }
          break;
        }
      }

      wA[ti] = weightA;
    }
  }

  // ── 0x05 entropy vars (wp_exp2s of stored medians) ───────────────────────────

  /// <summary>
  /// The three magnitude medians per channel, log-encoded as 16-bit values —
  /// the inverse of what <see cref="ApplyEntropyVars" /> reads back.
  /// </summary>
  private static byte[] EncodeEntropyVars(WavPackWords words, int subChannels) {
    var payload = new byte[subChannels == 1 ? 6 : 12];
    WriteMedians(payload, 0, words.Channel(0).Median);
    if (subChannels == 2)
      WriteMedians(payload, 6, words.Channel(1).Median);
    return payload;

    static void WriteMedians(byte[] payload, int offset, uint[] median) {
      for (var i = 0; i < 3; ++i) {
        var log = WpLog2S((int)median[i]);
        payload[offset + i * 2] = (byte)(log & 0xFF);
        payload[offset + i * 2 + 1] = (byte)((log >> 8) & 0xFF);
      }
    }
  }

  private static void ApplyEntropyVars(ReadOnlySpan<byte> body, WavPackWords words, int subChannels) {
    // Re-walk the sub-blocks to find 0x05 and seed the medians. Absent => zero.
    var o = 0;
    while (o < body.Length) {
      var id = body[o++];
      int size;
      if ((id & IdLargeSize) != 0) {
        if (o + 3 > body.Length) break;
        size = (body[o] | (body[o + 1] << 8) | (body[o + 2] << 16)) << 1;
        o += 3;
      } else {
        if (o >= body.Length) break;
        size = body[o++] << 1;
      }
      if ((id & IdOddSize) != 0) size -= 1;
      var payloadStart = o;
      if (payloadStart + size > body.Length) size = body.Length - payloadStart;

      if ((id & IdMask) == IdEntropyVars) {
        var p = body.Slice(payloadStart, size);
        var expected = subChannels == 1 ? 6 : 12;
        if (p.Length >= expected) {
          var c0 = words.Channel(0).Median;
          c0[0] = (uint)WpExp2S(p[0] | (p[1] << 8));
          c0[1] = (uint)WpExp2S(p[2] | (p[3] << 8));
          c0[2] = (uint)WpExp2S(p[4] | (p[5] << 8));
          if (subChannels == 2) {
            var c1 = words.Channel(1).Median;
            c1[0] = (uint)WpExp2S(p[6] | (p[7] << 8));
            c1[1] = (uint)WpExp2S(p[8] | (p[9] << 8));
            c1[2] = (uint)WpExp2S(p[10] | (p[11] << 8));
          }
        }
        return;
      }

      o = payloadStart + size + (size & 1);
    }
  }

  // ── 0x09 int32 info + extended/shift fix-up (unpack.c fixup_samples) ─────────

  private readonly record struct Int32Info(int SentBits, int Zeros, int Ones, int Dups);

  private static Int32Info ReadInt32Info(ReadOnlySpan<byte> p) =>
    p.Length < 4 ? default : new Int32Info(p[0] & 0x1F, p[1] & 0x1F, p[2] & 0x1F, p[3] & 0x1F);

  private static void ApplyIntFixup(int[][] ch, int subChannels, int samples, uint flags, Int32Info info) {
    var shift = (int)((flags >> FlagShiftLsb) & FlagShiftMask);

    if ((flags & FlagInt32Data) != 0) {
      var sentBits = info.SentBits;
      var zeros = info.Zeros;
      var ones = info.Ones;
      var dups = info.Dups;

      // Lossless path with no wvx correction bits: the bit groups fold into a
      // single left shift (reference else-branch: shift += zeros+sent+ones+dups).
      if (sentBits == 0 && zeros + ones + dups != 0) {
        for (var c = 0; c < subChannels; ++c) {
          var x = ch[c];
          for (var i = 0; i < samples; ++i) {
            if (zeros != 0)
              x[i] = (int)((uint)x[i] << zeros);
            else if (ones != 0)
              x[i] = (int)(((uint)(x[i] + 1) << ones) - 1);
            else if (dups != 0)
              x[i] = (int)(((uint)(x[i] + (x[i] & 1)) << dups) - (uint)(x[i] & 1));
          }
        }
      } else {
        shift += zeros + sentBits + ones + dups;
      }
    }

    shift &= 0x1F;
    if (shift == 0)
      return;

    for (var c = 0; c < subChannels; ++c) {
      var x = ch[c];
      for (var i = 0; i < samples; ++i)
        x[i] = (int)((uint)x[i] << shift);
    }
  }

  // ── wp_log2 / wp_exp2s (entropy_utils.c) ─────────────────────────────────────

  private static readonly byte[] Log2Table = BuildLog2Table();
  private static readonly byte[] Exp2Table = BuildExp2Table();
  private static readonly byte[] NbitsTable = BuildNbitsTable();

  /// <summary>Reference <c>wp_log2</c>: base-2 logarithm of a 32-bit unsigned value
  /// with 8 bits of fractional precision (max input ≈ 0xff800000 → 8447).</summary>
  internal static int WpLog2(uint avalue) {
    avalue += avalue >> 9;
    int dbits;
    if (avalue < (1 << 8)) {
      dbits = NbitsTable[avalue];
      return (dbits << 8) + Log2Table[(avalue << (9 - dbits)) & 0xFF];
    }

    if (avalue < (1u << 16))
      dbits = NbitsTable[avalue >> 8] + 8;
    else if (avalue < (1u << 24))
      dbits = NbitsTable[avalue >> 16] + 16;
    else
      dbits = NbitsTable[avalue >> 24] + 24;

    return (dbits << 8) + Log2Table[(avalue >> (dbits - 9)) & 0xFF];
  }

  /// <summary>Reference <c>wp_log2s</c>: signed base-2 logarithm.</summary>
  internal static int WpLog2S(int value) => value < 0 ? -WpLog2((uint)-value) : WpLog2((uint)value);

  /// <summary>Reference <c>wp_exp2s</c>: inverts <see cref="WpLog2"/> (input range
  /// -8192..+8447). Returns a full 32-bit value so it doubles as the unsigned
  /// median reconstruction.</summary>
  internal static int WpExp2S(int log) {
    if (log < 0)
      return ~(int)((uint)WpExp2S(-log) - 1);

    var value = (uint)Exp2Table[log & 0xFF] | 0x100u;
    log >>= 8;
    return log <= 9 ? (int)(value >> (9 - log)) : (int)(value << ((log - 9) & 0x1F));
  }

  // ── store_weight / restore_weight (entropy_utils.c) ──────────────────────────

  private static int RestoreWeight(sbyte weight) {
    var result = weight * 8;
    if (result > 0)
      result += (result + 64) >> 7;
    return result;
  }

  private static sbyte StoreWeight(int weight) {
    if (weight > 1024) weight = 1024;
    else if (weight < -1024) weight = -1024;
    if (weight > 0) weight -= (weight + 64) >> 7;
    return (sbyte)((weight + 4) >> 3);
  }

  // ── Sub-block read/write ──────────────────────────────────────────────────────

  /// <summary>
  /// The per-block check value: <c>crc = crc * 3 + sample</c> over the samples
  /// in interleaved order, seeded all-ones and wrapping at 32 bits.
  /// </summary>
  private static uint ComputeBlockCrc(int[][] channels, int subChannels, int totalSamples) {
    var crc = 0xFFFFFFFFu;
    for (var s = 0; s < totalSamples; ++s)
      for (var c = 0; c < subChannels; ++c)
        crc = unchecked(crc * 3 + (uint)channels[c][s]);
    return crc;
  }

  /// <summary>
  /// Bit length of the largest sample magnitude in the block. A negative sample
  /// contributes <c>~sample</c>, so -1 counts as zero and the range stays
  /// symmetric.
  /// </summary>
  private static uint ComputeMagnitude(int[][] channels, int subChannels, int totalSamples) {
    var largest = 0;
    for (var c = 0; c < subChannels; ++c) {
      var samples = channels[c];
      for (var s = 0; s < totalSamples; ++s) {
        var magnitude = samples[s];
        if (magnitude < 0) magnitude = ~magnitude;
        if (magnitude > largest) largest = magnitude;
      }
    }

    var bits = 0u;
    while (largest != 0) {
      ++bits;
      largest >>= 1;
    }

    return bits;
  }

  /// <summary>
  /// Channel count followed by the Microsoft channel mask, in as few bytes as
  /// the mask needs.
  /// </summary>
  private static byte[] EncodeChannelInfo(int channels) {
    var mask = DefaultChannelMask(channels);
    using var ms = new MemoryStream();
    ms.WriteByte((byte)channels);
    while (mask != 0) {
      ms.WriteByte((byte)(mask & 0xFF));
      mask >>= 8;
    }

    return ms.ToArray();
  }

  /// <summary>
  /// The conventional speaker layout for a channel count, used when the caller
  /// gives us a count and nothing more.
  /// </summary>
  private static uint DefaultChannelMask(int channels) => channels switch {
    1 => 0x004,  // front centre
    2 => 0x003,  // front left + right
    3 => 0x007,
    4 => 0x033,
    5 => 0x037,
    6 => 0x03F,  // 5.1
    7 => 0x13F,  // 6.1
    8 => 0x63F,  // 7.1
    _ => channels >= 32 ? 0xFFFFFFFF : (1u << channels) - 1,
  };

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

  // Terms+deltas are stored reversed: byte = ((term + 5) & 0x1f) | (delta << 5).
  private static void ReadDecorrTerms(ReadOnlySpan<byte> data, out int[] terms, out int[] deltas) {
    terms = new int[data.Length];
    deltas = new int[data.Length];
    for (var i = 0; i < data.Length; ++i) {
      terms[data.Length - 1 - i] = (data[i] & 0x1F) - 5;
      deltas[data.Length - 1 - i] = (data[i] >> 5) & 0x7;
    }
  }

  private static byte[] EncodeDecorrTerms(int[] terms, int[] deltas) {
    var data = new byte[terms.Length];
    for (var i = 0; i < terms.Length; ++i)
      data[terms.Length - 1 - i] = (byte)(((terms[i] + 5) & 0x1F) | ((deltas[i] & 0x7) << 5));
    return data;
  }

  // Weights are stored from the "last" term (front during decode) as signed chars.
  private static void ReadDecorrWeights(
      ReadOnlySpan<byte> data, int[]? terms, int subChannels,
      out int[] weightsA, out int[] weightsB) {

    var numTerms = terms?.Length ?? 0;
    weightsA = new int[numTerms];
    weightsB = new int[numTerms];
    if (numTerms == 0) return;

    var termcnt = subChannels == 2 ? data.Length / 2 : data.Length;
    if (termcnt > numTerms) termcnt = numTerms;

    var bp = 0;
    // The reference fills from the *last* decorr pass backwards.
    for (var t = numTerms - 1; t >= 0 && termcnt > 0; --t, --termcnt) {
      weightsA[t] = RestoreWeight((sbyte)data[bp++]);
      if (subChannels == 2 && bp < data.Length)
        weightsB[t] = RestoreWeight((sbyte)data[bp++]);
    }
  }

  private static byte[] EncodeDecorrWeights(int subChannels, int[] weightsA, int[] weightsB) {
    using var ms = new MemoryStream();
    for (var t = weightsA.Length - 1; t >= 0; --t) {
      ms.WriteByte((byte)StoreWeight(weightsA[t]));
      if (subChannels == 2)
        ms.WriteByte((byte)StoreWeight(weightsB[t]));
    }
    return ms.ToArray();
  }

  // Samples are stored from the "last" term backwards; count varies per term type.
  private static void ReadDecorrSamples(
      ReadOnlySpan<byte> data, int[]? terms, int subChannels,
      out int[][] samplesA, out int[][] samplesB) {

    var numTerms = terms?.Length ?? 0;
    samplesA = FreshSamples(numTerms);
    samplesB = FreshSamples(numTerms);
    if (numTerms == 0 || terms == null) return;

    var bp = 0;
    for (var t = numTerms - 1; t >= 0 && bp < data.Length; --t) {
      var term = terms[t];
      if (term > MaxTerm) {
        if (bp + (subChannels == 1 ? 4 : 8) > data.Length) break;
        samplesA[t][0] = WpExp2S((short)(data[bp] | (data[bp + 1] << 8)));
        samplesA[t][1] = WpExp2S((short)(data[bp + 2] | (data[bp + 3] << 8)));
        bp += 4;
        if (subChannels == 2) {
          samplesB[t][0] = WpExp2S((short)(data[bp] | (data[bp + 1] << 8)));
          samplesB[t][1] = WpExp2S((short)(data[bp + 2] | (data[bp + 3] << 8)));
          bp += 4;
        }
      } else if (term < 0) {
        if (bp + 4 > data.Length) break;
        samplesA[t][0] = WpExp2S((short)(data[bp] | (data[bp + 1] << 8)));
        samplesB[t][0] = WpExp2S((short)(data[bp + 2] | (data[bp + 3] << 8)));
        bp += 4;
      } else {
        for (var m = 0; m < term; ++m) {
          if (bp + (subChannels == 1 ? 2 : 4) > data.Length) break;
          samplesA[t][m] = WpExp2S((short)(data[bp] | (data[bp + 1] << 8)));
          bp += 2;
          if (subChannels == 2) {
            samplesB[t][m] = WpExp2S((short)(data[bp] | (data[bp + 1] << 8)));
            bp += 2;
          }
        }
      }
    }
  }

  private static byte[] EncodeDecorrSamples(int subChannels, int[] terms, int[][] samplesA, int[][] samplesB) {
    using var ms = new MemoryStream();
    void Put16(int v) {
      var e = (short)WpLog2S(v); // stored as wp_log2s
      ms.WriteByte((byte)(e & 0xFF));
      ms.WriteByte((byte)((e >> 8) & 0xFF));
    }

    for (var t = terms.Length - 1; t >= 0; --t) {
      var term = terms[t];
      if (term > MaxTerm) {
        Put16(samplesA[t][0]);
        Put16(samplesA[t][1]);
        if (subChannels == 2) {
          Put16(samplesB[t][0]);
          Put16(samplesB[t][1]);
        }
      } else if (term < 0) {
        Put16(samplesA[t][0]);
        Put16(samplesB[t][0]);
      } else {
        for (var m = 0; m < term; ++m) {
          Put16(samplesA[t][m]);
          if (subChannels == 2)
            Put16(samplesB[t][m]);
        }
      }
    }
    return ms.ToArray();
  }

  private static int[][] FreshSamples(int numTerms) {
    var s = new int[numTerms][];
    for (var t = 0; t < numTerms; ++t)
      s[t] = new int[MaxTerm];
    return s;
  }

  private static int[] Filled(int n, int value) {
    var a = new int[n];
    Array.Fill(a, value);
    return a;
  }

  // ── log/exp tables (verbatim from entropy_utils.c) ───────────────────────────

  private static byte[] BuildNbitsTable() {
    var t = new byte[256];
    for (var i = 1; i < 256; ++i) {
      var n = 0;
      var v = i;
      while (v != 0) { ++n; v >>= 1; }
      t[i] = (byte)n;
    }
    return t;
  }

  private static byte[] BuildLog2Table() => [
    0x00, 0x01, 0x03, 0x04, 0x06, 0x07, 0x09, 0x0a, 0x0b, 0x0d, 0x0e, 0x10, 0x11, 0x12, 0x14, 0x15,
    0x16, 0x18, 0x19, 0x1a, 0x1c, 0x1d, 0x1e, 0x20, 0x21, 0x22, 0x24, 0x25, 0x26, 0x28, 0x29, 0x2a,
    0x2c, 0x2d, 0x2e, 0x2f, 0x31, 0x32, 0x33, 0x34, 0x36, 0x37, 0x38, 0x39, 0x3b, 0x3c, 0x3d, 0x3e,
    0x3f, 0x41, 0x42, 0x43, 0x44, 0x45, 0x47, 0x48, 0x49, 0x4a, 0x4b, 0x4d, 0x4e, 0x4f, 0x50, 0x51,
    0x52, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x5c, 0x5d, 0x5e, 0x5f, 0x60, 0x61, 0x62, 0x63,
    0x64, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x6b, 0x6c, 0x6d, 0x6e, 0x6f, 0x70, 0x71, 0x72, 0x74, 0x75,
    0x76, 0x77, 0x78, 0x79, 0x7a, 0x7b, 0x7c, 0x7d, 0x7e, 0x7f, 0x80, 0x81, 0x82, 0x83, 0x84, 0x85,
    0x86, 0x87, 0x88, 0x89, 0x8a, 0x8b, 0x8c, 0x8d, 0x8e, 0x8f, 0x90, 0x91, 0x92, 0x93, 0x94, 0x95,
    0x96, 0x97, 0x98, 0x99, 0x9a, 0x9b, 0x9b, 0x9c, 0x9d, 0x9e, 0x9f, 0xa0, 0xa1, 0xa2, 0xa3, 0xa4,
    0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xa9, 0xaa, 0xab, 0xac, 0xad, 0xae, 0xaf, 0xb0, 0xb1, 0xb2, 0xb2,
    0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xb9, 0xba, 0xbb, 0xbc, 0xbd, 0xbe, 0xbf, 0xc0, 0xc0,
    0xc1, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xcb, 0xcb, 0xcc, 0xcd, 0xce,
    0xcf, 0xd0, 0xd0, 0xd1, 0xd2, 0xd3, 0xd4, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd8, 0xd9, 0xda, 0xdb,
    0xdc, 0xdc, 0xdd, 0xde, 0xdf, 0xe0, 0xe0, 0xe1, 0xe2, 0xe3, 0xe4, 0xe4, 0xe5, 0xe6, 0xe7, 0xe7,
    0xe8, 0xe9, 0xea, 0xea, 0xeb, 0xec, 0xed, 0xee, 0xee, 0xef, 0xf0, 0xf1, 0xf1, 0xf2, 0xf3, 0xf4,
    0xf4, 0xf5, 0xf6, 0xf7, 0xf7, 0xf8, 0xf9, 0xf9, 0xfa, 0xfb, 0xfc, 0xfc, 0xfd, 0xfe, 0xff, 0xff,
  ];

  private static byte[] BuildExp2Table() => [
    0x00, 0x01, 0x01, 0x02, 0x03, 0x03, 0x04, 0x05, 0x06, 0x06, 0x07, 0x08, 0x08, 0x09, 0x0a, 0x0b,
    0x0b, 0x0c, 0x0d, 0x0e, 0x0e, 0x0f, 0x10, 0x10, 0x11, 0x12, 0x13, 0x13, 0x14, 0x15, 0x16, 0x16,
    0x17, 0x18, 0x19, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1d, 0x1e, 0x1f, 0x20, 0x20, 0x21, 0x22, 0x23,
    0x24, 0x24, 0x25, 0x26, 0x27, 0x28, 0x28, 0x29, 0x2a, 0x2b, 0x2c, 0x2c, 0x2d, 0x2e, 0x2f, 0x30,
    0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x3a, 0x3b, 0x3c, 0x3d,
    0x3e, 0x3f, 0x40, 0x41, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x48, 0x49, 0x4a, 0x4b,
    0x4c, 0x4d, 0x4e, 0x4f, 0x50, 0x51, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a,
    0x5b, 0x5c, 0x5d, 0x5e, 0x5e, 0x5f, 0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
    0x6a, 0x6b, 0x6c, 0x6d, 0x6e, 0x6f, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79,
    0x7a, 0x7b, 0x7c, 0x7d, 0x7e, 0x7f, 0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x87, 0x88, 0x89, 0x8a,
    0x8b, 0x8c, 0x8d, 0x8e, 0x8f, 0x90, 0x91, 0x92, 0x93, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0x9b,
    0x9c, 0x9d, 0x9f, 0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa8, 0xa9, 0xaa, 0xab, 0xac, 0xad,
    0xaf, 0xb0, 0xb1, 0xb2, 0xb3, 0xb4, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xbc, 0xbd, 0xbe, 0xbf, 0xc0,
    0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc8, 0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf, 0xd0, 0xd2, 0xd3, 0xd4,
    0xd6, 0xd7, 0xd8, 0xd9, 0xdb, 0xdc, 0xdd, 0xde, 0xe0, 0xe1, 0xe2, 0xe4, 0xe5, 0xe6, 0xe8, 0xe9,
    0xea, 0xec, 0xed, 0xee, 0xf0, 0xf1, 0xf2, 0xf4, 0xf5, 0xf6, 0xf8, 0xf9, 0xfa, 0xfc, 0xfd, 0xff,
  ];

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
    if ((flags & FlagDsd) != 0)
      throw new NotSupportedException("DSD WavPack blocks are not supported.");
    _ = FlagCrossDecorr;
    _ = FlagFalseStereo;
    _ = FlagMagLsb;
    _ = FlagMagMask;
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
