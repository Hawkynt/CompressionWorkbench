#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Codec.MonkeysAudio;

/// <summary>
/// Monkey's Audio (.ape) lossless codec — a reference-faithful encoder and decoder
/// for the version 3.99 (v3990) container and bitstream. The on-disk layout is the
/// v3.98+ <c>APE_DESCRIPTOR</c> + 24-byte <c>APE_HEADER</c> + u32-per-frame seek
/// table + range-coded frame data, exactly as the reference SDK writes it. PCM in
/// and out is raw interleaved little-endian signed integers.
/// <para>
/// The decode path is a byte-exact port of the reference SDK's <c>CUnBitArray</c>
/// range coder, <c>DecodeValueRange</c> entropy stage (v3990), the order-4 dual
/// cross-channel predictor (<c>CPredictorDecompress3950toCurrent</c>) and the
/// level-dependent <c>CNNFilter</c> cascade, plus the X/Y → L/R decorrelation and
/// 8/16/24-bit sample reconstruction (<c>CPrepare::Unprepare</c>) and the per-frame
/// CRC32 / special-frame (silence, pseudo-stereo) handling. It is the same machinery
/// ffmpeg's <c>libavcodec/apedec.c</c> implements, so it decodes real reference- or
/// ffmpeg-produced files of compression levels 1000–5000 (verified byte-exact against
/// ffmpeg). The container parser requires the v3.98+ <c>APE_DESCRIPTOR</c> layout, so
/// files older than v3980 are rejected even though the bitstream port itself covers
/// the v3.95+ predictor.
/// </para>
/// <para>
/// The encode path is the exact forward inverse — the SDK's <c>CBitArray</c> range
/// coder, <c>EncodeValue</c>, <c>CPredictorCompressNormal</c> and <c>CPrepare</c>
/// — so a stream this codec writes is the byte-stream the reference encoder would
/// produce for the same input at the same level and round-trips losslessly through
/// the reference decoder (this one or ffmpeg). The encoder emits levels 1000–4000
/// (the level-5000 "insane" filter cascade decodes but is not used for encoding).
/// </para>
/// <para>
/// Pre-3.95 (&lt; 3950) files use older entropy/predictor variants this port does not
/// implement and are rejected with <see cref="NotSupportedException"/> so container
/// descriptors fall back gracefully.
/// </para>
/// </summary>
public static class MonkeysAudioCodec {

  private const int VersionWrite = 3990;
  private const int DescriptorBytes = 52;
  private const int HeaderBytes = 24;
  private const int BlocksPerFrame = 73728;

  // MAC_FORMAT_FLAG_CRC: we always emit the CRC32 + special-frame machinery.
  private const int FormatFlagsWrite = 0;

  public const int CompressionFast = 1000;
  public const int CompressionNormal = 2000;
  public const int CompressionHigh = 3000;
  public const int CompressionExtraHigh = 4000;
  public const int CompressionInsane = 5000;

  // Special-frame codes (Prepare.h).
  private const int SpecialMonoSilence = 1;
  private const int SpecialLeftSilence = 1;
  private const int SpecialRightSilence = 2;
  private const int SpecialPseudoStereo = 4;

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
      throw new NotSupportedException($"Monkey's Audio version {version} (pre-3.98 descriptor) is not supported.");

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

  /// <summary>Decodes a Monkey's Audio v3.95+ stream to raw interleaved
  /// little-endian PCM.</summary>
  public static void Decompress(Stream apeIn, Stream pcmOut) {
    ArgumentNullException.ThrowIfNull(apeIn);
    ArgumentNullException.ThrowIfNull(pcmOut);

    using var ms = new MemoryStream();
    apeIn.CopyTo(ms);
    var file = ms.ToArray();

    var parsed = ParseHeader(file);
    var info = parsed.Info;
    if (info.Version < 3950)
      throw new NotSupportedException(
        $"Monkey's Audio version {info.Version} (pre-3.95 predictor) is not supported.");
    if (info.CompressionLevel is not (CompressionFast or CompressionNormal
        or CompressionHigh or CompressionExtraHigh or CompressionInsane)
        || info.CompressionLevel % 1000 != 0)
      throw new NotSupportedException(
        $"Unsupported Monkey's Audio compression level: {info.CompressionLevel}.");
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
      var frameStart = (int)seekTable[f];
      var frameEnd = f + 1 < parsed.TotalFrames ? (int)seekTable[f + 1] : file.Length;
      if (frameStart < 0 || frameEnd > file.Length || frameEnd < frameStart)
        throw new InvalidDataException("Monkey's Audio frame extends past end of stream.");

      var blocks = f + 1 == parsed.TotalFrames
        ? (int)parsed.FinalFrameBlocks
        : (int)parsed.BlocksPerFrameValue;

      DecodeFrame(file, frameStart, frameEnd - frameStart, blocks, channels, bps,
        info.CompressionLevel, outBuf, samplesWritten);
      samplesWritten += blocks;
    }

    pcmOut.Write(outBuf);
  }

  private static void DecodeFrame(
      byte[] file, int offset, int length, int blocks, int channels, int bps, int level,
      byte[] outBuf, long firstSample) {

    var rc = new ApeRangeDecoder(file, offset, length);

    // StartFrame: CRC (raw 32 bits), then special codes if CRC bit31 set.
    var storedCrc = rc.DecodeUnsignedInt();
    var specialCodes = 0u;
    if ((storedCrc & 0x80000000) != 0)
      specialCodes = rc.DecodeUnsignedInt();
    // storedCrc &= 0x7FFFFFFF; // (CRC not verified here)

    var entropy = new ApeEntropy(channels);
    entropy.FlushStates();
    rc.StartDecoding();

    var predictorX = new ApePredictorDecompress(level);
    var predictorY = channels == 2 ? new ApePredictorDecompress(level) : null;

    var bytesPerSample = bps / 8;

    if (channels == 2) {
      var leftSilence = (specialCodes & SpecialLeftSilence) != 0;
      var rightSilence = (specialCodes & SpecialRightSilence) != 0;
      var lastX = 0;

      for (var s = 0; s < blocks; ++s) {
        int x, y;
        if (leftSilence && rightSilence) {
          x = 0; y = 0;
        } else if ((specialCodes & SpecialPseudoStereo) != 0) {
          x = predictorX.DecompressValue(entropy.Decode(rc, 1), 0);
          y = 0;
        } else {
          var ny = entropy.Decode(rc, 0);
          var nx = entropy.Decode(rc, 1);
          y = predictorY!.DecompressValue(ny, lastX);
          x = predictorX.DecompressValue(nx, y);
          lastX = x;
        }

        // Unprepare X/Y -> R/L (CPrepare::Unprepare): R = X - Y/2, L = R + Y.
        var r = x - (y / 2);
        var l = r + y;
        var baseByte = (int)((firstSample + s) * channels * bytesPerSample);
        WriteSample(outBuf, baseByte, bps, r);
        WriteSample(outBuf, baseByte + bytesPerSample, bps, l);
      }
    } else {
      var monoSilence = (specialCodes & SpecialMonoSilence) != 0;
      for (var s = 0; s < blocks; ++s) {
        var x = monoSilence ? 0 : predictorX.DecompressValue(entropy.Decode(rc, 0), 0);
        var baseByte = (int)((firstSample + s) * bytesPerSample);
        WriteSample(outBuf, baseByte, bps, x);
      }
    }
  }

  // ── Encode ─────────────────────────────────────────────────────────────────

  /// <summary>Encodes raw interleaved little-endian PCM to a Monkey's Audio stream
  /// at the given compression level (default <see cref="CompressionFast"/>).</summary>
  public static void Compress(Stream pcmIn, Stream apeOut, int channels, int sampleRate, int bitsPerSample,
      int compressionLevel = CompressionFast) {
    ArgumentNullException.ThrowIfNull(pcmIn);
    ArgumentNullException.ThrowIfNull(apeOut);
    if (channels is < 1 or > 2)
      throw new ArgumentOutOfRangeException(nameof(channels), "Monkey's Audio encoder supports 1 or 2 channels.");
    if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));
    if (bitsPerSample is not (8 or 16 or 24))
      throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "Monkey's Audio encoder supports 8, 16 or 24 bits per sample.");
    if (compressionLevel is not (CompressionFast or CompressionNormal or CompressionHigh or CompressionExtraHigh))
      throw new ArgumentOutOfRangeException(nameof(compressionLevel),
        "Monkey's Audio encoder supports levels 1000, 2000, 3000 or 4000.");

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
      frames.Add(EncodeFrame(pcm, sampleOffset, blocks, channels, bitsPerSample, compressionLevel));
    }

    var seekTableBytes = (uint)(totalFrames * 4);
    var frameDataBytes = frames.Sum(fr => (long)fr.Length);

    WriteContainer(apeOut, channels, sampleRate, bitsPerSample, compressionLevel,
      (uint)totalFrames, (uint)finalFrameBlocks, seekTableBytes, (ulong)frameDataBytes, frames);
  }

  private static byte[] EncodeFrame(byte[] pcm, int sampleOffset, int blocks, int channels, int bps, int level) {
    // Forward channel decorrelation + CRC + special codes (CPrepare::Prepare).
    var x = new int[blocks];
    var y = new int[blocks];
    var specialCodes = Prepare(pcm, sampleOffset, blocks, channels, bps, x, y, out var crc);

    var rc = new ApeRangeEncoder();
    rc.EncodeUnsignedInt(crc);
    if ((crc & 0x80000000) != 0)
      rc.EncodeUnsignedInt((uint)specialCodes);

    var entropy = new ApeEntropy(channels);
    entropy.FlushStates();
    rc.FlushBitArray();

    var predictorX = new ApePredictorCompress(level);
    var predictorY = channels == 2 ? new ApePredictorCompress(level) : null;

    if (channels == 2) {
      var leftSilence = (specialCodes & SpecialLeftSilence) != 0;
      var rightSilence = (specialCodes & SpecialRightSilence) != 0;
      var pseudo = (specialCodes & SpecialPseudoStereo) != 0;
      var lastX = 0;
      if (!(leftSilence && rightSilence)) {
        for (var s = 0; s < blocks; ++s) {
          if (pseudo) {
            entropy.Encode(rc, 1, predictorX.CompressValue(x[s], 0));
          } else {
            entropy.Encode(rc, 0, predictorY!.CompressValue(y[s], lastX));
            entropy.Encode(rc, 1, predictorX.CompressValue(x[s], y[s]));
            lastX = x[s];
          }
        }
      }
    } else {
      var monoSilence = (specialCodes & SpecialMonoSilence) != 0;
      if (!monoSilence)
        for (var s = 0; s < blocks; ++s)
          entropy.Encode(rc, 0, predictorX.CompressValue(x[s], 0));
    }

    rc.FinalizeStream();
    return rc.ToArray();
  }

  // ── Channel decorrelation + CRC (CPrepare) ─────────────────────────────────

  private static int Prepare(byte[] pcm, int sampleOffset, int blocks, int channels, int bps,
      int[] x, int[] y, out uint crc) {
    var c = 0xFFFFFFFFu;
    var specialCodes = 0;
    var bytesPerSample = bps / 8;
    var frameBytes = bytesPerSample * channels;

    if (channels == 2) {
      var lPeak = 0;
      var rPeak = 0;
      for (var i = 0; i < blocks; ++i) {
        var baseByte = (sampleOffset + i) * frameBytes;
        var r = ReadSample(pcm, baseByte, bps);
        var l = ReadSample(pcm, baseByte + bytesPerSample, bps);
        c = UpdateCrcSample(c, pcm, baseByte, bps);
        c = UpdateCrcSample(c, pcm, baseByte + bytesPerSample, bps);
        if (Math.Abs(l) > lPeak) lPeak = Math.Abs(l);
        if (Math.Abs(r) > rPeak) rPeak = Math.Abs(r);
        y[i] = l - r;
        x[i] = r + (y[i] / 2);
      }
      if (lPeak == 0) specialCodes |= SpecialLeftSilence;
      if (rPeak == 0) specialCodes |= SpecialRightSilence;
      // Pseudo-stereo: all Y are zero (left == right everywhere).
      if (!(lPeak == 0 && rPeak == 0)) {
        var allZero = true;
        for (var i = 0; i < blocks; ++i)
          if (y[i] != 0) { allZero = false; break; }
        if (allZero && blocks > 0) specialCodes |= SpecialPseudoStereo;
      }
    } else {
      var peak = 0;
      for (var i = 0; i < blocks; ++i) {
        var baseByte = (sampleOffset + i) * frameBytes;
        var r = ReadSample(pcm, baseByte, bps);
        c = UpdateCrcSample(c, pcm, baseByte, bps);
        if (Math.Abs(r) > peak) peak = Math.Abs(r);
        x[i] = r;
      }
      if (peak == 0) specialCodes |= SpecialMonoSilence;
    }

    c ^= 0xFFFFFFFF;
    c >>= 1;
    if (specialCodes != 0)
      c |= 1u << 31;
    crc = c;
    return specialCodes;
  }

  private static uint UpdateCrcSample(uint crc, byte[] pcm, int offset, int bps) {
    var n = bps / 8;
    for (var i = 0; i < n; ++i)
      crc = (crc >> 8) ^ Crc32Table[(crc & 0xFF) ^ pcm[offset + i]];
    return crc;
  }

  // ── Container write ───────────────────────────────────────────────────────────

  private static void WriteContainer(
      Stream output, int channels, int sampleRate, int bitsPerSample, int compressionLevel,
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
    BinaryPrimitives.WriteUInt16LittleEndian(hdr, (ushort)compressionLevel);
    BinaryPrimitives.WriteUInt16LittleEndian(hdr[2..], FormatFlagsWrite); // format flags
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

  // CPrepare::CRC32_TABLE (standard reflected CRC-32 / zlib polynomial).
  private static readonly uint[] Crc32Table = BuildCrc32Table();

  private static uint[] BuildCrc32Table() {
    var table = new uint[256];
    for (var n = 0u; n < 256; ++n) {
      var c = n;
      for (var k = 0; k < 8; ++k)
        c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
      table[n] = c;
    }
    return table;
  }
}
