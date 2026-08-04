using System.Buffers.Binary;
using Compression.Core.Entropy.Arithmetic;

namespace Compression.Core.Entropy.ContextMixing.Paq8hp;

/// <summary>
/// A clean-room, deliberately reduced reimplementation of the PAQ8hp
/// architecture: hashed byte-order contexts and a match model mixed by
/// <em>context-selected</em> weight sets — one of PAQ8's signature techniques
/// — refined by an SSE stage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope honesty:</b> PAQ8hp (a PAQ8 variant tuned for the Hutter Prize /
/// enwik benchmark — see
/// <see href="http://mattmahoney.net/dc/paq8hp12any.zip"/> and
/// <see href="https://www.mattmahoney.net/dc/paq.html"/>) ships dozens of
/// specialised models (word, sparse, indirect, PPM-style, image/audio
/// detectors, and more) behind a large mixing network. This class implements
/// <b>none</b> of that. It is a small, explicitly documented subset, named
/// "reduced" so it is never mistaken for the reference compressor:
/// </para>
/// <list type="bullet">
///   <item>hashed byte-history contexts, orders 0, 1, 2, 3, 4 and 6
///         (<see cref="ContextModel"/>);</item>
///   <item>one match model (<see cref="ContextMixing.MatchModel"/>) predicting
///         the next byte from the longest recent repeat;</item>
///   <item><b>context-selected mixing</b>: 16 independent weight vectors, one
///         per high nibble of the previous byte, exactly the PAQ8 technique
///         of letting the mixer specialise per context rather than sharing one
///         global weight set (<see href="https://en.wikipedia.org/wiki/Context_mixing"/>);</item>
///   <item>a single <see cref="Apm"/> (SSE) stage keyed on an order-2 hash.</item>
/// </list>
/// </remarks>
public static class Paq8hpCompressor {
  private static readonly int[] _Orders = [0, 1, 2, 3, 4, 6];
  private static readonly int[] _OrderTableBits = [10, 16, 18, 21, 22, 22];

  private const int ProbabilityScaleBits = 16;
  private const int WeightShift = 16;
  private const int LearningRate = 3;
  private const int MixerContexts = 16; // selected by the previous byte's high nibble

  // 6 hashed byte-order models + 1 match model.
  private const int InputCount = 7;

  /// <summary>
  /// Compresses data using the reduced PAQ8hp-style model set.
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
    var state = new Paq8HpState(data.Length);

    foreach (int value in data) {
      var c0 = 1;
      for (var bit = 7; bit >= 0; --bit) {
        var bitVal = (value >> bit) & 1;
        var prob1 = state.Predict(c0, bit);
        encoder.EncodeBit(bitVal, (1 << Paq8hpCompressor.ProbabilityScaleBits) - prob1);
        state.Update(bitVal);
        c0 = (c0 << 1) | bitVal;
      }

      state.PushByte(value);
    }

    encoder.Finish();
    return output.ToArray();
  }

  /// <summary>
  /// Decompresses reduced-PAQ8hp-style compressed data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed) {
    var size = BinaryPrimitives.ReadInt32LittleEndian(compressed);
    if (size == 0)
      return [];

    using var input = new MemoryStream(compressed[4..].ToArray());
    var decoder = new ArithmeticDecoder(input);
    var state = new Paq8HpState(size);

    var result = new byte[size];
    for (var i = 0; i < size; ++i) {
      var c0 = 1;
      for (var bit = 7; bit >= 0; --bit) {
        var prob1 = state.Predict(c0, bit);
        var bitVal = decoder.DecodeBit((1 << Paq8hpCompressor.ProbabilityScaleBits) - prob1);
        state.Update(bitVal);
        c0 = (c0 << 1) | bitVal;
      }

      var b = c0 & 0xFF;
      result[i] = (byte)b;
      state.PushByte(b);
    }

    return result;
  }

  private sealed class Paq8HpState {
    private readonly ContextModel[] _orderModels;
    private readonly MatchModel _matchModel;

    // One weight vector per mixer context (PAQ8-style context-selected mixing).
    private readonly int[][] _weightSets;
    private readonly int[] _stretched = new int[Paq8hpCompressor.InputCount];
    private readonly int[] _contexts = new int[Paq8hpCompressor.InputCount - 1]; // order models only

    private readonly int[] _history = new int[8];
    private int _mixerContext;
    private int _preApmProbability12;

    private readonly Apm _apm = new(1 << 16);

    public Paq8HpState(int capacity) {
      var count = Paq8hpCompressor._Orders.Length;
      this._orderModels = new ContextModel[count];
      for (var i = 0; i < count; ++i)
        this._orderModels[i] = new ContextModel(Paq8hpCompressor._OrderTableBits[i]);

      this._matchModel = new MatchModel(capacity);

      this._weightSets = new int[Paq8hpCompressor.MixerContexts][];
      var initial = (1 << Paq8hpCompressor.WeightShift) / Paq8hpCompressor.InputCount;
      for (var c = 0; c < Paq8hpCompressor.MixerContexts; ++c) {
        this._weightSets[c] = new int[Paq8hpCompressor.InputCount];
        this._weightSets[c].AsSpan().Fill(initial);
      }
    }

    public int Predict(int c0, int bit) {
      for (var i = 0; i < this._orderModels.Length; ++i) {
        var order = Paq8hpCompressor._Orders[i];
        var h = (uint)(order * 0x9E3779B1u);
        for (var k = 0; k < order; ++k)
          h = Paq8HpState.Mix(h, (uint)this._history[k]);

        h = Paq8HpState.Mix(h, (uint)c0);
        this._contexts[i] = (int)(h & 0x7FFFFFFF);
        this._stretched[i] = Logistic.Stretch(this._orderModels[i].Predict(this._contexts[i]));
      }

      var matchIndex = this._orderModels.Length;
      this._stretched[matchIndex] = Paq8HpState.MatchStretch(this._matchModel, c0, bit);

      this._mixerContext = (this._history[0] >> 4) & (Paq8hpCompressor.MixerContexts - 1);
      var weights = this._weightSets[this._mixerContext];

      long dot = 0;
      for (var i = 0; i < Paq8hpCompressor.InputCount; ++i)
        dot += (long)weights[i] * this._stretched[i];

      var logit = (int)(dot >> Paq8hpCompressor.WeightShift);
      var p12 = Logistic.Squash(logit);
      this._preApmProbability12 = p12;

      var apmContext = ((this._history[0] << 8) ^ this._history[1]) & 0xFFFF;
      var refined = this._apm.Refine(p12, apmContext);

      var blended = (p12 + 3 * refined) >> 2;
      blended = Math.Clamp(blended, 1, Logistic.ProbabilityScale - 1);

      var p16 = blended << (Paq8hpCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits);
      return Math.Clamp(p16, 1, (1 << Paq8hpCompressor.ProbabilityScaleBits) - 1);
    }

    public void Update(int bit) {
      for (var i = 0; i < this._orderModels.Length; ++i)
        this._orderModels[i].Update(this._contexts[i], bit);

      var error = (bit << Logistic.ProbabilityBits) - this._preApmProbability12;
      var weights = this._weightSets[this._mixerContext];
      for (var i = 0; i < Paq8hpCompressor.InputCount; ++i) {
        var grad = Paq8hpCompressor.LearningRate * error * this._stretched[i];
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
