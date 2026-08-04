using System.Buffers.Binary;
using Compression.Core.Entropy.Arithmetic;

namespace Compression.Core.Entropy.ContextMixing.Mcm;

/// <summary>
/// A clean-room implementation of the MCM architecture: several small
/// context-mixers, each specialised on a group of related contexts, combined
/// by a final mixing stage into one prediction — a two-level mixing network.
/// </summary>
/// <remarks>
/// <para>
/// Modelled after Mathieu Chartier's MCM (<see href="https://github.com/mathieuchartier/mcm"/>,
/// discussed at <see href="https://encode.su/threads/2121-MCM-new-compressor-by-Mathieu-Chartier"/>),
/// which mixes multiple specialised context maps through a network of mixers
/// rather than one flat mixer over every model. See also
/// <see href="https://en.wikipedia.org/wiki/Context_mixing"/> for the general
/// technique.
/// </para>
/// <para>
/// This is a reduced, from-specification reimplementation with three model
/// groups, each combined by its own <see cref="ContextMixer"/>:
/// </para>
/// <list type="bullet">
///   <item>local group — orders 0, 1, 2;</item>
///   <item>medium group — orders 3, 4;</item>
///   <item>wide group — order 6 plus a sparse context over the byte two
///         positions back (skipping the immediate predecessor, useful for
///         periodic/columnar data).</item>
/// </list>
/// <para>
/// The three group predictions are stretched and combined by a second,
/// top-level mixer (manually implemented with the same stretch/squash and
/// gradient-descent update <see cref="ContextMixer"/> uses internally, since
/// the top level mixes mixer outputs rather than raw model predictions), then
/// refined by two chained <see cref="Apm"/> (SSE) stages before entropy coding.
/// </para>
/// </remarks>
public static class McmCompressor {
  private const int ProbabilityScaleBits = 16;
  private const int WeightShift = 16;
  private const int LearningRate = 3;

  /// <summary>
  /// Compresses data using the two-level context-mixing network.
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
    var state = new McmState();

    foreach (int value in data) {
      var c0 = 1;
      for (var bit = 7; bit >= 0; --bit) {
        var bitVal = (value >> bit) & 1;
        var prob1 = state.Predict(c0);
        encoder.EncodeBit(bitVal, (1 << McmCompressor.ProbabilityScaleBits) - prob1);
        state.Update(bitVal);
        c0 = (c0 << 1) | bitVal;
      }

      state.PushByte(value);
    }

    encoder.Finish();
    return output.ToArray();
  }

  /// <summary>
  /// Decompresses MCM-style compressed data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed) {
    var size = BinaryPrimitives.ReadInt32LittleEndian(compressed);
    if (size == 0)
      return [];

    using var input = new MemoryStream(compressed[4..].ToArray());
    var decoder = new ArithmeticDecoder(input);
    var state = new McmState();

    var result = new byte[size];
    for (var i = 0; i < size; ++i) {
      var c0 = 1;
      for (var bit = 7; bit >= 0; --bit) {
        var prob1 = state.Predict(c0);
        var bitVal = decoder.DecodeBit((1 << McmCompressor.ProbabilityScaleBits) - prob1);
        state.Update(bitVal);
        c0 = (c0 << 1) | bitVal;
      }

      var b = c0 & 0xFF;
      result[i] = (byte)b;
      state.PushByte(b);
    }

    return result;
  }

  /// <summary>
  /// The two-level mixing network: three grouped sub-mixers feeding a top-level
  /// mixer, refined by a two-stage SSE chain.
  /// </summary>
  private sealed class McmState {
    private static readonly int[] _LocalOrders = [0, 1, 2];
    private static readonly int[] _MediumOrders = [3, 4];

    private readonly ContextModel[] _local = [new(9), new(16), new(20)];
    private readonly ContextModel[] _medium = [new(21), new(22)];
    private readonly ContextModel[] _wide = [new(22), new(18)]; // order-6, sparse(skip-1)

    private readonly ContextMixer _localMixer;
    private readonly ContextMixer _mediumMixer;
    private readonly ContextMixer _wideMixer;

    private readonly int[] _localCtx = new int[3];
    private readonly int[] _mediumCtx = new int[2];
    private readonly int[] _wideCtx = new int[2];

    private readonly int[] _networkWeights = [
      (1 << McmCompressor.WeightShift) / 3,
      (1 << McmCompressor.WeightShift) / 3,
      (1 << McmCompressor.WeightShift) / 3,
    ];
    private readonly int[] _networkStretch = new int[3];
    private int _preApmProbability12;

    private readonly Apm _apm1 = new(256);
    private readonly Apm _apm2 = new(1 << 12);

    private readonly int[] _history = new int[8];

    public McmState() {
      this._localMixer = new ContextMixer(this._local);
      this._mediumMixer = new ContextMixer(this._medium);
      this._wideMixer = new ContextMixer(this._wide);
    }

    public int Predict(int c0) {
      this.ComputeContexts(c0);

      var pLocal16 = this._localMixer.Predict(this._localCtx);
      var pMedium16 = this._mediumMixer.Predict(this._mediumCtx);
      var pWide16 = this._wideMixer.Predict(this._wideCtx);

      this._networkStretch[0] = Logistic.Stretch(pLocal16 >> (McmCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits));
      this._networkStretch[1] = Logistic.Stretch(pMedium16 >> (McmCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits));
      this._networkStretch[2] = Logistic.Stretch(pWide16 >> (McmCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits));

      long dot = 0;
      for (var i = 0; i < 3; ++i)
        dot += (long)this._networkWeights[i] * this._networkStretch[i];

      var logit = (int)(dot >> McmCompressor.WeightShift);
      var p12 = Logistic.Squash(logit);
      this._preApmProbability12 = p12;

      var refined1 = this._apm1.Refine(p12, this._history[0]);
      var apm2Context = ((this._history[0] << 4) ^ (this._history[1] >> 4)) & 0xFFF;
      var refined2 = this._apm2.Refine(refined1, apm2Context);

      var blended = (p12 + refined1 + 2 * refined2) >> 2;
      blended = Math.Clamp(blended, 1, Logistic.ProbabilityScale - 1);

      var p16 = blended << (McmCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits);
      return Math.Clamp(p16, 1, (1 << McmCompressor.ProbabilityScaleBits) - 1);
    }

    public void Update(int bit) {
      this._localMixer.Update(this._localCtx, bit);
      this._mediumMixer.Update(this._mediumCtx, bit);
      this._wideMixer.Update(this._wideCtx, bit);

      var error = (bit << Logistic.ProbabilityBits) - this._preApmProbability12;
      for (var i = 0; i < 3; ++i) {
        var grad = McmCompressor.LearningRate * error * this._networkStretch[i];
        this._networkWeights[i] += grad >> Logistic.ProbabilityBits;
      }

      this._apm1.Update(bit);
      this._apm2.Update(bit);
    }

    public void PushByte(int value) {
      for (var k = this._history.Length - 1; k > 0; --k)
        this._history[k] = this._history[k - 1];
      this._history[0] = value & 0xFF;
    }

    private void ComputeContexts(int c0) {
      for (var i = 0; i < McmState._LocalOrders.Length; ++i)
        this._localCtx[i] = this.HashOrder(McmState._LocalOrders[i], c0);

      for (var i = 0; i < McmState._MediumOrders.Length; ++i)
        this._mediumCtx[i] = this.HashOrder(McmState._MediumOrders[i], c0);

      this._wideCtx[0] = this.HashOrder(6, c0);
      // Sparse context: byte two positions back, skipping the immediate predecessor.
      var sparseSeed = (uint)(0xC2B2AE35u);
      var h = McmState.Mix(sparseSeed, (uint)this._history[1]);
      h = McmState.Mix(h, (uint)c0);
      this._wideCtx[1] = (int)(h & 0x7FFFFFFF);
    }

    private int HashOrder(int order, int c0) {
      var h = (uint)(order * 0x9E3779B1u);
      for (var k = 0; k < order; ++k)
        h = McmState.Mix(h, (uint)this._history[k]);

      h = McmState.Mix(h, (uint)c0);
      return (int)(h & 0x7FFFFFFF);
    }

    private static uint Mix(uint h, uint x) {
      h ^= x + 0x9E3779B1u + (h << 6) + (h >> 2);
      return h;
    }
  }
}
