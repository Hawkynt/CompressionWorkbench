#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Codec.Tta;

/// <summary>
/// True Audio (TTA1) lossless codec — both encoder and decoder. Reads and writes
/// the on-disk TTA1 layout faithfully: an 18-byte little-endian header
/// (<c>"TTA1"</c> | format | channels | bits | sample-rate | data-length) guarded
/// by a CRC-32, a seek table of per-frame byte sizes guarded by its own CRC-32,
/// and a sequence of independent frames. Each frame interleaves the channels
/// through TTA's order-8 adaptive hybrid filter (<see cref="TtaFilter"/>), a
/// fixed-order integer predictor, inter-channel decorrelation and the two-state
/// adaptive Rice coder (<see cref="TtaRice"/>), then appends a CRC-32 of the
/// coded bytes. PCM in and out is raw interleaved little-endian signed integers
/// (8-bit is stored unsigned, matching WAV).
/// <para>
/// The frame length is <c>(sampleRate * 256) / 245</c> samples (the
/// 1.04489795918367 s FRAME_TIME of the reference), with a shorter trailing
/// frame. The bitstream algorithms (filter, predictor, decorrelation, Rice
/// adaptation, CRC polynomial) are ported verbatim from the reference / ffmpeg
/// <c>libavcodec/tta.c</c>, so a stream this codec writes is structurally a
/// genuine TTA1 file and round-trips losslessly.
/// </para>
/// </summary>
public static class TtaCodec {

  private const int FrameTimeNum = 256;
  private const int FrameTimeDen = 245;
  private const int HeaderBytes = 18;

  /// <summary>Stream geometry callers need to build PCM headers / split channels.</summary>
  public readonly record struct TtaStreamInfo(int Channels, int SampleRate, int BitsPerSample, long SampleCount);

  /// <summary>Samples-per-channel in a full TTA frame for the given sample rate.</summary>
  private static int FrameLength(int sampleRate) => (int)((long)sampleRate * FrameTimeNum / FrameTimeDen);

  // ── Encode ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Encodes raw interleaved little-endian PCM (<paramref name="pcmInput"/>) to a
  /// TTA1 stream on <paramref name="ttaOutput"/>.
  /// </summary>
  public static void Compress(Stream pcmInput, Stream ttaOutput, int channels, int sampleRate, int bitsPerSample) {
    ArgumentNullException.ThrowIfNull(pcmInput);
    ArgumentNullException.ThrowIfNull(ttaOutput);
    if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
    if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));
    if (bitsPerSample is not (8 or 16 or 24 or 32))
      throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "TTA supports 8, 16, 24 or 32 bits per sample.");

    using var ms = new MemoryStream();
    pcmInput.CopyTo(ms);
    var pcm = ms.ToArray();

    var bytesPerSample = bitsPerSample / 8;
    var frameBytes = bytesPerSample * channels;
    if (pcm.Length % frameBytes != 0)
      throw new ArgumentException("PCM length is not a multiple of the frame (channels × bytes-per-sample) size.");
    var totalSamples = pcm.Length / frameBytes; // per channel

    var frameLength = FrameLength(sampleRate);
    var frameCount = totalSamples == 0 ? 0 : (totalSamples + frameLength - 1) / frameLength;

    // Encode every frame up front so the seek table can carry exact byte sizes.
    var frames = new List<byte[]>(frameCount);
    var sampleOffset = 0;
    for (var f = 0; f < frameCount; ++f) {
      var thisLen = Math.Min(frameLength, totalSamples - sampleOffset);
      frames.Add(EncodeFrame(pcm, sampleOffset, thisLen, channels, bitsPerSample));
      sampleOffset += thisLen;
    }

    WriteHeader(ttaOutput, channels, bitsPerSample, sampleRate, totalSamples);
    WriteSeekTable(ttaOutput, frames);
    foreach (var frame in frames)
      ttaOutput.Write(frame);
  }

  private static byte[] EncodeFrame(byte[] pcm, int sampleOffset, int frameLen, int channels, int bps) {
    var bytesPerSample = bps / 8;
    var frameBytes = bytesPerSample * channels;

    var rice = new TtaRice[channels];
    var filter = new TtaFilter[channels];
    var predictor = new int[channels];
    var fixedShift = PredictorShift(bps);
    var filterShift = TtaFilter.ShiftForBitsPerSample(bps);
    for (var c = 0; c < channels; ++c) {
      rice[c] = new TtaRice();
      filter[c] = new TtaFilter(filterShift);
    }

    var writer = new TtaBitWriter();

    for (var s = 0; s < frameLen; ++s) {
      var baseByte = (sampleOffset + s) * frameBytes;

      // Read this sample's channel values into signed ints.
      var ch = new int[channels];
      for (var c = 0; c < channels; ++c)
        ch[c] = ReadSample(pcm, baseByte + c * bytesPerSample, bps);

      // Forward inter-channel decorrelation — the exact algebraic inverse of
      // tta.c's decode-side walk. Decode computes, in place over stored s[]:
      //   o[L-1] = s[L-1] + s[L-2]/2;  o[c] = o[c+1] - s[c]  (c = L-2 … 0).
      // Inverting: s[c] = o[c+1] - o[c] for c < L-1, then s[L-1] = o[L-1] - s[L-2]/2.
      if (channels > 1) {
        for (var c = 0; c < channels - 1; ++c)
          ch[c] = ch[c + 1] - ch[c];
        ch[channels - 1] -= ch[channels - 2] / 2;
      }

      for (var c = 0; c < channels; ++c) {
        var value = ch[c];

        // Fixed-order predictor (forward).
        var pred = Pred(predictor[c], fixedShift);
        predictor[c] = value;
        var afterFixed = value - pred;

        // Adaptive hybrid filter (forward).
        var residual = filter[c].Encode(afterFixed);

        rice[c].Encode(writer, residual);
      }
    }

    var coded = writer.Flush();
    var crc = TtaCrc.Compute(coded);
    var frame = new byte[coded.Length + 4];
    coded.CopyTo(frame, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(coded.Length), crc);
    return frame;
  }

  // ── Decode ───────────────────────────────────────────────────────────────

  /// <summary>Decodes a TTA1 stream to raw interleaved little-endian PCM.</summary>
  public static void Decompress(Stream ttaInput, Stream pcmOutput) {
    ArgumentNullException.ThrowIfNull(ttaInput);
    ArgumentNullException.ThrowIfNull(pcmOutput);

    using var ms = new MemoryStream();
    ttaInput.CopyTo(ms);
    var data = ms.ToArray();

    var (info, pos) = ReadHeaderAt(data);
    var bps = info.BitsPerSample;
    var channels = info.Channels;
    var bytesPerSample = bps / 8;

    var frameLength = FrameLength(info.SampleRate);
    var frameCount = info.SampleCount == 0 ? 0 : (int)((info.SampleCount + frameLength - 1) / frameLength);

    var (seekTable, afterSeek) = ReadSeekTable(data, pos, frameCount);
    pos = afterSeek;

    var samplesRemaining = info.SampleCount;
    for (var f = 0; f < frameCount; ++f) {
      var frameSize = (int)seekTable[f];
      if (pos + frameSize > data.Length)
        throw new InvalidDataException("TTA frame extends past end of stream.");
      if (frameSize < 4)
        throw new InvalidDataException("TTA frame too small to hold its CRC.");

      var codedLen = frameSize - 4;
      var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + codedLen));
      var actualCrc = TtaCrc.Compute(data.AsSpan(pos, codedLen));
      if (actualCrc != storedCrc)
        throw new InvalidDataException($"TTA frame {f} CRC mismatch (got 0x{actualCrc:X8}, expected 0x{storedCrc:X8}).");

      var thisLen = (int)Math.Min(frameLength, samplesRemaining);
      DecodeFrame(data, pos, codedLen, thisLen, channels, bps, pcmOutput);
      samplesRemaining -= thisLen;
      pos += frameSize;
    }
  }

  private static void DecodeFrame(byte[] data, int offset, int codedLen, int frameLen, int channels, int bps, Stream output) {
    var bytesPerSample = bps / 8;
    var fixedShift = PredictorShift(bps);
    var filterShift = TtaFilter.ShiftForBitsPerSample(bps);

    var rice = new TtaRice[channels];
    var filter = new TtaFilter[channels];
    var predictor = new int[channels];
    for (var c = 0; c < channels; ++c) {
      rice[c] = new TtaRice();
      filter[c] = new TtaFilter(filterShift);
    }

    var reader = new TtaBitReader(data, offset);
    _ = codedLen;
    var outBuf = new byte[frameLen * channels * bytesPerSample];
    var ch = new int[channels];

    for (var s = 0; s < frameLen; ++s) {
      for (var c = 0; c < channels; ++c) {
        var residual = rice[c].Decode(reader);

        // Adaptive hybrid filter (inverse).
        var afterFixed = filter[c].Decode(residual);

        // Fixed-order predictor (inverse).
        var value = afterFixed + Pred(predictor[c], fixedShift);
        predictor[c] = value;
        ch[c] = value;
      }

      // Inter-channel decorrelation (decode side, tta.c).
      if (channels > 1) {
        ch[channels - 1] += ch[channels - 2] / 2;
        for (var c = channels - 2; c >= 0; --c)
          ch[c] = ch[c + 1] - ch[c];
      }

      var baseByte = s * channels * bytesPerSample;
      for (var c = 0; c < channels; ++c)
        WriteSample(outBuf, baseByte + c * bytesPerSample, bps, ch[c]);
    }

    output.Write(outBuf);
  }

  // ── Header / seek table ────────────────────────────────────────────────────

  /// <summary>Reads the TTA1 header without decoding the audio.</summary>
  public static TtaStreamInfo ReadStreamInfo(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    Span<byte> header = stackalloc byte[HeaderBytes + 4];
    var read = 0;
    while (read < header.Length) {
      var n = input.Read(header[read..]);
      if (n == 0) break;
      read += n;
    }
    if (read < header.Length)
      throw new InvalidDataException("Stream too short for a TTA1 header.");
    return ParseHeader(header);
  }

  private static (TtaStreamInfo Info, int Pos) ReadHeaderAt(byte[] data) {
    if (data.Length < HeaderBytes + 4)
      throw new InvalidDataException("Stream too short for a TTA1 header.");
    var info = ParseHeader(data);
    return (info, HeaderBytes + 4);
  }

  private static TtaStreamInfo ParseHeader(ReadOnlySpan<byte> data) {
    if (data[0] != (byte)'T' || data[1] != (byte)'T' || data[2] != (byte)'A' || data[3] != (byte)'1')
      throw new InvalidDataException("Not a TTA1 stream: missing 'TTA1' magic.");

    var headerCrc = BinaryPrimitives.ReadUInt32LittleEndian(data[HeaderBytes..]);
    var actual = TtaCrc.Compute(data[..HeaderBytes]);
    if (actual != headerCrc)
      throw new InvalidDataException($"TTA header CRC mismatch (got 0x{actual:X8}, expected 0x{headerCrc:X8}).");

    var format = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
    if (format != 1)
      throw new InvalidDataException($"Unsupported TTA audio format {format} (only integer PCM = 1 is supported).");
    var channels = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
    var bps = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
    var sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[10..]);
    var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(data[14..]);
    return new TtaStreamInfo(channels, sampleRate, bps, dataLength);
  }

  private static void WriteHeader(Stream output, int channels, int bps, int sampleRate, long totalSamples) {
    Span<byte> header = stackalloc byte[HeaderBytes + 4];
    header[0] = (byte)'T'; header[1] = (byte)'T'; header[2] = (byte)'A'; header[3] = (byte)'1';
    BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 1); // integer PCM
    BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)channels);
    BinaryPrimitives.WriteUInt16LittleEndian(header[8..], (ushort)bps);
    BinaryPrimitives.WriteUInt32LittleEndian(header[10..], (uint)sampleRate);
    BinaryPrimitives.WriteUInt32LittleEndian(header[14..], (uint)totalSamples);
    var crc = TtaCrc.Compute(header[..HeaderBytes]);
    BinaryPrimitives.WriteUInt32LittleEndian(header[HeaderBytes..], crc);
    output.Write(header);
  }

  private static void WriteSeekTable(Stream output, IReadOnlyList<byte[]> frames) {
    var bytes = new byte[frames.Count * 4 + 4];
    for (var i = 0; i < frames.Count; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), (uint)frames[i].Length);
    var crc = TtaCrc.Compute(bytes.AsSpan(0, frames.Count * 4));
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(frames.Count * 4), crc);
    output.Write(bytes);
  }

  private static (uint[] Sizes, int Pos) ReadSeekTable(byte[] data, int pos, int frameCount) {
    var tableBytes = frameCount * 4;
    if (pos + tableBytes + 4 > data.Length)
      throw new InvalidDataException("Stream too short for the TTA seek table.");

    var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + tableBytes));
    var actualCrc = TtaCrc.Compute(data.AsSpan(pos, tableBytes));
    if (actualCrc != storedCrc)
      throw new InvalidDataException($"TTA seek-table CRC mismatch (got 0x{actualCrc:X8}, expected 0x{storedCrc:X8}).");

    var sizes = new uint[frameCount];
    for (var i = 0; i < frameCount; ++i)
      sizes[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + i * 4));
    return (sizes, pos + tableBytes + 4);
  }

  // ── Sample helpers ──────────────────────────────────────────────────────────

  // Fixed-predictor shift per depth (tta.c PRED): 8-bit→4, 16/24-bit→5, 32-bit→0 (predictor added whole).
  private static int PredictorShift(int bps) => bps switch {
    8 => 4,
    16 or 24 => 5,
    32 => 0,
    _ => throw new NotSupportedException($"Unsupported TTA bit depth: {bps}."),
  };

  // PRED(x,k) = ((x << k) - x) >> k; for k == 0 the predictor is added whole.
  private static int Pred(int x, int k) => k == 0 ? x : (int)((((long)x << k) - x) >> k);

  private static int ReadSample(byte[] pcm, int offset, int bps) {
    switch (bps) {
      case 8:
        return pcm[offset] - 0x80; // WAV 8-bit is unsigned.
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
        throw new NotSupportedException($"Unsupported TTA bit depth: {bps}.");
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
        throw new NotSupportedException($"Unsupported TTA bit depth: {bps}.");
    }
  }
}
