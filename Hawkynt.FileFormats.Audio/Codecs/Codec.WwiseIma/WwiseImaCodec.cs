#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.WwiseIma;

/// <summary>
/// Audiokinetic Wwise IMA ADPCM (the <c>imaw</c>-style variant carried by .wem media). The
/// step/index tables are the standard IMA ones; the block layout follows Microsoft IMA's
/// interleave:
/// <list type="bullet">
///   <item>The stream is a sequence of fixed <c>blockAlign</c>-byte blocks. <c>blockAlign</c>
///         is the size of one block across <em>all</em> channels (a common Wwise value is
///         <c>0x24</c> per channel).</item>
///   <item>Each block starts with one 4-byte header per channel — int16 LE predictor,
///         u8 step index, 1 reserved byte — laid out channel after channel.</item>
///   <item>The remaining bytes are 4-byte nibble groups that round-robin through the
///         channels (ch0's 4 bytes, ch1's 4 bytes, …); within each byte the LOW nibble is
///         decoded first. Each 4-byte group yields 8 samples for its channel.</item>
/// </list>
/// The header predictor is emitted as the block's first sample for each channel.
/// </summary>
public static class WwiseImaCodec {

  private static readonly int[] StepTable = [
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
    34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
    157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658,
    724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024,
    3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
    15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
  ];

  private static readonly int[] IndexAdjust = [-1, -1, -1, -1, 2, 4, 6, 8];

  /// <summary>Bytes of nibble data per channel per interleave group (one 4-byte group → 8 samples).</summary>
  public const int GroupBytes = 4;

  /// <summary>Per-channel block header size (int16 predictor + u8 step index + 1 reserved).</summary>
  public const int HeaderBytes = 4;

  /// <summary>
  /// Decodes Wwise IMA ADPCM into interleaved 16-bit PCM. Each block contributes
  /// <c>((blockAlign/channels) - 4) * 2 + 1</c> samples per channel.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> data, int channels, int blockAlign) {
    if (channels < 1)
      throw new ArgumentException("Wwise IMA needs at least one channel.", nameof(channels));
    if (blockAlign < HeaderBytes * channels)
      throw new ArgumentException($"blockAlign {blockAlign} too small for {channels} channel(s).", nameof(blockAlign));

    var perChannelData = blockAlign / channels - HeaderBytes;
    if (perChannelData < 0 || perChannelData % GroupBytes != 0)
      throw new ArgumentException($"blockAlign {blockAlign} does not split into whole 4-byte groups per channel.", nameof(blockAlign));

    var groupsPerBlock = perChannelData / GroupBytes;
    var samplesPerBlock = perChannelData * 2 + 1;
    var blockCount = data.Length / blockAlign;
    var output = new short[blockCount * samplesPerBlock * channels];

    var predictor = new int[channels];
    var index = new int[channels];

    for (var b = 0; b < blockCount; ++b) {
      var blockStart = b * blockAlign;
      var outFrame = b * samplesPerBlock;

      for (var c = 0; c < channels; ++c) {
        var h = blockStart + c * HeaderBytes;
        predictor[c] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(h, 2));
        index[c] = data[h + 2];
        if (index[c] > 88) index[c] = 88;
        if (index[c] < 0) index[c] = 0;
        output[outFrame * channels + c] = (short)predictor[c];
      }

      var dataStart = blockStart + HeaderBytes * channels;
      for (var grp = 0; grp < groupsPerBlock; ++grp) {
        for (var c = 0; c < channels; ++c) {
          var gs = dataStart + (grp * channels + c) * GroupBytes;
          for (var k = 0; k < GroupBytes; ++k) {
            var byteVal = data[gs + k];
            var sampleIndex = 1 + grp * (GroupBytes * 2) + k * 2;
            output[(outFrame + sampleIndex) * channels + c] =
              DecodeNibble((byte)(byteVal & 0x0F), ref predictor[c], ref index[c]);
            output[(outFrame + sampleIndex + 1) * channels + c] =
              DecodeNibble((byte)(byteVal >> 4), ref predictor[c], ref index[c]);
          }
        }
      }
    }

    return output;
  }

  /// <summary>
  /// Encodes interleaved 16-bit PCM into Wwise IMA ADPCM blocks of <paramref name="blockAlign"/>
  /// bytes. The first sample of each block per channel is stored verbatim as the header
  /// predictor; the rest are quantised with the standard IMA step walk. The final block is
  /// padded with the last reconstructed sample so it stays a whole block.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> interleaved, int channels, int blockAlign) {
    if (channels < 1)
      throw new ArgumentException("Wwise IMA needs at least one channel.", nameof(channels));
    if (blockAlign < HeaderBytes * channels)
      throw new ArgumentException($"blockAlign {blockAlign} too small for {channels} channel(s).", nameof(blockAlign));
    if (interleaved.Length % channels != 0)
      throw new ArgumentException("Interleaved sample count must be a multiple of the channel count.", nameof(interleaved));

    var perChannelData = blockAlign / channels - HeaderBytes;
    if (perChannelData < 0 || perChannelData % GroupBytes != 0)
      throw new ArgumentException($"blockAlign {blockAlign} does not split into whole 4-byte groups per channel.", nameof(blockAlign));

    var groupsPerBlock = perChannelData / GroupBytes;
    var samplesPerBlock = perChannelData * 2 + 1;
    var framesPerChannel = interleaved.Length / channels;
    var blockCount = (framesPerChannel + samplesPerBlock - 1) / samplesPerBlock;
    if (blockCount == 0) return [];

    var output = new byte[blockCount * blockAlign];
    var predictor = new int[channels];
    var index = new int[channels];

    for (var b = 0; b < blockCount; ++b) {
      var blockStart = b * blockAlign;
      var baseFrame = b * samplesPerBlock;

      // Header: seed each channel's predictor from the first sample of the block, and pick a
      // starting step index from the block's first delta so the IMA step can track the signal
      // immediately (a too-small step otherwise needs many samples to ramp up — the classic
      // "slow attack"). The header records both, so the decoder reproduces this state exactly.
      for (var c = 0; c < channels; ++c) {
        var first = baseFrame < framesPerChannel ? interleaved[baseFrame * channels + c] : (short)0;
        predictor[c] = first;
        var second = baseFrame + 1 < framesPerChannel ? interleaved[(baseFrame + 1) * channels + c] : first;
        index[c] = StartIndexFor(Math.Abs(second - first));
        var h = blockStart + c * HeaderBytes;
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(h, 2), (short)predictor[c]);
        output[h + 2] = (byte)index[c];
        output[h + 3] = 0;
      }

      var dataStart = blockStart + HeaderBytes * channels;
      for (var grp = 0; grp < groupsPerBlock; ++grp) {
        for (var c = 0; c < channels; ++c) {
          var gs = dataStart + (grp * channels + c) * GroupBytes;
          for (var k = 0; k < GroupBytes; ++k) {
            var sampleIndex = 1 + grp * (GroupBytes * 2) + k * 2;
            var s0 = Sample(interleaved, framesPerChannel, channels, baseFrame + sampleIndex, c, predictor[c]);
            var s1 = Sample(interleaved, framesPerChannel, channels, baseFrame + sampleIndex + 1, c, predictor[c]);
            var low = EncodeNibble(s0, ref predictor[c], ref index[c]);
            var high = EncodeNibble(s1, ref predictor[c], ref index[c]);
            output[gs + k] = (byte)((high << 4) | low);
          }
        }
      }
    }

    return output;
  }

  private static int Sample(ReadOnlySpan<short> interleaved, int framesPerChannel, int channels, int frame, int channel, int fallback)
    => frame < framesPerChannel ? interleaved[frame * channels + channel] : fallback;

  /// <summary>
  /// Smallest step-table index whose step is at least <paramref name="delta"/>, so the very
  /// first quantised nibble of a block can span the initial jump. Clamped to the table range.
  /// </summary>
  private static int StartIndexFor(int delta) {
    var i = 0;
    while (i < StepTable.Length - 1 && StepTable[i] < delta)
      ++i;
    return i;
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

  private static int EncodeNibble(int sample, ref int predictor, ref int index) {
    var step = StepTable[index];
    var delta = sample - predictor;

    var nibble = 0;
    if (delta < 0) {
      nibble = 8;
      delta = -delta;
    }

    // Standard IMA quantisation: vdiff accumulates step/2,4,8 per matched bit.
    var diff = step >> 3;
    if (delta >= step) { nibble |= 4; delta -= step; diff += step; }
    if (delta >= (step >> 1)) { nibble |= 2; delta -= step >> 1; diff += step >> 1; }
    if (delta >= (step >> 2)) { nibble |= 1; diff += step >> 2; }

    if ((nibble & 8) != 0) predictor -= diff;
    else predictor += diff;
    if (predictor > 32767) predictor = 32767;
    else if (predictor < -32768) predictor = -32768;

    index += IndexAdjust[nibble & 0x07];
    if (index < 0) index = 0;
    else if (index > 88) index = 88;

    return nibble;
  }
}
