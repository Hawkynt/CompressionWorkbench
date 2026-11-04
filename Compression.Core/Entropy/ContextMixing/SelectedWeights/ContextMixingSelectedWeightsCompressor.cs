using System.Buffers.Binary;
using Compression.Core.Entropy.Arithmetic;

namespace Compression.Core.Entropy.ContextMixing.SelectedWeights;

/// <summary>
/// A context-mixing coder of this repository's own design: hashed byte-history contexts and a match
/// model mixed by weight sets that the previous byte selects, refined by one SSE stage.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>hashed byte-history contexts, orders 0, 1, 2, 3, 4 and 6 (<see cref="ContextModel"/>);</item>
///   <item>one match model (<see cref="ContextMixing.MatchModel"/>) predicting the next byte from the
///         longest recent repeat;</item>
///   <item>16 independent mixer weight vectors, one per high nibble of the previous byte, so the
///         mixer specialises per context instead of sharing one global weight set;</item>
///   <item>one <see cref="Apm"/> (SSE) stage keyed on an order-2 hash.</item>
/// </list>
/// <para>Layout: 4-byte little-endian length, then the arithmetic-coded bits. No other program reads
/// or writes it.</para>
/// </remarks>
public static class ContextMixingSelectedWeightsCompressor {
  private static readonly int[] _Orders = [0, 1, 2, 3, 4, 6];
  private static readonly int[] _OrderTableBits = [10, 16, 18, 21, 22, 22];

  private const int ProbabilityScaleBits = 16;
  private const int WeightShift = 16;
  private const int LearningRate = 3;
  private const int MixerContexts = 16; // selected by the previous byte's high nibble

  // 6 hashed byte-order models + 1 match model.
  private const int InputCount = 7;

  /// <summary>
  /// Compresses data using the context-selected-weight model.
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
    var state = new SelectedWeightsState(data.Length);

    foreach (int value in data) {
      var c0 = 1;
      for (var bit = 7; bit >= 0; --bit) {
        var bitVal = (value >> bit) & 1;
        var prob1 = state.Predict(c0, bit);
        encoder.EncodeBit(bitVal, (1 << ContextMixingSelectedWeightsCompressor.ProbabilityScaleBits) - prob1);
        state.Update(bitVal);
        c0 = (c0 << 1) | bitVal;
      }

      state.PushByte(value);
    }

    encoder.Finish();
    return output.ToArray();
  }

  /// <summary>
  /// Decompresses data written by <see cref="Compress"/>.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed) {
    var size = BinaryPrimitives.ReadInt32LittleEndian(compressed);
    if (size == 0)
      return [];

    using var input = new MemoryStream(compressed[4..].ToArray());
    var decoder = new ArithmeticDecoder(input);
    var state = new SelectedWeightsState(size);

    var result = new byte[size];
    for (var i = 0; i < size; ++i) {
      var c0 = 1;
      for (var bit = 7; bit >= 0; --bit) {
        var prob1 = state.Predict(c0, bit);
        var bitVal = decoder.DecodeBit((1 << ContextMixingSelectedWeightsCompressor.ProbabilityScaleBits) - prob1);
        state.Update(bitVal);
        c0 = (c0 << 1) | bitVal;
      }

      var b = c0 & 0xFF;
      result[i] = (byte)b;
      state.PushByte(b);
    }

    return result;
  }

  private sealed class SelectedWeightsState {
    private readonly ContextModel[] _orderModels;
    private readonly MatchModel _matchModel;

    // One weight vector per mixer context (PAQ8-style context-selected mixing).
    private readonly int[][] _weightSets;
    private readonly int[] _stretched = new int[ContextMixingSelectedWeightsCompressor.InputCount];
    private readonly int[] _contexts = new int[ContextMixingSelectedWeightsCompressor.InputCount - 1]; // order models only

    private readonly int[] _history = new int[8];
    private int _mixerContext;
    private int _preApmProbability12;

    private readonly Apm _apm = new(1 << 16);

    public SelectedWeightsState(int capacity) {
      var count = ContextMixingSelectedWeightsCompressor._Orders.Length;
      this._orderModels = new ContextModel[count];
      for (var i = 0; i < count; ++i)
        this._orderModels[i] = new ContextModel(ContextMixingSelectedWeightsCompressor._OrderTableBits[i]);

      this._matchModel = new MatchModel(capacity);

      this._weightSets = new int[ContextMixingSelectedWeightsCompressor.MixerContexts][];
      var initial = (1 << ContextMixingSelectedWeightsCompressor.WeightShift) / ContextMixingSelectedWeightsCompressor.InputCount;
      for (var c = 0; c < ContextMixingSelectedWeightsCompressor.MixerContexts; ++c) {
        this._weightSets[c] = new int[ContextMixingSelectedWeightsCompressor.InputCount];
        this._weightSets[c].AsSpan().Fill(initial);
      }
    }

    public int Predict(int c0, int bit) {
      for (var i = 0; i < this._orderModels.Length; ++i) {
        var order = ContextMixingSelectedWeightsCompressor._Orders[i];
        var h = (uint)(order * 0x9E3779B1u);
        for (var k = 0; k < order; ++k)
          h = SelectedWeightsState.Mix(h, (uint)this._history[k]);

        h = SelectedWeightsState.Mix(h, (uint)c0);
        this._contexts[i] = (int)(h & 0x7FFFFFFF);
        this._stretched[i] = Logistic.Stretch(this._orderModels[i].Predict(this._contexts[i]));
      }

      var matchIndex = this._orderModels.Length;
      this._stretched[matchIndex] = SelectedWeightsState.MatchStretch(this._matchModel, c0, bit);

      this._mixerContext = (this._history[0] >> 4) & (ContextMixingSelectedWeightsCompressor.MixerContexts - 1);
      var weights = this._weightSets[this._mixerContext];

      long dot = 0;
      for (var i = 0; i < ContextMixingSelectedWeightsCompressor.InputCount; ++i)
        dot += (long)weights[i] * this._stretched[i];

      var logit = (int)(dot >> ContextMixingSelectedWeightsCompressor.WeightShift);
      var p12 = Logistic.Squash(logit);
      this._preApmProbability12 = p12;

      var apmContext = ((this._history[0] << 8) ^ this._history[1]) & 0xFFFF;
      var refined = this._apm.Refine(p12, apmContext);

      var blended = (p12 + 3 * refined) >> 2;
      blended = Math.Clamp(blended, 1, Logistic.ProbabilityScale - 1);

      var p16 = blended << (ContextMixingSelectedWeightsCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits);
      return Math.Clamp(p16, 1, (1 << ContextMixingSelectedWeightsCompressor.ProbabilityScaleBits) - 1);
    }

    public void Update(int bit) {
      for (var i = 0; i < this._orderModels.Length; ++i)
        this._orderModels[i].Update(this._contexts[i], bit);

      var error = (bit << Logistic.ProbabilityBits) - this._preApmProbability12;
      var weights = this._weightSets[this._mixerContext];
      for (var i = 0; i < ContextMixingSelectedWeightsCompressor.InputCount; ++i) {
        var grad = ContextMixingSelectedWeightsCompressor.LearningRate * error * this._stretched[i];
        weights[i] += grad >> Logistic.ProbabilityBits;
      }

      this._apm.Update(bit);
    }

    public void PushByte(int value) {
      for (var k = this._history.Length - 1; k > 0; --k)
        this._history[k] = this._history[k - 1];
      this._history[0] = value & 0xFF;

      this._matchModel.Append((byte)value);
    }

    private static int MatchStretch(MatchModel model, int c0, int bit) {
      var predicted = model.PredictedByte;
      if (predicted < 0)
        return 0;

      var placedBits = 7 - bit;
      if (placedBits > 0) {
        var mask = (1 << placedBits) - 1;
        var actualPrefix = c0 & mask;
        var predictedPrefix = (predicted >> (8 - placedBits)) & mask;
        if (actualPrefix != predictedPrefix)
          return 0;
      }

      var predictedBit = (predicted >> bit) & 1;
      var confidence = Math.Min(model.MatchLength, 28) * 64;
      return predictedBit == 1 ? confidence : -confidence;
    }

    private static uint Mix(uint h, uint x) {
      h ^= x + 0x9E3779B1u + (h << 6) + (h >> 2);
      return h;
    }
  }
}
