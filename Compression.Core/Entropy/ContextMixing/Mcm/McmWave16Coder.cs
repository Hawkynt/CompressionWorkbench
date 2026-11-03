namespace Compression.Core.Entropy.ContextMixing.Mcm;

/// <summary>
/// mcm 0.84's stereo 16-bit PCM coder (compressor type 1), from <c>docs/MCM-ON-DISK.md</c>, "Wav16":
/// per 4096-sample block a second-order-by-channel delta, then per sample a direct sign bit,
/// eleven modelled bits and four direct bits. mcm's own decoder does not invert this encoder;
/// <see cref="Decode"/> does.
/// </summary>
public static class McmWave16Coder {
  private const int BlockSamples = 4096;
  private const int ModelledBits = 11;
  private const int DirectBits = 4;

  /// <summary>Encodes little-endian 16-bit samples; an odd final byte is dropped, as mcm does.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    var encoder = new McmRangeEncoder(Math.Max(64, data.Length / 2));
    var models = NewModels();
    var samples = data.Length / 2;
    var block = new short[BlockSamples];
    for (var start = 0; start < samples; start += BlockSamples) {
      var count = Math.Min(BlockSamples, samples - start);
      for (var i = 0; i < count; ++i)
        block[i] = (short)(data[(start + i) * 2] | data[(start + i) * 2 + 1] << 8);
      for (var i = count - 1; i >= 2; --i)
        block[i] = (short)(block[i] - block[i - 2]);
      for (var i = 0; i < count; ++i) {
        var error = (ushort)-block[i];
        var high = error >> 15;
        encoder.EncodeDirect(high);
        var value = (high != 0 ? ~error : error) & 0x7FFF;
        var context = (i & 1) << ModelledBits;
        var node = 1;
        for (var b = 14; b >= DirectBits; --b) {
          var bit = (value >> b) & 1;
          encoder.Encode(bit, Probability(models[context + node]));
          Train(ref models[context + node], bit);
          node = node * 2 + bit;
        }
        for (var b = DirectBits - 1; b >= 0; --b)
          encoder.EncodeDirect((value >> b) & 1);
      }
    }
    return encoder.Finish();
  }

  /// <summary>Decodes <paramref name="count"/> bytes; mcm cannot have written an odd count it could restore.</summary>
  public static byte[] Decode(byte[] source, int offset, int end, int count, out int consumed) {
    if ((count & 1) != 0)
      throw new InvalidDataException("mcm Wav16 block of odd length: the encoder dropped its last byte.");
    var decoder = new McmRangeDecoder(source, offset, end);
    var models = NewModels();
    var output = new byte[count];
    var samples = count / 2;
    var block = new short[BlockSamples];
    for (var start = 0; start < samples; start += BlockSamples) {
      var blockCount = Math.Min(BlockSamples, samples - start);
      for (var i = 0; i < blockCount; ++i) {
        var high = decoder.DecodeDirect();
        var context = (i & 1) << ModelledBits;
        var node = 1;
        for (var b = 0; b < ModelledBits; ++b) {
          var bit = decoder.Decode(Probability(models[context + node]));
          Train(ref models[context + node], bit);
          node = node * 2 + bit;
        }
        for (var b = 0; b < DirectBits; ++b)
          node = node * 2 + decoder.DecodeDirect();
        var value = node & 0x7FFF;
        var error = high != 0 ? 0x8000 | (~value & 0x7FFF) : value;
        var delta = (short)-error;
        block[i] = i >= 2 ? (short)(delta + block[i - 2]) : delta;
        output[(start + i) * 2] = (byte)block[i];
        output[(start + i) * 2 + 1] = (byte)(block[i] >> 8);
      }
    }
    consumed = decoder.Position - offset;
    if (decoder.Overran)
      throw new InvalidDataException("mcm Wav16 stream is truncated.");
    return output;
  }

  private static int[] NewModels() {
    var models = new int[2 << (ModelledBits + 2)];
    Array.Fill(models, 2048 << 18);
    return models;
  }

  private static int Probability(int model) {
    var p = model >> 18;
    return p == 0 ? 1 : p == 4096 ? 4095 : p;
  }

  private static void Train(ref int model, int bit) => model += ((bit << 30) - model) >> 6;
}
