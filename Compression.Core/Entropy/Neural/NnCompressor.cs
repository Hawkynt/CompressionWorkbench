using System.Buffers.Binary;
using Compression.Core.Entropy.Arithmetic;
using Compression.Core.Entropy.ContextMixing;

namespace Compression.Core.Entropy.Neural;

/// <summary>
/// A clean-room neural compressor: an online-trained multi-layer perceptron
/// (<see cref="NeuralPredictor"/>) drives a binary arithmetic coder bit-by-bit.
/// </summary>
/// <remarks>
/// <para>
/// This is an NNCP-style "neural sequence predictor": the network learns the
/// statistics of the data <i>as it compresses</i>, and the decoder replays the
/// identical learning trajectory, so no weights need to be transmitted. The
/// predictor is a genuine two-layer net with a nonlinear hidden layer and
/// backpropagation — distinct from the single-layer logistic mixer used by the
/// context-mixing primitive — but it reuses that package's deterministic
/// <see cref="Logistic"/> tables, <see cref="ContextModel"/> bit models and the
/// shared binary <see cref="ArithmeticEncoder"/>/<see cref="ArithmeticDecoder"/>
/// so the entropy backend is consistent.
/// </para>
/// <para>
/// Per byte the coder walks all eight bits MSB-first. For each bit it asks the
/// network for <c>P(bit=1)</c>, codes the bit, then trains the network on the
/// true outcome. The original length is stored in a 4-byte header so decoding
/// stops exactly; an empty input produces just the header.
/// </para>
/// </remarks>
public static class NnCompressor {
  /// <summary>Compresses data with the online neural predictor.</summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The compressed bytes (4-byte length header + arithmetic-coded stream).</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();

    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    output.Write(header);

    if (data.Length == 0)
      return output.ToArray();

    var encoder = new ArithmeticEncoder(output);
    var net = new NeuralPredictor();

    foreach (int value in data) {
      var partial = 1; // leading-1 sentinel
      for (var bit = 7; bit >= 0; --bit) {
        var bitVal = (value >> bit) & 1;

        var prob1 = net.Predict(partial);
        encoder.EncodeBit(bitVal, 65536 - prob1); // coder wants P(bit=0)
        net.Update(bitVal);

        partial = (partial << 1) | bitVal;
      }

      net.PushByte(value);
    }

    encoder.Finish();
    return output.ToArray();
  }

  /// <summary>Decompresses data produced by <see cref="Compress"/>.</summary>
  /// <param name="compressed">The compressed bytes.</param>
  /// <returns>The original data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed) {
    var size = BinaryPrimitives.ReadInt32LittleEndian(compressed);
    if (size == 0)
      return [];

    using var input = new MemoryStream(compressed[4..].ToArray());
    var decoder = new ArithmeticDecoder(input);
    var net = new NeuralPredictor();

    var result = new byte[size];
    for (var i = 0; i < size; ++i) {
      var partial = 1;
      for (var bit = 7; bit >= 0; --bit) {
        var prob1 = net.Predict(partial);
        var bitVal = decoder.DecodeBit(65536 - prob1);
        net.Update(bitVal);

        partial = (partial << 1) | bitVal;
      }

      var b = partial & 0xFF;
      result[i] = (byte)b;
      net.PushByte(b);
    }

    return result;
  }
}
