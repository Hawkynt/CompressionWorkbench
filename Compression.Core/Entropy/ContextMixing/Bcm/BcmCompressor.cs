using System.Buffers.Binary;
using Compression.Core.Entropy.Arithmetic;
using Compression.Core.Transforms;

namespace Compression.Core.Entropy.ContextMixing.Bcm;

/// <summary>
/// A clean-room implementation of the BCM architecture: a Burrows-Wheeler
/// Transform followed by a small logistic-domain context-mixing back end.
/// </summary>
/// <remarks>
/// <para>
/// Modelled after Ilya Muravyov's BCM ("Big brother of BZip2"), which pairs a
/// block sort with a compact context-mixing coder instead of BWT's classic
/// MTF+Huffman/arithmetic pipeline. See
/// <see href="https://github.com/encode84/bcm"/>,
/// Burrows &amp; Wheeler, "A Block-sorting Lossless Data Compression Algorithm"
/// (SRC-RR-124, 1994), and Mahoney, "Data Compression Explained"
/// (<see href="http://mattmahoney.net/dc/dce.html"/>) for the general
/// block-sort + context-mixing family this belongs to.
/// </para>
/// <para>
/// This is a reduced, from-specification reimplementation, not a port of the
/// reference source: after the forward BWT, the sorted string is coded
/// bit-by-bit with three hashed byte-history context models (orders 0, 1, 2)
/// mixed in the stretch domain and refined by one adaptive probability map
/// (SSE) keyed on the previous BWT byte, then entropy-coded with a binary
/// arithmetic coder. BWT output clusters bytes that share a following
/// context, so low orders already carry most of the redundancy — matching
/// the compact model set the real BCM uses.
/// </para>
/// </remarks>
public static class BcmCompressor {
  // Orders modelled directly over the BWT output: order-0 (bit position only),
  // order-1 (previous BWT byte) and order-2 (previous two BWT bytes).
  private static readonly int[] _Orders = [0, 1, 2];
  private static readonly int[] _OrderTableBits = [9, 16, 20];

  private const int ApmContexts = 256;
  private const int ProbabilityScaleBits = 16;

  /// <summary>
  /// Compresses data via BWT followed by context-mixing entropy coding.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();

    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    output.Write(header);

    if (data.Length == 0)
      return output.ToArray();

    var (bwt, index) = BurrowsWheelerTransform.Forward(data);

    Span<byte> indexHeader = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader, index);
    output.Write(indexHeader);

    var encoder = new ArithmeticEncoder(output);
    var state = new BcmState();
    var contexts = new int[state.ModelCount];

    foreach (int value in bwt) {
      var c0 = 1;
      for (var bit = 7; bit >= 0; --bit) {
        var bitVal = (value >> bit) & 1;

        state.ComputeContexts(contexts, c0);
        var prob1 = state.Predict(contexts, c0);
        encoder.EncodeBit(bitVal, (1 << BcmCompressor.ProbabilityScaleBits) - prob1);
        state.Update(contexts, bitVal);

        c0 = (c0 << 1) | bitVal;
      }

      state.PushByte(value);
    }

    encoder.Finish();
    return output.ToArray();
  }

  /// <summary>
  /// Decompresses BCM-style compressed data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed) {
    var size = BinaryPrimitives.ReadInt32LittleEndian(compressed);
    if (size == 0)
      return [];

    var index = BinaryPrimitives.ReadInt32LittleEndian(compressed[4..]);

    using var input = new MemoryStream(compressed[8..].ToArray());
    var decoder = new ArithmeticDecoder(input);
    var state = new BcmState();
    var contexts = new int[state.ModelCount];

    var bwt = new byte[size];
    for (var i = 0; i < size; ++i) {
      var c0 = 1;
      for (var bit = 7; bit >= 0; --bit) {
        state.ComputeContexts(contexts, c0);
        var prob1 = state.Predict(contexts, c0);
        var bitVal = decoder.DecodeBit((1 << BcmCompressor.ProbabilityScaleBits) - prob1);
        state.Update(contexts, bitVal);

        c0 = (c0 << 1) | bitVal;
      }

      var b = c0 & 0xFF;
      bwt[i] = (byte)b;
      state.PushByte(b);
    }

    return BurrowsWheelerTransform.Inverse(bwt, index);
  }

  /// <summary>
  /// Model set, mixer and APM shared identically by the encoder and decoder,
  /// operating over the BWT-transformed byte stream.
  /// </summary>
  private sealed class BcmState {
    private readonly ContextModel[] _models;
    private readonly ContextMixer _mixer;
    private readonly Apm _apm;
    private readonly int[] _history;

    public BcmState() {
      var count = BcmCompressor._Orders.Length;
      this._models = new ContextModel[count];
      for (var i = 0; i < count; ++i)
        this._models[i] = new ContextModel(BcmCompressor._OrderTableBits[i]);

      this._mixer = new ContextMixer(this._models);
      this._apm = new Apm(BcmCompressor.ApmContexts);
      this._history = new int[4];
    }

    public int ModelCount => this._models.Length;

    public void ComputeContexts(int[] contexts, int c0) {
      for (var i = 0; i < this._models.Length; ++i) {
        var order = BcmCompressor._Orders[i];
        var h = (uint)(order * 0x9E3779B1u);
        for (var k = 0; k < order; ++k)
          h = BcmState.Mix(h, (uint)this._history[k]);

        h = BcmState.Mix(h, (uint)c0);
        contexts[i] = (int)(h & 0x7FFFFFFF);
      }
    }

    public int Predict(int[] contexts, int c0) {
      _ = c0;
      var mixed16 = this._mixer.Predict(contexts);
      var mixed12 = mixed16 >> (BcmCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits);

      var refined12 = this._apm.Refine(mixed12, this._history[0]);
      var blended12 = (mixed12 + refined12) >> 1;
      blended12 = Math.Clamp(blended12, 1, Logistic.ProbabilityScale - 1);

      var p16 = blended12 << (BcmCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits);
      return Math.Clamp(p16, 1, (1 << BcmCompressor.ProbabilityScaleBits) - 1);
    }

    public void Update(int[] contexts, int bit) {
      this._mixer.Update(contexts, bit);
      this._apm.Update(bit);
    }

    public void PushByte(int value) {
      for (var k = this._history.Length - 1; k > 0; --k)
        this._history[k] = this._history[k - 1];
      this._history[0] = value & 0xFF;
    }

    private static uint Mix(uint h, uint x) {
      h ^= x + 0x9E3779B1u + (h << 6) + (h >> 2);
      return h;
    }
  }
}
