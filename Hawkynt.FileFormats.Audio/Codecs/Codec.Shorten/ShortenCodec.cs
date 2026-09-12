#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Codec.Shorten;

/// <summary>
/// Constants from shorten 3.6.x (Tony Robinson / SoftSound) and ffmpeg
/// <c>libavcodec/shorten.c</c>. The numeric command codes and entropy-size constants
/// are part of the on-wire format and must match exactly for interoperability.
/// </summary>
internal static class ShortenConstants {
  public const int UlongSize = 2;   // ULONGSIZE: k used by ulong_get's leading uvar
  public const int FnSize = 2;      // FNSIZE: k used to read function/command codes
  public const int EnergySize = 3;  // ENERGYSIZE: k for the per-block residual energy
  public const int BitshiftSize = 2;// BITSHIFTSIZE: k for FN_BITSHIFT amount
  public const int LpcQuantSize = 2;// LPCQUANT field width family (decode side only)
  public const int NSkipSize = 1;   // NSKIPSIZE-ish: skip-byte count parameter width

  public const int DefaultBlockSize = 256;

  // Function (command) codes.
  public const int FnDiff0 = 0;
  public const int FnDiff1 = 1;
  public const int FnDiff2 = 2;
  public const int FnDiff3 = 3;
  public const int FnQuit = 4;
  public const int FnBlocksize = 5;
  public const int FnBitshift = 6;
  public const int FnQlpc = 7;
  public const int FnZero = 8;
  public const int FnVerbatim = 9;

  // File-type codes (subset shorten supports; this codec handles 2, 3, 5).
  public const int TypeU8 = 2;   // unsigned 8-bit
  public const int TypeS8 = 3;   // signed 8-bit
  public const int TypeS16Le = 5;// signed 16-bit, little-endian

  public const int VerbatimChunkSize = 256; // VERBATIM_CHUNK_SIZE
  public const int VerbatimByteSize = 8;     // VERBATIM_BYTE_SIZE
}

/// <summary>
/// Shorten (<c>.shn</c>) lossless audio codec — both encoder and decoder, so PCM round-trips
/// can be verified. Decodes the classic SoftSound bitstream (magic <c>ajkg</c>, version 2)
/// into canonical interleaved little-endian PCM and encodes interleaved PCM back into a
/// version-2 stream.
/// <para>
/// Exact-spec behaviour (matches shorten 3.6.x / ffmpeg <c>shorten.c</c>): the bit packing,
/// the <c>uvar</c>/<c>ulong</c>/<c>var</c> entropy primitives, the version-2 header
/// (<c>ftype, nchan, blocksize, maxnlpc, nmean, nskip</c> + skip bytes), the polynomial
/// fixed predictors <c>FN_DIFF0..FN_DIFF3</c>, <c>FN_ZERO</c>, <c>FN_BLOCKSIZE</c>,
/// <c>FN_BITSHIFT</c>, <c>FN_VERBATIM</c>, <c>FN_QUIT</c>, the per-block residual energy
/// parameter, the round-robin per-channel block interleave, and the running channel-mean
/// offset when <c>nmean &gt; 0</c>.
/// </para>
/// <para>
/// <c>FN_QLPC</c> (quantised LPC) is decoded on a best-effort basis following shorten's layout
/// (order via <c>ulong_get</c>, then per-coefficient <c>var_get(LPCQUANT)</c>); this codec's
/// own encoder never emits <c>FN_QLPC</c> — it selects the best fixed predictor per block like
/// FLAC's fixed-order search — so the QLPC path is exercised only against third-party streams.
/// </para>
/// <para>
/// The Shorten container carries no sample-rate field; the <c>sampleRate</c> parameter on
/// <see cref="Compress"/> is accepted for caller convenience but not stored, and
/// <see cref="ReadStreamInfo"/> reports a sample rate of 0 (unknown).
/// </para>
/// </summary>
public static class ShortenCodec {

  private static readonly byte[] Magic = "ajkg"u8.ToArray();
  private const byte FormatVersion = 2;

  /// <summary>Header facts a caller needs to drive channel-splitting / PCM framing.</summary>
  /// <param name="Channels">Channel count.</param>
  /// <param name="BitsPerSample">Decoded PCM width in bits (8 or 16).</param>
  /// <param name="FileType">Raw shorten file-type code (2 = u8, 3 = s8, 5 = s16 LE).</param>
  /// <param name="SampleRate">Always 0 — Shorten stores no sample rate.</param>
  public readonly record struct ShortenStreamInfo(int Channels, int BitsPerSample, int FileType, int SampleRate);

  /// <summary>
  /// Reads the Shorten header (magic, version and the version-2 header commands) without
  /// decoding audio. Sample rate is unknown to the format and reported as 0.
  /// </summary>
  public static ShortenStreamInfo ReadStreamInfo(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var data = ReadAll(input);
    var reader = OpenAfterMagic(data);
    var header = ReadHeader(reader);
    return new ShortenStreamInfo(header.Channels, BitsForType(header.FileType), header.FileType, SampleRate: 0);
  }

  /// <summary>
  /// Decodes a Shorten stream on <paramref name="shnInput"/> into raw interleaved
  /// little-endian PCM on <paramref name="pcmOutput"/>.
  /// </summary>
  public static void Decompress(Stream shnInput, Stream pcmOutput) {
    ArgumentNullException.ThrowIfNull(shnInput);
    ArgumentNullException.ThrowIfNull(pcmOutput);

    var data = ReadAll(shnInput);
    var reader = OpenAfterMagic(data);
    var header = ReadHeader(reader);

    var nchan = header.Channels;
    var blocksize = header.BlockSize;
    var bytesPerSample = BitsForType(header.FileType) / 8;
    var fileType = header.FileType;

    // Per-channel history: shorten keeps the previous 'nwrap' samples in front of each
    // block so the fixed predictors can reach back across block boundaries. order<=3 here.
    const int nwrap = 3;
    var buffer = new int[nchan][];
    for (var c = 0; c < nchan; ++c)
      buffer[c] = new int[nwrap + blocksize];

    // Running channel mean (offset) state, used only when nmean > 0.
    var nmean = header.NMean;
    var means = new long[nchan];
    var meanRing = new int[nchan][];
    var meanRingPos = new int[nchan];
    if (nmean > 0)
      for (var c = 0; c < nchan; ++c)
        meanRing[c] = new int[nmean];

    var chan = 0;
    var samplesWritten = 0L;

    while (!reader.AtEnd) {
      int cmd;
      try {
        cmd = (int)reader.UVarGet(ShortenConstants.FnSize);
      } catch (InvalidDataException) {
        break; // padded trailer reached
      }

      if (cmd == ShortenConstants.FnQuit)
        break;

      switch (cmd) {
        case ShortenConstants.FnBlocksize:
          blocksize = (int)reader.ULongGet();
          for (var c = 0; c < nchan; ++c) {
            var grown = new int[nwrap + blocksize];
            Array.Copy(buffer[c], grown, Math.Min(buffer[c].Length, grown.Length));
            buffer[c] = grown;
          }
          continue;

        case ShortenConstants.FnBitshift:
          header.BitShift = (int)reader.UVarGet(ShortenConstants.BitshiftSize);
          continue;

        case ShortenConstants.FnVerbatim: {
          var n = (int)reader.ULongGet();
          for (var i = 0; i < n; ++i)
            reader.UVarGet(ShortenConstants.VerbatimByteSize);
          continue;
        }

        case ShortenConstants.FnDiff0:
        case ShortenConstants.FnDiff1:
        case ShortenConstants.FnDiff2:
        case ShortenConstants.FnDiff3:
        case ShortenConstants.FnZero:
        case ShortenConstants.FnQlpc: {
          var b = buffer[chan];
          DecodeBlock(reader, cmd, b, nwrap, blocksize);

          // Channel-mean offset.
          var offset = 0;
          if (nmean > 0) {
            offset = means[chan] == 0 ? 0 : RoundedMean(means[chan], nmean);
            for (var i = 0; i < blocksize; ++i)
              b[nwrap + i] += offset;
          }

          // Update running mean from this block's average.
          if (nmean > 0) {
            long sum = 0;
            for (var i = 0; i < blocksize; ++i)
              sum += b[nwrap + i];
            var blockMean = (int)(sum / blocksize);
            var ring = meanRing[chan];
            means[chan] += blockMean - ring[meanRingPos[chan]];
            ring[meanRingPos[chan]] = blockMean;
            meanRingPos[chan] = (meanRingPos[chan] + 1) % nmean;
          }

          // Emit interleaved once a full frame across all channels is ready: shorten
          // round-robins channel blocks, so when we just decoded the last channel we can
          // flush 'blocksize' interleaved frames.
          if (chan == nchan - 1) {
            WriteFrames(pcmOutput, buffer, nchan, nwrap, blocksize, bytesPerSample, fileType, header.BitShift);
            samplesWritten += blocksize;
          }

          // Carry the trailing 'nwrap' samples to the head for the next block of this channel.
          for (var i = 0; i < nwrap; ++i)
            b[i] = b[blocksize + i];

          chan = (chan + 1) % nchan;
          continue;
        }

        default:
          throw new NotSupportedException($"Unsupported Shorten command {cmd}.");
      }
    }

    _ = samplesWritten;
  }

  /// <summary>
  /// Encodes interleaved little-endian PCM on <paramref name="pcmInput"/> into a
  /// version-2 Shorten stream on <paramref name="shnOutput"/>. The encoder selects the
  /// best polynomial predictor (DIFF0..DIFF3) per block, like FLAC's fixed-order search.
  /// <para>
  /// <paramref name="sampleRate"/> is accepted for caller convenience; the Shorten
  /// container has no sample-rate field, so it is not stored.
  /// </para>
  /// </summary>
  public static void Compress(Stream pcmInput, Stream shnOutput, int channels, int sampleRate, int bitsPerSample) {
    ArgumentNullException.ThrowIfNull(pcmInput);
    ArgumentNullException.ThrowIfNull(shnOutput);
    if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
    if (bitsPerSample is not (8 or 16)) throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "Shorten codec supports 8-bit or 16-bit PCM.");
    _ = sampleRate; // not part of the Shorten container

    var fileType = bitsPerSample == 8 ? ShortenConstants.TypeU8 : ShortenConstants.TypeS16Le;
    var bytesPerSample = bitsPerSample / 8;

    var pcm = ReadAll(pcmInput);
    var frameBytes = bytesPerSample * channels;
    if (frameBytes == 0 || pcm.Length % frameBytes != 0)
      throw new ArgumentException("Interleaved PCM length is not a multiple of the frame size.");
    var frameCount = pcm.Length / frameBytes;

    const int blocksize = ShortenConstants.DefaultBlockSize;
    const int nwrap = 3;

    // Header.
    shnOutput.Write(Magic);
    shnOutput.WriteByte(FormatVersion);

    var writer = new ShortenBitWriter(shnOutput);
    writer.ULongPut((uint)fileType);
    writer.ULongPut((uint)channels);
    writer.ULongPut(blocksize);
    writer.ULongPut(0);            // maxnlpc — encoder emits no QLPC
    writer.ULongPut(0);            // nmean   — encoder emits no channel-mean offsets
    writer.ULongPut(0);            // nskip   — no skipped bytes
    // (no skip bytes follow when nskip == 0)

    // Deinterleave into per-channel sample arrays with leading nwrap zero history.
    var samples = new int[channels][];
    for (var c = 0; c < channels; ++c)
      samples[c] = new int[nwrap + frameCount];

    for (var f = 0; f < frameCount; ++f) {
      for (var c = 0; c < channels; ++c) {
        var off = f * frameBytes + c * bytesPerSample;
        samples[c][nwrap + f] = fileType == ShortenConstants.TypeU8
          ? pcm[off] - 128                                   // store as signed about midpoint
          : BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(off));
      }
    }

    var pos = 0;
    while (pos < frameCount) {
      var thisBlock = Math.Min(blocksize, frameCount - pos);

      if (thisBlock != blocksize) {
        writer.UVarPut(ShortenConstants.FnBlocksize, ShortenConstants.FnSize);
        writer.ULongPut((uint)thisBlock);
      }

      for (var c = 0; c < channels; ++c)
        EncodeBlock(writer, samples[c], nwrap + pos, nwrap, thisBlock);

      pos += thisBlock;
    }

    writer.UVarPut(ShortenConstants.FnQuit, ShortenConstants.FnSize);
    writer.Flush();
  }

  // ── Block decode ─────────────────────────────────────────────────────────────

  private static void DecodeBlock(ShortenBitReader reader, int cmd, int[] b, int nwrap, int blocksize) {
    if (cmd == ShortenConstants.FnZero) {
      for (var i = 0; i < blocksize; ++i)
        b[nwrap + i] = 0;
      return;
    }

    if (cmd == ShortenConstants.FnQlpc) {
      DecodeQlpc(reader, b, nwrap, blocksize);
      return;
    }

    var order = cmd; // FN_DIFF0..3 → polynomial order 0..3
    var k = (int)reader.UVarGet(ShortenConstants.EnergySize);
    for (var i = 0; i < blocksize; ++i) {
      var residual = reader.VarGet(k);
      var idx = nwrap + i;
      var prediction = Predict(b, idx, order);
      b[idx] = prediction + residual;
    }
  }

  private static void DecodeQlpc(ShortenBitReader reader, int[] b, int nwrap, int blocksize) {
    // Best-effort QLPC decode following shorten's layout. Not produced by this encoder.
    var order = (int)reader.ULongGet();
    var coeffs = new int[order];
    for (var i = 0; i < order; ++i)
      coeffs[i] = reader.VarGet(ShortenConstants.LpcQuantSize);

    var k = (int)reader.UVarGet(ShortenConstants.EnergySize);
    const int lpcQuant = 5; // shorten's default LPCQUANT shift
    for (var i = 0; i < blocksize; ++i) {
      var residual = reader.VarGet(k);
      long prediction = 0;
      for (var j = 0; j < order; ++j) {
        var hist = nwrap + i - 1 - j;
        prediction += (long)coeffs[j] * b[hist >= 0 ? hist : 0];
      }
      b[nwrap + i] = residual + (int)(prediction >> lpcQuant);
    }
  }

  private static int Predict(int[] b, int idx, int order) => order switch {
    0 => 0,
    1 => b[idx - 1],
    2 => 2 * b[idx - 1] - b[idx - 2],
    3 => 3 * b[idx - 1] - 3 * b[idx - 2] + b[idx - 3],
    _ => 0,
  };

  // ── Block encode ─────────────────────────────────────────────────────────────

  private static void EncodeBlock(ShortenBitWriter writer, int[] samples, int start, int nwrap, int blocksize) {
    // Pick the polynomial order (0..3) whose residuals have the smallest absolute sum.
    var bestOrder = 0;
    long bestCost = long.MaxValue;
    var bestK = 0;
    int[]? bestResiduals = null;

    for (var order = 0; order <= 3; ++order) {
      var residuals = new int[blocksize];
      long absSum = 0;
      for (var i = 0; i < blocksize; ++i) {
        var idx = start + i;
        var prediction = PredictAbsolute(samples, idx, order, start, nwrap);
        var r = samples[idx] - prediction;
        residuals[i] = r;
        absSum += r < 0 ? -(long)r : r;
      }

      var k = RiceParam(absSum, blocksize);
      var cost = EstimateBits(residuals, k);
      if (cost >= bestCost)
        continue;

      bestCost = cost;
      bestOrder = order;
      bestK = k;
      bestResiduals = residuals;
    }

    var chosen = bestResiduals!;
    writer.UVarPut((uint)bestOrder, ShortenConstants.FnSize);
    writer.UVarPut((uint)bestK, ShortenConstants.EnergySize);
    for (var i = 0; i < blocksize; ++i)
      writer.VarPut(chosen[i], bestK);
  }

  // For encoding, the predictor must reach back across the block start into the carried
  // history; samples before the very first frame are zero (the nwrap zero pad).
  private static int PredictAbsolute(int[] samples, int idx, int order, int start, int nwrap) {
    int At(int i) => i < start - nwrap ? 0 : (i < 0 ? 0 : samples[i]);
    return order switch {
      0 => 0,
      1 => At(idx - 1),
      2 => 2 * At(idx - 1) - At(idx - 2),
      3 => 3 * At(idx - 1) - 3 * At(idx - 2) + At(idx - 3),
      _ => 0,
    };
  }

  private static int RiceParam(long absSum, int count) {
    if (count == 0 || absSum == 0)
      return 0;
    // shorten: choose k so that 2^k is near the mean magnitude.
    var mean = (double)absSum / count;
    var k = 0;
    while ((1L << (k + 1)) < mean + 1 && k < 30)
      ++k;
    return k;
  }

  private static long EstimateBits(int[] residuals, int k) {
    long bits = 0;
    foreach (var r in residuals) {
      var u = r < 0 ? (uint)(~r << 1) | 1u : (uint)r << 1;
      bits += (u >> k) + 1 + k;
    }
    return bits;
  }

  // ── Output framing ─────────────────────────────────────────────────────────────

  private static void WriteFrames(Stream output, int[][] buffer, int nchan, int nwrap,
      int blocksize, int bytesPerSample, int fileType, int bitShift) {
    var frame = new byte[blocksize * nchan * bytesPerSample];
    var p = 0;
    for (var i = 0; i < blocksize; ++i) {
      for (var c = 0; c < nchan; ++c) {
        var v = buffer[c][nwrap + i];
        if (bitShift > 0)
          v <<= bitShift;
        if (bytesPerSample == 1) {
          // file types 2 (u8) and 3 (s8): store as raw byte. Type 2 PCM is unsigned.
          frame[p++] = fileType == ShortenConstants.TypeU8 ? (byte)(v + 128) : (byte)(sbyte)v;
        } else {
          BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(p), (short)v);
          p += 2;
        }
      }
    }
    output.Write(frame, 0, p);
  }

  private static int RoundedMean(long sum, int n) => (int)((sum + (n >> 1)) / n);

  private static int BitsForType(int fileType) => fileType switch {
    ShortenConstants.TypeU8 or ShortenConstants.TypeS8 => 8,
    ShortenConstants.TypeS16Le => 16,
    _ => throw new NotSupportedException($"Unsupported Shorten file type {fileType}; this codec handles 2, 3 and 5."),
  };

  // ── Header parsing ───────────────────────────────────────────────────────────

  private sealed class Header {
    public int FileType;
    public int Channels;
    public int BlockSize;
    public int MaxNlpc;
    public int NMean;
    public int NSkip;
    public int BitShift;
  }

  /// <summary>
  /// Opens a continuous MSB-first bit reader positioned right after the 4-byte magic and the
  /// 1-byte version. Shorten packs the header values and the audio command stream into one
  /// uninterrupted bitstream — there is no byte alignment between them — so the same reader is
  /// used for both.
  /// </summary>
  private static ShortenBitReader OpenAfterMagic(byte[] data) {
    if (data.Length < 5 ||
        data[0] != Magic[0] || data[1] != Magic[1] || data[2] != Magic[2] || data[3] != Magic[3])
      throw new InvalidDataException("Not a Shorten stream: missing 'ajkg' magic.");

    var version = data[4];
    if (version != FormatVersion)
      throw new NotSupportedException($"Unsupported Shorten version {version}; this codec handles version 2.");

    return new ShortenBitReader(data, 5);
  }

  private static Header ReadHeader(ShortenBitReader reader) {
    var header = new Header {
      FileType = (int)reader.ULongGet(),
      Channels = (int)reader.ULongGet(),
      BlockSize = (int)reader.ULongGet(),
      MaxNlpc = (int)reader.ULongGet(),
      NMean = (int)reader.ULongGet(),
      NSkip = (int)reader.ULongGet(),
    };

    // Skipped bytes follow as nskip uvar values; consume and ignore.
    for (var i = 0; i < header.NSkip; ++i)
      reader.UVarGet(ShortenConstants.VerbatimByteSize);

    if (header.Channels < 1)
      throw new InvalidDataException("Shorten header declares zero channels.");
    if (header.BlockSize < 1)
      throw new InvalidDataException("Shorten header declares an invalid block size.");
    _ = BitsForType(header.FileType); // validate file type early

    return header;
  }

  // ── Utilities ──────────────────────────────────────────────────────────────

  private static byte[] ReadAll(Stream input) {
    if (input is MemoryStream ms && ms.TryGetBuffer(out var seg) && seg.Offset == 0 && seg.Count == seg.Array!.Length)
      return seg.Array;
    using var tmp = new MemoryStream();
    input.CopyTo(tmp);
    return tmp.ToArray();
  }
}
