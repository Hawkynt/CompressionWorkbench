#pragma warning disable CS1591
namespace Codec.SmackerAudio;

/// <summary>
/// Decode-only port of FFmpeg's Smacker audio decoder (<c>libavcodec/smacker.c</c>,
/// the <c>smka_decode_frame</c> path, codec tag 'SMKA'). Each audio chunk is prefixed by a
/// 4-byte little-endian unpacked-byte count, followed by an LSB-first bitstream
/// (<see cref="SmackerBitReader"/>). The bitstream carries a "data present" flag, a stereo
/// flag and a bit-depth flag, then <c>1 &lt;&lt; (bits + stereo)</c> Huffman trees
/// (<see cref="SmackerHuffman"/>); samples are reconstructed by adding Huffman-coded deltas
/// to per-channel predictors seeded from initial base values.
///
/// <para>16-bit predictors are seeded from byte-swapped 16-bit base values and the deltas
/// combine a low and a high byte tree; 8-bit predictors use one (mono) or two (stereo)
/// trees directly. Output is interleaved native PCM: signed 16-bit for the 16-bit format,
/// unsigned 8-bit for the 8-bit format. The codec relies on wraparound rather than clipping
/// (the reference comment), so predictor arithmetic is done in <c>ushort</c>/<c>byte</c>.</para>
/// </summary>
public sealed class SmackerAudioCodec {

  /// <summary>Channel count declared by the container audio-track header (1 or 2).</summary>
  public int Channels { get; }

  /// <summary>Sample rate declared by the container audio-track header.</summary>
  public int SampleRate { get; }

  /// <summary>Coded bit depth declared by the container (8 or 16).</summary>
  public int BitsPerSample { get; }

  /// <summary>
  /// Builds a decoder from the container's declared track parameters.
  /// <paramref name="channels"/> must be 1 or 2; <paramref name="bitsPerSample"/> 8 or 16.
  /// </summary>
  public SmackerAudioCodec(int sampleRate, int channels, int bitsPerSample) {
    if (channels is not (1 or 2))
      throw new ArgumentOutOfRangeException(nameof(channels));
    if (bitsPerSample is not (8 or 16))
      throw new ArgumentOutOfRangeException(nameof(bitsPerSample));
    this.SampleRate = sampleRate;
    this.Channels = channels;
    this.BitsPerSample = bitsPerSample;
  }

  /// <summary>
  /// Decodes the concatenation of every audio chunk for one track into interleaved native
  /// PCM bytes (little-endian signed 16-bit, or unsigned 8-bit). Each chunk must carry its
  /// own 4-byte length prefix. A chunk that fails to decode is skipped so the rest of the
  /// stream still surfaces.
  /// </summary>
  public byte[] DecodeStream(IReadOnlyList<byte[]> chunks) {
    using var ms = new MemoryStream();
    foreach (var chunk in chunks) {
      var pcm = this.DecodeChunk(chunk);
      if (pcm.Length > 0)
        ms.Write(pcm);
    }
    return ms.ToArray();
  }

  /// <summary>
  /// Decodes one audio chunk (4-byte LE unpacked size + bitstream) to interleaved native
  /// PCM bytes. Returns an empty array on malformed input (mirrors the reference's
  /// <c>AVERROR_INVALIDDATA</c> bail-outs and "no data" early return).
  /// </summary>
  public byte[] DecodeChunk(byte[] chunk) {
    if (chunk.Length <= 4)
      return [];

    var unpSize = (int)(chunk[0] | (uint)chunk[1] << 8 | (uint)chunk[2] << 16 | (uint)chunk[3] << 24);
    if (unpSize < 0 || unpSize > (1 << 24))
      return [];

    var reader = new SmackerBitReader(chunk, 4, chunk.Length - 4);

    if (reader.GetBit() == 0) // "Sound: no data"
      return [];

    var stereo = reader.GetBit();
    var bits = reader.GetBit();

    // Container/bitstream consistency, as the reference asserts.
    if (stereo != (this.Channels != 1 ? 1 : 0))
      return [];
    var is8Bit = this.BitsPerSample == 8;
    if ((bits == 1) == is8Bit)
      return [];

    // Build the trees: 1 << (bits + stereo) of them, each wrapped by skip_bits1 markers.
    var treeCount = 1 << (bits + stereo);
    var trees = new SmackerHuffman[4];
    for (var i = 0; i < treeCount; ++i) {
      reader.SkipBit();
      var tree = SmackerHuffman.Build(reader);
      if (tree == null)
        return [];
      reader.SkipBit();
      trees[i] = tree;
    }

    if (reader.BitsLeft < (stereo + 1) * (bits + 1) * 8)
      return [];

    return bits == 1
      ? Decode16(reader, trees, stereo, unpSize)
      : Decode8(reader, trees, stereo, unpSize);
  }

  private static byte[] Decode16(SmackerBitReader reader, SmackerHuffman[] trees, int stereo, int unpSize) {
    var samples = new List<short>(unpSize / 2);
    var pred = new ushort[2];

    // Seed predictors: byte-swapped 16-bit bases, read high-channel-first.
    for (var i = stereo; i >= 0; --i)
      pred[i] = ByteSwap16((ushort)reader.GetBits(16));
    for (var i = 0; i <= stereo; ++i)
      samples.Add(unchecked((short)pred[i]));

    var count = unpSize / 2;
    var start = stereo + 1;
    for (var i = start; i < count; ++i) {
      if (reader.BitsLeft < 0)
        break;
      var idx = 2 * (i & stereo);
      uint val = (uint)DecodeValue(trees[idx], reader);
      val |= (uint)DecodeValue(trees[idx + 1], reader) << 8;
      pred[idx / 2] = (ushort)(pred[idx / 2] + val);
      samples.Add(unchecked((short)pred[idx / 2]));
    }

    var bytes = new byte[samples.Count * 2];
    for (var i = 0; i < samples.Count; ++i) {
      bytes[i * 2] = (byte)(samples[i] & 0xFF);
      bytes[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
    }
    return bytes;
  }

  private static byte[] Decode8(SmackerBitReader reader, SmackerHuffman[] trees, int stereo, int unpSize) {
    var samples = new List<byte>(unpSize);
    var pred = new byte[2];

    for (var i = stereo; i >= 0; --i)
      pred[i] = (byte)reader.GetBits(8);
    for (var i = 0; i <= stereo; ++i)
      samples.Add(pred[i]);

    var start = stereo + 1;
    for (var i = start; i < unpSize; ++i) {
      if (reader.BitsLeft < 0)
        break;
      var idx = i & stereo;
      var val = (uint)DecodeValue(trees[idx], reader);
      pred[idx] = (byte)(pred[idx] + val);
      samples.Add(pred[idx]);
    }

    return samples.ToArray();
  }

  private static int DecodeValue(SmackerHuffman tree, SmackerBitReader reader)
    => tree.IsSingle ? tree.SingleValue : tree.Decode(reader);

  private static ushort ByteSwap16(ushort v) => (ushort)((v >> 8) | (v << 8));
}
