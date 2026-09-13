#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.AdpcmX;

/// <summary>
/// Duck DK4 IMA ADPCM (ffmpeg <c>adpcm_ima_dk4</c>). Unlike DK3 the channels are plain IMA;
/// each block opens with a 4-byte header <em>per channel</em> — a little-endian 16-bit start
/// predictor and a little-endian 16-bit start step index — and that start predictor is also
/// emitted as the block's first sample for that channel. The remaining bytes are nibble pairs:
/// the high nibble feeds channel 0, the low nibble feeds the last channel (so for mono both
/// nibbles feed channel 0, for stereo high→L, low→R). All expands use the DK shift of 3.
/// </summary>
public static class ImaDk4 {

  /// <summary>
  /// Decodes one DK4 block into interleaved PCM16. <paramref name="channels"/> is 1 or 2.
  /// The per-channel start predictors are emitted as the leading samples (matching ffmpeg).
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> block, int channels) {
    if (channels is < 1 or > 2)
      throw new ArgumentException("DK4 supports 1 or 2 channels.", nameof(channels));
    var headerSize = 4 * channels;
    if (block.Length < headerSize)
      throw new ArgumentException("DK4 block shorter than its per-channel headers.", nameof(block));

    Span<int> predictor = stackalloc int[channels];
    Span<int> index = stackalloc int[channels];
    var output = new List<short>();

    for (var c = 0; c < channels; ++c) {
      predictor[c] = (short)BinaryPrimitives.ReadUInt16LittleEndian(block[(c * 4)..]);
      index[c] = (short)BinaryPrimitives.ReadUInt16LittleEndian(block[(c * 4 + 2)..]);
      if (index[c] > 88) index[c] = 88;
      else if (index[c] < 0) index[c] = 0;
      output.Add((short)predictor[c]); // start predictor doubles as the first sample
    }

    var last = channels - 1;
    for (var pos = headerSize; pos < block.Length; ++pos) {
      var b = block[pos];
      output.Add(ImaCore.Expand(b >> 4, ref predictor[0], ref index[0], 3));
      output.Add(ImaCore.Expand(b & 0x0F, ref predictor[last], ref index[last], 3));
    }

    return [.. output];
  }
}
