using System.Buffers.Binary;
using Compression.Core.Entropy.Arithmetic;

namespace Compression.Core.Entropy.ContextMixing;

/// <summary>
/// A clean-room lpaq-grade context-mixing compressor: a set of hashed order-N
/// bit models combined in the logistic domain, refined by an adaptive
/// probability map (SSE), and coded with a binary arithmetic coder.
/// </summary>
/// <remarks>
/// <para>Per output bit the coder:</para>
/// <list type="number">
///   <item>hashes orders 0..5 of prior bytes together with the partial current
///         byte into one context per <see cref="ContextModel"/>;</item>
///   <item>mixes the models' predictions in the stretch domain
///         (<see cref="ContextMixer"/>);</item>
///   <item>refines the result through an <see cref="Apm"/> stage; and</item>
///   <item>codes the bit through the binary arithmetic coder.</item>
/// </list>
/// <para>
/// Bytes are coded MSB-first; the original length is stored in the header so
/// decoding stops exactly. Decoding is the symmetric inverse — it reproduces
/// the same predictions because every model, weight and APM update is
/// deterministic integer arithmetic. This is the algorithmic foundation for
/// formats like ZPAQ, PAQ8 and cmix.
/// </para>
/// </remarks>
public static class CmCompressor {
  // Context model table sizes (log2). Order 0 is small; higher orders are hashed
  // into larger tables to limit collisions while capping total memory.
  private static readonly int[] _OrderTableBits = [10, 16, 18, 21, 22, 22];

  // Orders modelled: 0 = bit-position only, k = previous k bytes.
  private static readonly int[] _Orders = [0, 1, 2, 3, 4, 6];

  private const int ApmContexts = 256; // refinement keyed by previous byte
  private const int ProbabilityScaleBits = 16;

  /// <summary>
  /// Compresses data using logistic-domain context mixing.
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

    var encoder = new ArithmeticEncoder(output);
    var state = new CmState();
    var contexts = new int[state.ModelCount];

    foreach (int value in data) {
      var c0 = 1; // partial byte with a leading 1 sentinel (lpaq convention)
      for (var bit = 7; bit >= 0; --bit) {
        var bitVal = (value >> bit) & 1;

        state.ComputeContexts(contexts, c0);
        var prob1 = state.Predict(contexts, c0);
        encoder.EncodeBit(bitVal, (1 << CmCompressor.ProbabilityScaleBits) - prob1);
        state.Update(contexts, bitVal);

        c0 = (c0 << 1) | bitVal;
      }

      state.PushByte(value);
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
    var size = BinaryPrimitives.ReadInt32LittleEndian(compressed);
    if (size == 0)
      return [];

    using var input = new MemoryStream(compressed[4..].ToArray());
    var decoder = new ArithmeticDecoder(input);
    var state = new CmState();
    var contexts = new int[state.ModelCount];

    var result = new byte[size];
    for (var i = 0; i < size; ++i) {
      var c0 = 1;
      for (var bit = 7; bit >= 0; --bit) {
        state.ComputeContexts(contexts, c0);
        var prob1 = state.Predict(contexts, c0);
        var bitVal = decoder.DecodeBit((1 << CmCompressor.ProbabilityScaleBits) - prob1);
        state.Update(contexts, bitVal);

        c0 = (c0 << 1) | bitVal;
      }

      var b = c0 & 0xFF;
      result[i] = (byte)b;
      state.PushByte(b);
    }

    return result;
  }

  /// <summary>
  /// Encapsulates the model set, mixer and APM plus the rolling byte history,
  /// shared identically by the encoder and decoder.
  /// </summary>
  private sealed class CmState {
    private readonly ContextModel[] _models;
    private readonly ContextMixer _mixer;
    private readonly Apm _apm;
    private readonly int[] _history;     // previous bytes, _history[0] = most recent
    private int _lastApmContext;

    public CmState() {
      var count = CmCompressor._Orders.Length;
      this._models = new ContextModel[count];
      for (var i = 0; i < count; ++i)
        this._models[i] = new ContextModel(CmCompressor._OrderTableBits[i]);

      this._mixer = new ContextMixer(this._models);
      this._apm = new Apm(CmCompressor.ApmContexts);
      this._history = new int[8];
    }

    public int ModelCount => this._models.Length;

    public void ComputeContexts(int[] contexts, int c0) {
      for (var i = 0; i < this._models.Length; ++i) {
        var order = CmCompressor._Orders[i];
        // Seed the hash with the order itself so different orders never collide
        // into the same slot, then fold in the prior bytes and the partial byte.
        var h = (uint)(order * 0x9E3779B1u);
        for (var k = 0; k < order; ++k)
          h = CmState.Mix(h, (uint)this._history[k]);

        h = CmState.Mix(h, (uint)c0);
        contexts[i] = (int)(h & 0x7FFFFFFF);
      }
    }

    public int Predict(int[] contexts, int c0) {
      var mixed16 = this._mixer.Predict(contexts);
      var mixed12 = mixed16 >> (CmCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits);

      // Refine against the previous byte; mix mixer and APM output (lpaq blend).
      this._lastApmContext = this._history[0];
      var refined12 = this._apm.Refine(mixed12, this._lastApmContext);
      var blended12 = (mixed12 + 3 * refined12) >> 2;
      blended12 = Math.Clamp(blended12, 1, Logistic.ProbabilityScale - 1);

      var p16 = blended12 << (CmCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits);
      return Math.Clamp(p16, 1, (1 << CmCompressor.ProbabilityScaleBits) - 1);
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
