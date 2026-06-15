#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace Codec.CriAdx;

/// <summary>
/// CRI ADX ADPCM encoder and decoder. ADX is a fixed-block ADPCM stream used by many
/// console games (CRI Middleware). The container is a big-endian header followed by
/// interleaved per-channel frames:
/// <list type="bullet">
///   <item>Header: <c>u16 magic 0x8000 | u16 copyrightOffset</c> — coded sample data
///     starts at <c>copyrightOffset + 4</c>, and the six bytes ending at
///     <c>copyrightOffset - 2</c> hold the ASCII string <c>"(c)CRI"</c>.</item>
///   <item><c>u8 encodingType</c> (3 = standard ADX, the only type decoded here),
///     <c>u8 blockSize</c> (18), <c>u8 bitDepth</c> (4), <c>u8 channelCount</c>,
///     <c>u32 sampleRate</c>, <c>u32 totalSamples</c>, <c>u16 highpassFrequency</c>,
///     <c>u8 version</c> (3 or 4), <c>u8 flags</c> (0x08 = encrypted).</item>
/// </list>
/// Each 18-byte frame carries a big-endian <c>u16</c> scale followed by 32 signed
/// 4-bit nibbles (high nibble first), one per sample. A sample is reconstructed as
/// <c>predicted + signExtend4(nibble) * scale</c>, where
/// <c>predicted = (coef1 * hist1 + coef2 * hist2) &gt;&gt; 12</c> and the two predictor
/// coefficients are derived once from the high-pass cutoff. A scale word of
/// <c>0x8001</c> marks an end-of-stream padding frame.
/// </summary>
public static class AdxCodec {

  /// <summary>ADX header magic word (big-endian): high bit set, low 15 bits = copyright offset.</summary>
  public const ushort Magic = 0x8000;

  /// <summary>Standard ADX ADPCM encoding type (the only type this codec encodes/decodes).</summary>
  public const byte EncodingTypeStandard = 3;

  /// <summary>AHX encoding type (MPEG-2 Layer II payload), version 10 — decoded by the MP3 path, not this codec.</summary>
  public const byte EncodingTypeAhx = 0x10;

  /// <summary>AHX encoding type (MPEG-2 Layer II payload), version 11 — decoded by the MP3 path, not this codec.</summary>
  public const byte EncodingTypeAhx11 = 0x11;

  /// <summary>Bytes per ADX frame (1 channel): a 2-byte scale plus 32 4-bit nibbles.</summary>
  public const int FrameSize = 18;

  /// <summary>PCM samples carried by one 18-byte frame.</summary>
  public const int SamplesPerFrame = 32;

  /// <summary>ADPCM nibble bit depth.</summary>
  public const byte BitDepth = 4;

  /// <summary>End-of-stream marker carried in a frame's scale word.</summary>
  public const ushort EndMarkerScale = 0x8001;

  private const double Sqrt2 = 1.4142135623730951;

  /// <summary>Parsed ADX header fields plus where the coded sample data begins.</summary>
  public readonly record struct AdxInfo(
    byte EncodingType, int BlockSize, int BitDepth, int Channels,
    int SampleRate, int TotalSamples, int HighpassFrequency, int Version,
    byte Flags, int DataOffset) {

    /// <summary>True when the stream is flagged encrypted (flag bit 0x08) — not decodable here.</summary>
    public bool IsEncrypted => (this.Flags & 0x08) != 0;

    /// <summary>True for standard ADX ADPCM (encoding type 3) — the only decodable form.</summary>
    public bool IsStandard => this.EncodingType == EncodingTypeStandard;

    /// <summary>
    /// True for AHX streams (encoding type 0x10 / 0x11): the payload after the header is an
    /// MPEG-2 Layer II (22.05 kHz mono) elementary stream rather than ADX ADPCM. AHX is
    /// decoded via the MP3 codec at the container layer, not by <see cref="AdxCodec"/>.
    /// </summary>
    public bool IsAhx => this.EncodingType is EncodingTypeAhx or EncodingTypeAhx11;
  }

  /// <summary>
  /// Derives the two fixed-point predictor coefficients from a high-pass cutoff and
  /// sample rate. The prediction term is <c>(coef1*h1 + coef2*h2) &gt;&gt; 12</c>, so the
  /// coefficients use the standard 8192 / 4096 fixed-point scaling.
  /// </summary>
  public static (int Coef1, int Coef2) DeriveCoefficients(int highpassFrequency, int sampleRate) {
    var z = Math.Cos(2.0 * Math.PI * highpassFrequency / sampleRate);
    var a = Sqrt2 - z;
    var b = Sqrt2 - 1.0;
    var c = (a - Math.Sqrt((a + b) * (a - b))) / b;
    var coef1 = (int)Math.Floor(c * 8192.0);
    var coef2 = (int)Math.Floor(-(c * c) * 4096.0);
    return (coef1, coef2);
  }

  /// <summary>Reads and validates an ADX header from the start of <paramref name="file"/>.</summary>
  public static AdxInfo ReadInfo(ReadOnlySpan<byte> file) {
    if (file.Length < 20)
      throw new InvalidDataException("ADX file too short for a header.");

    var magic = BinaryPrimitives.ReadUInt16BigEndian(file);
    if ((magic & 0x8000) == 0)
      throw new InvalidDataException("Missing ADX magic (high bit of word 0).");

    var copyrightOffset = (int)(magic & 0x7FFF);
    var dataOffset = copyrightOffset + 4;

    var encodingType = file[4];
    var blockSize = file[5];
    var bitDepth = file[6];
    var channels = file[7];
    var sampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(file[8..]);
    var totalSamples = (int)BinaryPrimitives.ReadUInt32BigEndian(file[12..]);
    var highpass = BinaryPrimitives.ReadUInt16BigEndian(file[16..]);
    var version = file[18];
    var flags = file[19];

    if (channels < 1)
      throw new InvalidDataException("ADX header reports zero channels.");

    return new AdxInfo(encodingType, blockSize, bitDepth, channels,
      sampleRate, totalSamples, highpass, version, flags, dataOffset);
  }

  /// <summary>
  /// Decodes a complete standard ADX file to interleaved 16-bit PCM. Throws
  /// <see cref="NotSupportedException"/> for encrypted streams or non-standard encoding
  /// types, which the container layer treats as a FULL-only fallback.
  /// </summary>
  public static (short[] InterleavedPcm, int Channels, int SampleRate) Decode(ReadOnlySpan<byte> file) {
    var info = ReadInfo(file);
    if (!info.IsStandard)
      throw new NotSupportedException($"Unsupported ADX encoding type {info.EncodingType}.");
    if (info.IsEncrypted)
      throw new NotSupportedException("Encrypted ADX streams are not supported.");

    var sampleRate = info.SampleRate <= 0 ? 44100 : info.SampleRate;
    var (coef1, coef2) = DeriveCoefficients(
      info.HighpassFrequency <= 0 ? 500 : info.HighpassFrequency, sampleRate);

    var channels = info.Channels;
    var totalSamples = info.TotalSamples;
    var data = file[info.DataOffset..];

    var pcm = new short[totalSamples * channels];
    var hist1 = new int[channels];
    var hist2 = new int[channels];

    // Frames interleave per channel: ch0 frame, ch1 frame, ... then repeat.
    var framePitch = FrameSize * channels;
    var frameGroups = totalSamples == 0 ? 0 : (totalSamples + SamplesPerFrame - 1) / SamplesPerFrame;

    for (var group = 0; group < frameGroups; ++group) {
      var groupStart = group * framePitch;
      var samplesDone = group * SamplesPerFrame;
      var samplesThisGroup = Math.Min(SamplesPerFrame, totalSamples - samplesDone);

      for (var ch = 0; ch < channels; ++ch) {
        var frameStart = groupStart + ch * FrameSize;
        if (frameStart + FrameSize > data.Length)
          break;

        var scale = BinaryPrimitives.ReadUInt16BigEndian(data[frameStart..]);
        if (scale == EndMarkerScale)
          continue; // padding frame — leaves history untouched, emits no fresh deltas

        var h1 = hist1[ch];
        var h2 = hist2[ch];

        for (var i = 0; i < samplesThisGroup; ++i) {
          var nibbleByte = data[frameStart + 2 + (i >> 1)];
          var nibble = (i & 1) == 0 ? (nibbleByte >> 4) & 0x0F : nibbleByte & 0x0F;
          var delta = SignExtend4(nibble);

          var predicted = (coef1 * h1 + coef2 * h2) >> 12;
          var sample = Clamp16(predicted + delta * scale);

          pcm[(samplesDone + i) * channels + ch] = (short)sample;
          h2 = h1;
          h1 = sample;
        }

        hist1[ch] = h1;
        hist2[ch] = h2;
      }
    }

    return (pcm, channels, sampleRate);
  }

  /// <summary>
  /// Encodes interleaved 16-bit PCM into a complete standard ADX file (version 3,
  /// encoding type 3, high-pass 500 Hz). The encoder reconstructs each sample exactly
  /// as <see cref="Decode"/> will, so the two stay bit-exact for the chosen scale
  /// convention.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> interleaved, int channels, int sampleRate) {
    if (channels < 1)
      throw new ArgumentOutOfRangeException(nameof(channels), "Channel count must be at least 1.");
    if (sampleRate <= 0)
      throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");
    if (interleaved.Length % channels != 0)
      throw new ArgumentException("Interleaved sample count is not a multiple of the channel count.");

    const int highpass = 500;
    var totalSamples = interleaved.Length / channels;
    var (coef1, coef2) = DeriveCoefficients(highpass, sampleRate);

    // Header layout: bytes 0..19 are the fixed fields (version @18, flags @19); the
    // six-byte "(c)CRI" string sits at copyrightOffset - 2 and must follow those
    // fields, so the smallest legal copyright offset is 22 (string @20..25), which
    // puts the coded sample data at copyrightOffset + 4 == 26.
    const int copyrightOffset = 22;            // points two bytes past the "(c)CRI" string
    const int dataOffset = copyrightOffset + 4; // == 26
    var header = new byte[dataOffset];

    BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)(Magic | copyrightOffset));
    header[4] = EncodingTypeStandard;
    header[5] = FrameSize;
    header[6] = BitDepth;
    header[7] = (byte)channels;
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)sampleRate);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), (uint)totalSamples);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(16), (ushort)highpass);
    header[18] = 3;    // version
    header[19] = 0x00; // flags (not encrypted)
    // "(c)CRI" occupies the six bytes ending at copyrightOffset - 2 == bytes 0x12..0x17.
    Encoding.ASCII.GetBytes("(c)CRI").CopyTo(header.AsSpan(copyrightOffset - 2));

    var frameGroups = totalSamples == 0 ? 0 : (totalSamples + SamplesPerFrame - 1) / SamplesPerFrame;
    var data = new byte[frameGroups * FrameSize * channels];

    var hist1 = new int[channels];
    var hist2 = new int[channels];

    for (var group = 0; group < frameGroups; ++group) {
      var samplesDone = group * SamplesPerFrame;
      var samplesThisGroup = Math.Min(SamplesPerFrame, totalSamples - samplesDone);

      for (var ch = 0; ch < channels; ++ch) {
        var frameStart = (group * channels + ch) * FrameSize;

        // Pick the smallest scale that keeps every residual within the signed-4-bit
        // range, then quantise; reconstruct so history matches the decoder exactly.
        var h1 = hist1[ch];
        var h2 = hist2[ch];

        var scale = ChooseScale(interleaved, channels, ch, samplesDone, samplesThisGroup, coef1, coef2, h1, h2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(frameStart), (ushort)scale);

        for (var i = 0; i < SamplesPerFrame; ++i) {
          var nibble = 0;
          if (i < samplesThisGroup) {
            var target = interleaved[(samplesDone + i) * channels + ch];
            var predicted = (coef1 * h1 + coef2 * h2) >> 12;
            var residual = target - predicted;

            var quant = (int)Math.Round((double)residual / scale, MidpointRounding.AwayFromZero);
            if (quant > 7) quant = 7;
            else if (quant < -8) quant = -8;

            var sample = Clamp16(predicted + quant * scale);
            h2 = h1;
            h1 = sample;
            nibble = quant & 0x0F;
          }

          var byteIndex = frameStart + 2 + (i >> 1);
          if ((i & 1) == 0)
            data[byteIndex] = (byte)(nibble << 4);
          else
            data[byteIndex] |= (byte)nibble;
        }

        hist1[ch] = h1;
        hist2[ch] = h2;
      }
    }

    var file = new byte[header.Length + data.Length];
    header.CopyTo(file.AsSpan());
    data.CopyTo(file.AsSpan(header.Length));
    return file;
  }

  // Smallest scale (>= 1) keeping every residual within the signed-4-bit range. The
  // running history mirrors the decoder, walked here under the assumption of a
  // near-lossless reconstruction so the chosen scale bounds the real residuals.
  private static int ChooseScale(
      ReadOnlySpan<short> interleaved, int channels, int ch,
      int samplesDone, int samplesThisGroup, int coef1, int coef2, int h1, int h2) {
    var maxResidual = 0;
    for (var i = 0; i < samplesThisGroup; ++i) {
      var target = interleaved[(samplesDone + i) * channels + ch];
      var predicted = (coef1 * h1 + coef2 * h2) >> 12;
      maxResidual = Math.Max(maxResidual, Math.Abs(target - predicted));

      h2 = h1;
      h1 = target; // optimistic: assume the quantiser reproduces the target sample
    }

    // The quantiser rounds residual/scale into [-8, 7]; dividing the largest residual
    // by 7 keeps the positive side in range, and -8 covers the negative side.
    var scale = (maxResidual + 6) / 7;
    return scale < 1 ? 1 : scale > 0x7FFF ? 0x7FFF : scale;
  }

  private static int SignExtend4(int nibble) => (nibble & 0x08) != 0 ? nibble - 16 : nibble;

  private static int Clamp16(int value) => value > 32767 ? 32767 : value < -32768 ? -32768 : value;
}
