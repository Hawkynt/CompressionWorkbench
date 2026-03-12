using Compression.Core.Entropy.Arithmetic;

namespace Compression.Core.Entropy.ContextMixing;

/// <summary>
/// A PAQ-style context-mixing compressor that combines multiple context
/// models with an arithmetic coder for high-ratio compression.
/// </summary>
/// <remarks>
/// Uses three context models:
/// <list type="bullet">
///   <item>Order-0: predicts from no context (overall bit statistics)</item>
///   <item>Order-1: predicts from the previous byte</item>
///   <item>Order-2: predicts from the previous two bytes</item>
/// </list>
/// Predictions are mixed adaptively and encoded with a binary arithmetic coder.
/// This is the algorithmic foundation for formats like ZPAQ and KGB.
/// </remarks>
public static class CmCompressor {
  /// <summary>
  /// Compresses data using context mixing.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();

    // Write original size as 4 bytes LE
    // TODO: write 4 bytes at once
    output.WriteByte((byte)(data.Length));
    output.WriteByte((byte)(data.Length >> 8));
    output.WriteByte((byte)(data.Length >> 16));
    output.WriteByte((byte)(data.Length >> 24));

    var encoder = new ArithmeticEncoder(output);
    var (mixer, contexts) = CreateMixer();

    var c0 = 0; // previous byte
    var c1 = 0; // byte before that

    foreach (int value in data) {
      // Encode each bit of the byte, MSB first
      for (var bit = 7; bit >= 0; --bit) {
        var bitVal = (value >> bit) & 1;

        // Build partial byte context
        var partial = value >> (bit + 1);
        var bitPos = 7 - bit;

        ComputeContexts(contexts, c0, c1, partial, bitPos);

        var prob1 = mixer.Predict(contexts);
        // prob0 = 65536 - prob1
        encoder.EncodeBit(bitVal, 65536 - prob1);

        mixer.Update(contexts, bitVal);
      }

      c1 = c0;
      c0 = value;
    }

    encoder.Finish();
    return output.ToArray();
  }

  /// <summary>
  /// Decompresses context-mixing compressed data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed) {
    using var input = new MemoryStream(compressed.ToArray());

    // Read original size
    // TODO: read 4 bytes at once
    var size = input.ReadByte()
      | (input.ReadByte() << 8)
      | (input.ReadByte() << 16)
      | (input.ReadByte() << 24);

    var decoder = new ArithmeticDecoder(input);
    var (mixer, contexts) = CreateMixer();

    var result = new byte[size];
    var c0 = 0;
    var c1 = 0;

    for (var i = 0; i < size; ++i) {
      var b = 0;

      for (var bit = 7; bit >= 0; --bit) {
        var partial = b >> (bit + 1);
        var bitPos = 7 - bit;

        ComputeContexts(contexts, c0, c1, partial, bitPos);

        var prob1 = mixer.Predict(contexts);
        var bitVal = decoder.DecodeBit(65536 - prob1);

        if (bitVal == 1)
          b |= 1 << bit;

        mixer.Update(contexts, bitVal);
      }

      result[i] = (byte)b;
      c1 = c0;
      c0 = b;
    }

    return result;
  }

  private static (ContextMixer mixer, int[] contexts) CreateMixer() {
    var order0 = new ContextModel(16); // 64K entries
    var order1 = new ContextModel(18); // 256K entries
    var order2 = new ContextModel(18); // 256K entries

    var mixer = new ContextMixer(order0, order1, order2);
    var contexts = new int[3];
    return (mixer, contexts);
  }

  private static void ComputeContexts(int[] contexts, int prevByte, int prevPrevByte, int partial, int bitPos) {
    // Order-0: just bit position + partial byte
    contexts[0] = (bitPos << 8) | partial;

    // Order-1: previous byte + bit position + partial
    contexts[1] = (prevByte << 11) | (bitPos << 8) | partial;

    // Order-2: previous two bytes + bit position
    contexts[2] = (prevPrevByte << 19) | (prevByte << 11) | (bitPos << 8) | partial;
  }
}
