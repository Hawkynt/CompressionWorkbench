#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.AdpcmX;

/// <summary>
/// 4X Technologies ADPCM (ffmpeg <c>adpcm_4xm</c>, the audio in <c>.4xm</c> movies). It is plain
/// IMA with a per-block, per-channel header and the channels stored as separate contiguous runs
/// (not nibble-interleaved). Each block begins with one little-endian 16-bit start predictor per
/// channel followed by one little-endian 16-bit start step index per channel (the index is clamped
/// to 0..88). The remaining bytes are split evenly between the channels; within a channel's run the
/// LOW nibble of each byte is decoded first. Expands use the 4XM shift of 4.
/// </summary>
public static class FourXm {

  /// <summary>
  /// Decodes one 4XM block into interleaved PCM16. <paramref name="channels"/> is 1 or 2; the
  /// payload after the header is divided equally between the channels.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> block, int channels) {
    if (channels is < 1 or > 2)
      throw new ArgumentException("4XM supports 1 or 2 channels.", nameof(channels));
    var headerSize = 4 * channels; // predictor[ch] then step-index[ch], 2 bytes each
    if (block.Length < headerSize)
      throw new ArgumentException("4XM block shorter than its header.", nameof(block));

    var predictor = new int[channels];
    var index = new int[channels];
    for (var c = 0; c < channels; ++c)
      predictor[c] = (short)BinaryPrimitives.ReadUInt16LittleEndian(block[(c * 2)..]);
    for (var c = 0; c < channels; ++c) {
      index[c] = (short)BinaryPrimitives.ReadUInt16LittleEndian(block[(2 * channels + c * 2)..]);
      if (index[c] > 88) index[c] = 88;
      else if (index[c] < 0) index[c] = 0;
    }

    var payload = block[headerSize..];
    var bytesPerChannel = payload.Length / channels;
    var samplesPerChannel = bytesPerChannel * 2;
    var output = new short[samplesPerChannel * channels];

    for (var c = 0; c < channels; ++c) {
      var p = predictor[c];
      var ix = index[c];
      var run = payload.Slice(c * bytesPerChannel, bytesPerChannel);
      for (var i = 0; i < bytesPerChannel; ++i) {
        var b = run[i];
        output[(i * 2) * channels + c] = ImaCore.Expand(b & 0x0F, ref p, ref ix, 4);
        output[(i * 2 + 1) * channels + c] = ImaCore.Expand(b >> 4, ref p, ref ix, 4);
      }
    }

    return output;
  }
}
