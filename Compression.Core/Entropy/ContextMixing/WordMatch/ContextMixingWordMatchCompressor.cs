using System.Buffers.Binary;
using Compression.Core.Entropy.Arithmetic;

namespace Compression.Core.Entropy.ContextMixing.WordMatch;

/// <summary>
/// A context-mixing coder of this repository's own design: hashed byte-history contexts, a word
/// context and a match model, mixed by one logistic-domain mixer and refined by a two-stage SSE chain.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>hashed byte-history contexts, orders 0, 1, 2, 3, 4 and 6
///         (<see cref="ContextModel"/>, the same order set as <see cref="CmCompressor"/>);</item>
///   <item>one word context: a hash of the bytes since the last non-alphanumeric byte;</item>
///   <item>one match model (<see cref="ContextMixing.MatchModel"/>) predicting the next byte from the
///         longest recent repeat;</item>
///   <item>the eight predictions combined by one logistic-domain mixer and refined by two chained
///         <see cref="Apm"/> (SSE) stages.</item>
/// </list>
/// <para>Layout: 4-byte little-endian length, then the arithmetic-coded bits. No other program reads
/// or writes it.</para>
/// </remarks>
public static class ContextMixingWordMatchCompressor {
  private static readonly int[] _Orders = [0, 1, 2, 3, 4, 6];
  private static readonly int[] _OrderTableBits = [10, 16, 18, 21, 22, 22];
  private const int WordTableBits = 18;

  private const int ProbabilityScaleBits = 16;
  private const int WeightShift = 16;
  private const int LearningRate = 3;

  // 6 hashed byte-order models + 1 word model + 1 match model.
  private const int InputCount = 8;

  /// <summary>
  /// Compresses data using the word and match context-mixing model.
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
    var state = new WordMatchState(data.Length);

    foreach (int value in data) {
      var c0 = 1;
      for (var bit = 7; bit >= 0; --bit) {
        var bitVal = (value >> bit) & 1;
        var prob1 = state.Predict(c0, bit);
        encoder.EncodeBit(bitVal, (1 << ContextMixingWordMatchCompressor.ProbabilityScaleBits) - prob1);
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
    var state = new WordMatchState(size);

    var result = new byte[size];
    for (var i = 0; i < size; ++i) {
      var c0 = 1;
      for (var bit = 7; bit >= 0; --bit) {
        var prob1 = state.Predict(c0, bit);
        var bitVal = decoder.DecodeBit((1 << ContextMixingWordMatchCompressor.ProbabilityScaleBits) - prob1);
        state.Update(bitVal);
        c0 = (c0 << 1) | bitVal;
      }

      var b = c0 & 0xFF;
      result[i] = (byte)b;
      state.PushByte(b);
    }

    return result;
  }

  private sealed class WordMatchState {
    private readonly ContextModel[] _orderModels;
    private readonly ContextModel _wordModel;
    private readonly MatchModel _matchModel;

    private readonly int[] _weights = new int[ContextMixingWordMatchCompressor.InputCount];
    private readonly int[] _stretched = new int[ContextMixingWordMatchCompressor.InputCount];
    private readonly int[] _contexts = new int[ContextMixingWordMatchCompressor.InputCount - 1]; // order models + word model (match model keeps no context slot)

    private readonly int[] _history = new int[8];
    private uint _wordHash;

    private int _preApmProbability12;

    private readonly Apm _apm1 = new(256);
    private readonly Apm _apm2 = new(512);

    public WordMatchState(int capacity) {
      var count = ContextMixingWordMatchCompressor._Orders.Length;
      this._orderModels = new ContextModel[count];
      for (var i = 0; i < count; ++i)
        this._orderModels[i] = new ContextModel(ContextMixingWordMatchCompressor._OrderTableBits[i]);

      this._wordModel = new ContextModel(ContextMixingWordMatchCompressor.WordTableBits);
      this._matchModel = new MatchModel(capacity);

      this._weights.AsSpan().Fill((1 << ContextMixingWordMatchCompressor.WeightShift) / ContextMixingWordMatchCompressor.InputCount);
    }

    public int Predict(int c0, int bit) {
      for (var i = 0; i < this._orderModels.Length; ++i) {
        var order = ContextMixingWordMatchCompressor._Orders[i];
        var h = (uint)(order * 0x9E3779B1u);
        for (var k = 0; k < order; ++k)
          h = WordMatchState.Mix(h, (uint)this._history[k]);

        h = WordMatchState.Mix(h, (uint)c0);
        this._contexts[i] = (int)(h & 0x7FFFFFFF);
        this._stretched[i] = Logistic.Stretch(this._orderModels[i].Predict(this._contexts[i]));
      }

      var wordContext = (int)(WordMatchState.Mix(this._wordHash, (uint)c0) & 0x7FFFFFFF);
      this._contexts[^1] = wordContext;
      var wordIndex = this._orderModels.Length;
      this._stretched[wordIndex] = Logistic.Stretch(this._wordModel.Predict(wordContext));

      var matchIndex = wordIndex + 1;
      this._stretched[matchIndex] = WordMatchState.MatchStretch(this._matchModel, c0, bit);

      long dot = 0;
      for (var i = 0; i < ContextMixingWordMatchCompressor.InputCount; ++i)
        dot += (long)this._weights[i] * this._stretched[i];

      var logit = (int)(dot >> ContextMixingWordMatchCompressor.WeightShift);
      var p12 = Logistic.Squash(logit);
      this._preApmProbability12 = p12;

      var refined1 = this._apm1.Refine(p12, this._history[0]);
      var apm2Context = ((this._history[0] & 0xFF) ^ (this._matchModel.MatchLength > 0 ? 0x100 : 0)) & 0x1FF;
      var refined2 = this._apm2.Refine(refined1, apm2Context);

      var blended = (p12 + refined1 + 2 * refined2) >> 2;
      blended = Math.Clamp(blended, 1, Logistic.ProbabilityScale - 1);

      var p16 = blended << (ContextMixingWordMatchCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits);
      return Math.Clamp(p16, 1, (1 << ContextMixingWordMatchCompressor.ProbabilityScaleBits) - 1);
    }

    public void Update(int bit) {
      for (var i = 0; i < this._orderModels.Length; ++i)
        this._orderModels[i].Update(this._contexts[i], bit);

      this._wordModel.Update(this._contexts[^1], bit);

      var error = (bit << Logistic.ProbabilityBits) - this._preApmProbability12;
      for (var i = 0; i < ContextMixingWordMatchCompressor.InputCount; ++i) {
        var grad = ContextMixingWordMatchCompressor.LearningRate * error * this._stretched[i];
        this._weights[i] += grad >> Logistic.ProbabilityBits;
      }

      this._apm1.Update(bit);
      this._apm2.Update(bit);
    }

    public void PushByte(int value) {
      for (var k = this._history.Length - 1; k > 0; --k)
        this._history[k] = this._history[k - 1];
      this._history[0] = value & 0xFF;

      var c = (char)value;
      this._wordHash = char.IsLetterOrDigit(c)
        ? WordMatchState.Mix(this._wordHash == 0 ? 0x811C9DC5u : this._wordHash, (uint)value)
        : 0;

      this._matchModel.Append((byte)value);
    }

    private static int MatchStretch(MatchModel model, int c0, int bit) {
      var predicted = model.PredictedByte;
      if (predicted < 0)
        return 0;

      var placedBits = 7 - bit; // bits of the current byte already coded
      if (placedBits > 0) {
        var mask = (1 << placedBits) - 1;
        var actualPrefix = c0 & mask;
        var predictedPrefix = (predicted >> (8 - placedBits)) & mask;
        if (actualPrefix != predictedPrefix)
          return 0; // this byte has already diverged from the predicted one
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
