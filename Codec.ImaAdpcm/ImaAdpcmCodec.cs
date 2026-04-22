using System.Buffers.Binary;

namespace Codec.ImaAdpcm;

/// <summary>
/// IMA ADPCM (Interactive Multimedia Association Adaptive Differential PCM) decoder.
/// Each 4-bit nibble encodes the magnitude + sign of the delta between samples; the
/// step size walks up and down a 89-entry log-spaced table based on the previous
/// nibble. Used by WAV format code 0x0011 with a block layout:
/// <list type="bullet">
///   <item>Per-channel 4-byte header: int16 predictor, int8 step-index, 1 reserved byte.</item>
///   <item>For mono: remaining <c>blockAlign - 4</c> bytes are nibble pairs (LSN first).</item>
///   <item>For stereo: headers are interleaved per channel (4 bytes L, 4 bytes R), then
///         nibbles are interleaved 4 bytes per channel (8 samples each).</item>
/// </list>
/// </summary>
public static class ImaAdpcmCodec {

  private static readonly int[] StepTable = [
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
    34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
    157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658,
    724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024,
    3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
    15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
  ];

  private static readonly int[] IndexAdjust = [-1, -1, -1, -1, 2, 4, 6, 8];

  /// <summary>
  /// Decodes IMA ADPCM data to one PCM buffer per channel. Each output buffer holds
  /// <c>((blockAlign/channels - 4) * 2 + 1)</c> samples per block.
  /// </summary>
  public static short[][] Decode(ReadOnlySpan<byte> adpcm, int blockAlign, int channels) {
    if (channels is < 1 or > 2)
      throw new ArgumentException("IMA ADPCM in WAV supports 1 or 2 channels.", nameof(channels));
    if (blockAlign < 4 * channels)
      throw new ArgumentException($"blockAlign {blockAlign} too small for {channels} channel(s).", nameof(blockAlign));

    var samplesPerBlock = (blockAlign - 4 * channels) * 2 / channels + 1;
    var blockCount = adpcm.Length / blockAlign;
    var output = new short[channels][];
    for (var c = 0; c < channels; ++c) output[c] = new short[blockCount * samplesPerBlock];

    Span<int> predictor = stackalloc int[channels];
    Span<int> index = stackalloc int[channels];

    for (var b = 0; b < blockCount; ++b) {
      var blockStart = b * blockAlign;
      var outStart = b * samplesPerBlock;

      // Per-channel 4-byte headers at block start.
      for (var c = 0; c < channels; ++c) {
        var h = blockStart + c * 4;
        predictor[c] = BinaryPrimitives.ReadInt16LittleEndian(adpcm.Slice(h, 2));
        index[c] = adpcm[h + 2];
        if (index[c] > 88) index[c] = 88;
        if (index[c] < 0) index[c] = 0;
        output[c][outStart] = (short)predictor[c];
      }

      var dataStart = blockStart + 4 * channels;
      var dataLen = blockAlign - 4 * channels;

      if (channels == 1) {
        for (var i = 0; i < dataLen; ++i) {
          var byteVal = adpcm[dataStart + i];
          output[0][outStart + 1 + i * 2] = DecodeNibble((byte)(byteVal & 0x0F), ref predictor[0], ref index[0]);
          output[0][outStart + 2 + i * 2] = DecodeNibble((byte)(byteVal >> 4), ref predictor[0], ref index[0]);
        }
      } else {
        // Stereo: 4-byte groups alternate channels (L,R,L,R…). Each group yields 8 samples.
        var groups = dataLen / 8;
        for (var g = 0; g < groups; ++g) {
          for (var c = 0; c < 2; ++c) {
            var gs = dataStart + g * 8 + c * 4;
            for (var i = 0; i < 4; ++i) {
              var byteVal = adpcm[gs + i];
              var sampleIdx = outStart + 1 + g * 8 + i * 2;
              output[c][sampleIdx] = DecodeNibble((byte)(byteVal & 0x0F), ref predictor[c], ref index[c]);
              output[c][sampleIdx + 1] = DecodeNibble((byte)(byteVal >> 4), ref predictor[c], ref index[c]);
            }
          }
        }
      }
    }
    return output;
  }

  private static short DecodeNibble(byte nibble, ref int predictor, ref int index) {
    var step = StepTable[index];
    var diff = step >> 3;
    if ((nibble & 1) != 0) diff += step >> 2;
    if ((nibble & 2) != 0) diff += step >> 1;
    if ((nibble & 4) != 0) diff += step;
    if ((nibble & 8) != 0) predictor -= diff;
    else predictor += diff;
    if (predictor > 32767) predictor = 32767;
    else if (predictor < -32768) predictor = -32768;
    index += IndexAdjust[nibble & 0x07];
    if (index < 0) index = 0;
    else if (index > 88) index = 88;
    return (short)predictor;
  }
}
