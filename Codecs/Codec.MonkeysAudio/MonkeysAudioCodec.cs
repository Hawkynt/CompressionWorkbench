#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Codec.MonkeysAudio;

/// <summary>
/// Monkey's Audio (.ape) lossless codec — both encoder and decoder for the
/// version 3.99 ("current") container. The on-disk layout is written and read
/// exactly to spec: the <c>"MAC "</c> magic, a u16 version (3990), the v3.98+
/// <c>APE_DESCRIPTOR</c> (padding, the descriptor/header/seek-table/header-data/
/// frame-data byte counts and a 16-byte MD5) and the 24-byte <c>APE_HEADER</c>
/// (compression level, format flags, blocks-per-frame, final-frame blocks, total
/// frames, bits-per-sample, channels, sample rate), followed by a u32-per-frame
/// seek table and the range-coded frame data. PCM in and out is raw interleaved
/// little-endian signed integers.
/// <para>
/// Per the pragmatic-yet-self-consistent bar, the <em>encoder</em> emits the
/// compression level 1000 ("fast") profile only — the order-16 adaptive predictor
/// (<see cref="ApePredictor"/>), X/Y mid-side stereo decorrelation and the range
/// coder (<see cref="ApeRangeDecoder"/>/<see cref="ApeRangeEncoder"/>) with the
/// 3.98+ overflow-class entropy stage (<see cref="ApeEntropy"/>). The
/// <em>decoder</em> handles level-1000 streams fully; higher compression levels
/// (2000+), which add cascaded high-order filters this codec does not implement,
/// are rejected with <see cref="NotSupportedException"/> so container descriptors
/// can fall back gracefully.
/// </para>
/// <para>
/// EXACT-spec: the container descriptor/header/seek-table layout, the magic and
/// version, and the overflow-class cumulative table. SELF-CONSISTENT (encoder
/// algebraically inverted to this decoder, not bit-verified against the reference
/// SDK): the range-coder normalisation (carryless Subbotin form), the predictor
/// weight adaptation, and the Rice <c>k</c> update. A stream this codec writes is
/// a structurally valid level-1000 MAC file and round-trips losslessly through
/// this decoder.
/// </para>
/// </summary>
public static class MonkeysAudioCodec {

  private const int VersionWrite = 3990;
  private const int DescriptorBytes = 52;
  private const int HeaderBytes = 24;
  private const int BlocksPerFrame = 73728;

  public const int CompressionFast = 1000;
  public const int CompressionNormal = 2000;
  public const int CompressionHigh = 3000;
  public const int CompressionExtraHigh = 4000;
  public const int CompressionInsane = 5000;

  /// <summary>Stream geometry callers need to build PCM headers / split channels.</summary>
  public readonly record struct MonkeysAudioStreamInfo(
    int Channels, int SampleRate, int BitsPerSample, long TotalSamples, int CompressionLevel, int Version);

  // ── Public stream-info ───────────────────────────────────────────────────────

  /// <summary>Reads the descriptor + header to report channel count, rate, depth,
  /// sample count, compression level and version without decoding the audio.</summary>
  public static MonkeysAudioStreamInfo ReadStreamInfo(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    return ParseHeader(ms.ToArray()).Info;
  }

  // ── Header parse ──────────────────────────────────────────────────────────────

  private readonly record struct ParsedFile(
    MonkeysAudioStreamInfo Info, int SeekTableStart, uint SeekTableBytes,
    int FrameDataStart, uint TotalFrames, uint FinalFrameBlocks, uint BlocksPerFrameValue);

  private static ParsedFile ParseHeader(byte[] file) {
    if (file.Length < DescriptorBytes + HeaderBytes
        || file[0] != (byte)'M' || file[1] != (byte)'A' || file[2] != (byte)'C' || file[3] != (byte)' ')
      throw new InvalidDataException("Not a Monkey's Audio stream: missing 'MAC ' magic.");

    var version = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4));
    if (version < 3980)
      throw new NotSupportedException($"Monkey's Audio version {version} (pre-3.98) is not supported.");

    // APE_DESCRIPTOR (v3.98+): magic(4) version(2) pad(2) descriptorBytes(4)
    // headerBytes(4) seekTableBytes(4) headerDataBytes(4) apeFrameDataBytes(4)
    // apeFrameDataBytesHigh(4) terminatingDataBytes(4) md5(16).
    var descriptorBytes = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));
    var headerBytes = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12));
    var seekTableBytes = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16));
    var headerDataBytes = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(20));
    if (descriptorBytes < DescriptorBytes) descriptorBytes = DescriptorBytes;

    var headerStart = (int)descriptorBytes;
    if (headerStart + HeaderBytes > file.Length || headerBytes < HeaderBytes)
      throw new InvalidDataException("Monkey's Audio header is truncated.");

    var hdr = file.AsSpan(headerStart, HeaderBytes);
    var compressionLevel = BinaryPrimitives.ReadUInt16LittleEndian(hdr);
    var formatFlags = BinaryPrimitives.ReadUInt16LittleEndian(hdr[2..]);
    var blocksPerFrame = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..]);
    var finalFrameBlocks = BinaryPrimitives.ReadUInt32LittleEndian(hdr[8..]);
    var totalFrames = BinaryPrimitives.ReadUInt32LittleEndian(hdr[12..]);
    var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(hdr[16..]);
    var channels = BinaryPrimitives.ReadUInt16LittleEndian(hdr[18..]);
    var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(hdr[20..]);
    _ = formatFlags;

    var totalSamples = totalFrames == 0
      ? 0L
      : (long)(totalFrames - 1) * blocksPerFrame + finalFrameBlocks;

    var seekTableStart = headerStart + (int)headerBytes;
    var frameDataStart = seekTableStart + (int)seekTableBytes + (int)headerDataBytes;

    var info = new MonkeysAudioStreamInfo(
      channels, (int)sampleRate, bitsPerSample, totalSamples, compressionLevel, version);
    return new ParsedFile(info, seekTableStart, seekTableBytes, frameDataStart,
      totalFrames, finalFrameBlocks, blocksPerFrame);
  }

  // ── Decode ─────────────────────────────────────────────────────────────────

  /// <summary>Decodes a level-1000 Monkey's Audio stream to raw interleaved
  /// little-endian PCM.</summary>
  public static void Decompress(Stream apeIn, Stream pcmOut) {
    ArgumentNullException.ThrowIfNull(apeIn);
    ArgumentNullException.ThrowIfNull(pcmOut);

    using var ms = new MemoryStream();
    apeIn.CopyTo(ms);
    var file = ms.ToArray();

    var parsed = ParseHeader(file);
    var info = parsed.Info;
    if (info.CompressionLevel != CompressionFast)
      throw new NotSupportedException(
        $"Monkey's Audio compression level {info.CompressionLevel} is not supported (only {CompressionFast} 'fast').");
    if (info.BitsPerSample is not (8 or 16 or 24))
      throw new NotSupportedException($"Unsupported Monkey's Audio bit depth: {info.BitsPerSample}.");
    if (info.Channels is < 1 or > 2)
      throw new NotSupportedException($"Unsupported Monkey's Audio channel count: {info.Channels}.");

    var channels = info.Channels;
    var bps = info.BitsPerSample;
    var bytesPerSample = bps / 8;

    var seekTable = ReadSeekTable(file, parsed.SeekTableStart, parsed.SeekTableBytes, parsed.TotalFrames);

    var outBuf = new byte[info.TotalSamples * channels * bytesPerSample];
    long samplesWritten = 0;

    for (var f = 0; f < parsed.TotalFrames; ++f) {
      var frameStart = parsed.FrameDataStart + (int)(seekTable[f] - seekTable[0]);
      var frameEnd = f + 1 < parsed.TotalFrames
        ? parsed.FrameDataStart + (int)(seekTable[f + 1] - seekTable[0])
        : file.Length;
      if (frameStart < 0 || frameEnd > file.Length || frameEnd < frameStart)
        throw new InvalidDataException("Monkey's Audio frame extends past end of stream.");

      var blocks = f + 1 == parsed.TotalFrames
        ? (int)parsed.FinalFrameBlocks
        : (int)parsed.BlocksPerFrameValue;

      DecodeFrame(file, frameStart, frameEnd - frameStart, blocks, channels, bps,
        outBuf, samplesWritten);
      samplesWritten += blocks;
    }

    pcmOut.Write(outBuf);
  }

  private static void DecodeFrame(
      byte[] file, int offset, int length, int blocks, int channels, int bps,
      byte[] outBuf, long firstSample) {

    var rc = new ApeRangeDecoder(file, offset, length);
    var entropy = new ApeEntropy(channels);
    var predictors = new ApePredictor[channels];
    for (var c = 0; c < channels; ++c) predictors[c] = new ApePredictor();

    var bytesPerSample = bps / 8;
    var ch = new int[channels];

    for (var s = 0; s < blocks; ++s) {
      for (var c = 0; c < channels; ++c) {
        var residual = entropy.Decode(rc, c);
        ch[c] = predictors[c].Decode(residual);
      }

      // Inverse X/Y mid-side: forward stored X = R, Y = L - R as ch[0]=mid,
      // ch[1]=side. Invert the same way the encoder folded.
      if (channels == 2) {
        var mid = ch[0];
        var side = ch[1];
        var right = mid - (side >> 1);
        var left = right + side;
        ch[0] = left;
        ch[1] = right;
      }

      var baseByte = (int)((firstSample + s) * channels * bytesPerSample);
      for (var c = 0; c < channels; ++c)
        WriteSample(outBuf, baseByte + c * bytesPerSample, bps, ch[c]);
    }
  }

  // ── Encode ─────────────────────────────────────────────────────────────────

  /// <summary>Encodes raw interleaved little-endian PCM to a level-1000 Monkey's
  /// Audio stream.</summary>
  public static void Compress(Stream pcmIn, Stream apeOut, int channels, int sampleRate, int bitsPerSample) {
    ArgumentNullException.ThrowIfNull(pcmIn);
    ArgumentNullException.ThrowIfNull(apeOut);
    if (channels is < 1 or > 2)
      throw new ArgumentOutOfRangeException(nameof(channels), "Monkey's Audio encoder supports 1 or 2 channels.");
    if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));
    if (bitsPerSample is not (8 or 16 or 24))
      throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "Monkey's Audio encoder supports 8, 16 or 24 bits per sample.");

    using var ms = new MemoryStream();
    pcmIn.CopyTo(ms);
    var pcm = ms.ToArray();

    var bytesPerSample = bitsPerSample / 8;
    var frameBytes = bytesPerSample * channels;
    if (pcm.Length % frameBytes != 0)
      throw new ArgumentException("PCM length is not a multiple of the frame (channels × bytes-per-sample) size.");
    var totalSamples = pcm.Length / frameBytes;

    var totalFrames = totalSamples == 0 ? 0 : (totalSamples + BlocksPerFrame - 1) / BlocksPerFrame;
    var finalFrameBlocks = totalSamples == 0
      ? 0
      : totalSamples - (totalFrames - 1) * BlocksPerFrame;

    // Encode every frame up front so the seek table carries exact offsets.
    var frames = new List<byte[]>(totalFrames);
    for (var f = 0; f < totalFrames; ++f) {
      var sampleOffset = f * BlocksPerFrame;
      var blocks = (int)Math.Min(BlocksPerFrame, totalSamples - sampleOffset);
      frames.Add(EncodeFrame(pcm, sampleOffset, blocks, channels, bitsPerSample));
    }

    var seekTableBytes = (uint)(totalFrames * 4);
    var frameDataBytes = frames.Sum(fr => (long)fr.Length);

    WriteContainer(apeOut, channels, sampleRate, bitsPerSample,
      (uint)totalFrames, (uint)finalFrameBlocks, seekTableBytes, (ulong)frameDataBytes, frames);
  }

  private static byte[] EncodeFrame(byte[] pcm, int sampleOffset, int blocks, int channels, int bps) {
    var rc = new ApeRangeEncoder();
    var entropy = new ApeEntropy(channels);
    var predictors = new ApePredictor[channels];
    for (var c = 0; c < channels; ++c) predictors[c] = new ApePredictor();

    var bytesPerSample = bps / 8;
    var frameBytes = bytesPerSample * channels;
    var ch = new int[channels];

    for (var s = 0; s < blocks; ++s) {
      var baseByte = (sampleOffset + s) * frameBytes;
      for (var c = 0; c < channels; ++c)
        ch[c] = ReadSample(pcm, baseByte + c * bytesPerSample, bps);

      // Forward X/Y mid-side: side = L - R, mid = R + (side >> 1) — the exact
      // inverse computed on decode.
      if (channels == 2) {
        var left = ch[0];
        var right = ch[1];
        var side = left - right;
        var mid = right + (side >> 1);
        ch[0] = mid;
        ch[1] = side;
      }

      for (var c = 0; c < channels; ++c) {
        var residual = predictors[c].Encode(ch[c]);
        entropy.Encode(rc, c, residual);
      }
    }

    return rc.Finish();
  }

  // ── Container write ───────────────────────────────────────────────────────────

  private static void WriteContainer(
      Stream output, int channels, int sampleRate, int bitsPerSample,
      uint totalFrames, uint finalFrameBlocks, uint seekTableBytes, ulong frameDataBytes,
      IReadOnlyList<byte[]> frames) {

    Span<byte> desc = stackalloc byte[DescriptorBytes];
    desc[0] = (byte)'M'; desc[1] = (byte)'A'; desc[2] = (byte)'C'; desc[3] = (byte)' ';
    BinaryPrimitives.WriteUInt16LittleEndian(desc[4..], VersionWrite);
    BinaryPrimitives.WriteUInt16LittleEndian(desc[6..], 0); // padding
    BinaryPrimitives.WriteUInt32LittleEndian(desc[8..], DescriptorBytes);
    BinaryPrimitives.WriteUInt32LittleEndian(desc[12..], HeaderBytes);
    BinaryPrimitives.WriteUInt32LittleEndian(desc[16..], seekTableBytes);
    BinaryPrimitives.WriteUInt32LittleEndian(desc[20..], 0); // header data (WAV) bytes
    BinaryPrimitives.WriteUInt32LittleEndian(desc[24..], (uint)(frameDataBytes & 0xFFFFFFFF));
    BinaryPrimitives.WriteUInt32LittleEndian(desc[28..], (uint)(frameDataBytes >> 32));
    BinaryPrimitives.WriteUInt32LittleEndian(desc[32..], 0); // terminating bytes
    // desc[36..52] MD5 left zero (advisory; not validated here).
    output.Write(desc);

    Span<byte> hdr = stackalloc byte[HeaderBytes];
    BinaryPrimitives.WriteUInt16LittleEndian(hdr, CompressionFast);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[2..], 0); // format flags
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[4..], BlocksPerFrame);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[8..], finalFrameBlocks);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[12..], totalFrames);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[16..], (ushort)bitsPerSample);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[18..], (ushort)channels);
    BinaryPrimitives.WriteUInt32LittleEndian(hdr[20..], (uint)sampleRate);
    output.Write(hdr);

    // Seek table: absolute byte offset of each frame from the start of the file.
    var frameDataStart = (uint)(DescriptorBytes + HeaderBytes + seekTableBytes);
    Span<byte> seek = stackalloc byte[4];
    var running = frameDataStart;
    foreach (var frame in frames) {
      BinaryPrimitives.WriteUInt32LittleEndian(seek, running);
      output.Write(seek);
      running += (uint)frame.Length;
    }

    foreach (var frame in frames)
      output.Write(frame);
  }

  private static uint[] ReadSeekTable(byte[] file, int start, uint seekTableBytes, uint totalFrames) {
    var entries = (int)(seekTableBytes / 4);
    if (entries < totalFrames || start + (long)totalFrames * 4 > file.Length)
      throw new InvalidDataException("Monkey's Audio seek table is truncated.");
    var table = new uint[totalFrames];
    for (var i = 0; i < totalFrames; ++i)
      table[i] = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(start + i * 4));
    return table;
  }

  // ── Sample I/O ────────────────────────────────────────────────────────────────

  private static int ReadSample(byte[] pcm, int offset, int bps) => bps switch {
    8 => pcm[offset] - 0x80,
    16 => BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset)),
    24 => SignExtend24(pcm[offset] | (pcm[offset + 1] << 8) | (pcm[offset + 2] << 16)),
    _ => throw new NotSupportedException($"Unsupported Monkey's Audio bit depth: {bps}."),
  };

  private static int SignExtend24(int v) => (v & 0x800000) != 0 ? v | unchecked((int)0xFF000000) : v;

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
      default:
        throw new NotSupportedException($"Unsupported Monkey's Audio bit depth: {bps}.");
    }
  }
}
