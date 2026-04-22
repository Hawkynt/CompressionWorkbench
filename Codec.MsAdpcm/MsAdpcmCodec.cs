using System.Buffers.Binary;

namespace Codec.MsAdpcm;

/// <summary>
/// Microsoft ADPCM decoder (WAV format code 0x0002). Block layout:
/// <list type="bullet">
///   <item>Per channel: 1-byte predictor selector (index into the 7-entry coefficient table),
///         2-byte delta (quantization step), 2-byte sample1, 2-byte sample2.</item>
///   <item>Followed by <c>blockAlign - 7*channels</c> bytes of ADPCM nibbles, where each
///         byte packs two samples (high nibble first) that alternate channels when stereo.</item>
/// </list>
/// Predictor coefficients are taken from a standard 7-entry table; the new sample is
/// computed as <c>(s1 * c1 + s2 * c2) >> 8</c> plus a dequantized delta, with the delta
/// itself adapting based on an error table.
/// </summary>
public static class MsAdpcmCodec {

  private static readonly int[] AdaptationTable = [
    230, 230, 230, 230, 307, 409, 512, 614,
    768, 614, 512, 409, 307, 230, 230, 230
  ];

  private static readonly int[] AdaptCoeff1 = [256, 512, 0, 192, 240, 460, 392];
  private static readonly int[] AdaptCoeff2 = [0, -256, 0, 64, 0, -208, -232];

  /// <summary>
  /// Decodes a buffer of MS-ADPCM blocks to per-channel PCM. Each block emits
  /// <c>2 + (blockAlign - 7*channels) * 2 / channels</c> samples per channel.
  /// </summary>
  public static short[][] Decode(ReadOnlySpan<byte> adpcm, int blockAlign, int channels) {
    if (channels is < 1 or > 2)
      throw new ArgumentException("MS ADPCM supports 1 or 2 channels.", nameof(channels));
    var headerBytes = 7 * channels;
    if (blockAlign < headerBytes)
      throw new ArgumentException($"blockAlign {blockAlign} too small for {channels} channel(s).", nameof(blockAlign));

    var samplesPerBlock = 2 + (blockAlign - headerBytes) * 2 / channels;
    var blockCount = adpcm.Length / blockAlign;
    var output = new short[channels][];
    for (var c = 0; c < channels; ++c) output[c] = new short[blockCount * samplesPerBlock];

    Span<int> predIndex = stackalloc int[channels];
    Span<int> delta = stackalloc int[channels];
    Span<int> sample1 = stackalloc int[channels];
    Span<int> sample2 = stackalloc int[channels];

    for (var b = 0; b < blockCount; ++b) {
      var blockStart = b * blockAlign;
      var outStart = b * samplesPerBlock;

      var p = blockStart;
      for (var c = 0; c < channels; ++c) {
        predIndex[c] = adpcm[p++];
        if (predIndex[c] > 6) predIndex[c] = 6;
      }
      for (var c = 0; c < channels; ++c) {
        delta[c] = BinaryPrimitives.ReadInt16LittleEndian(adpcm.Slice(p, 2));
        p += 2;
      }
      for (var c = 0; c < channels; ++c) {
        sample1[c] = BinaryPrimitives.ReadInt16LittleEndian(adpcm.Slice(p, 2));
        p += 2;
      }
      for (var c = 0; c < channels; ++c) {
        sample2[c] = BinaryPrimitives.ReadInt16LittleEndian(adpcm.Slice(p, 2));
        p += 2;
      }

      // First two samples are sample2 then sample1 (reverse storage).
      for (var c = 0; c < channels; ++c) {
        output[c][outStart] = (short)sample2[c];
        output[c][outStart + 1] = (short)sample1[c];
      }

      var dataLen = blockAlign - headerBytes;
      var sampleIdx = outStart + 2;
      for (var i = 0; i < dataLen; ++i) {
        var byteVal = adpcm[p + i];
        // High nibble first → first channel for stereo; low nibble second.
        var n1 = (byteVal >> 4) & 0x0F;
        var n2 = byteVal & 0x0F;

        if (channels == 1) {
          output[0][sampleIdx++] = DecodeNibble(n1, ref predIndex[0], ref delta[0], ref sample1[0], ref sample2[0]);
          output[0][sampleIdx++] = DecodeNibble(n2, ref predIndex[0], ref delta[0], ref sample1[0], ref sample2[0]);
        } else {
          var leftOut = DecodeNibble(n1, ref predIndex[0], ref delta[0], ref sample1[0], ref sample2[0]);
          var rightOut = DecodeNibble(n2, ref predIndex[1], ref delta[1], ref sample1[1], ref sample2[1]);
          output[0][sampleIdx] = leftOut;
          output[1][sampleIdx] = rightOut;
          ++sampleIdx;
        }
      }
    }
    return output;
  }

  private static short DecodeNibble(int nibble, ref int predIndex, ref int delta, ref int sample1, ref int sample2) {
    // Sign-extend 4-bit nibble.
    var signed = nibble < 8 ? nibble : nibble - 16;
    var predicted = (sample1 * AdaptCoeff1[predIndex] + sample2 * AdaptCoeff2[predIndex]) >> 8;
    predicted += signed * delta;
    if (predicted > 32767) predicted = 32767;
    else if (predicted < -32768) predicted = -32768;
    sample2 = sample1;
    sample1 = predicted;
    delta = AdaptationTable[nibble] * delta >> 8;
    if (delta < 16) delta = 16;
    return (short)predicted;
  }
}
