using System.Buffers.Binary;
using Compression.Core.Dictionary.Lz77;
using Compression.Core.Dictionary.MatchFinders;
using Compression.Core.Entropy.Arithmetic;

namespace Compression.Core.Entropy.ContextMixing.Csc;

/// <summary>
/// A clean-room implementation of the CSC architecture: LZ77 parsing whose
/// literal and flag streams are entropy-coded with logistic-domain context mixing.
/// </summary>
/// <remarks>
/// <para>
/// Modelled after Fu Siyuan's CSC (<see href="https://github.com/fusiyuan2010/CSC"/>),
/// which pairs an LZ77-family match finder with a context-modelled range coder
/// instead of static/canonical Huffman tables. See also the general LZ77
/// description (<see href="https://en.wikipedia.org/wiki/LZ77_and_LZ78"/>) and
/// Mahoney's context-modelling notes
/// (<see href="https://mattmahoney.net/dc/dce.html#Section_43"/>).
/// </para>
/// <para>
/// This is a reduced, from-specification reimplementation: <see cref="Lz77Compressor"/>
/// (hash-chain match finder, 32 KiB window, 3-258 byte matches) produces the
/// token stream. Every token starts with a match/literal flag bit predicted by
/// two hashed models over the last one/two flags, mixed by a
/// <see cref="ContextMixer"/>. Literal bytes are coded bit-by-bit with an
/// order-0/order-1 <see cref="ContextMixer"/> (context = previous output byte)
/// refined by an <see cref="Apm"/>; match length and distance — which carry
/// little redundancy beyond what the match finder already removed — are coded
/// through simple order-0 adaptive bit-trees built on the same
/// <see cref="ContextModel"/>/<see cref="Arithmetic.ArithmeticEncoder"/>
/// primitives. All four channels share one bitstream, interleaved in token order.
/// </para>
/// </remarks>
public static class CscCompressor {
  private const int WindowSize = 32768;
  private const int MaxMatchLength = 258;
  private const int MinMatchLength = 3;
  private const int ProbabilityScaleBits = 16;

  /// <summary>
  /// Compresses data via LZ77 parsing with a context-mixed entropy back end.
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

    var matchFinder = new HashChainMatchFinder(CscCompressor.WindowSize);
    var parser = new Lz77Compressor(matchFinder, CscCompressor.WindowSize, CscCompressor.MaxMatchLength, CscCompressor.MinMatchLength);
    var tokens = parser.Compress(data);

    var encoder = new ArithmeticEncoder(output);
    var state = new CscState();

    var position = 0;
    foreach (var token in tokens) {
      state.EncodeFlag(encoder, token.IsLiteral ? 0 : 1);

      if (token.IsLiteral) {
        state.EncodeLiteral(encoder, token.Literal);
        ++position;
      }
      else {
        state.EncodeLength(encoder, token.Length - CscCompressor.MinMatchLength);
        state.EncodeDistance(encoder, token.Distance - 1);
        // Track the context byte through the copied region without re-deriving
        // it from the token, so encoder and decoder stay in lockstep.
        for (var i = 0; i < token.Length; ++i)
          state.PushLiteralByte(data[position + i]);
        position += token.Length;
      }
    }

    encoder.Finish();
    return output.ToArray();
  }

  /// <summary>
  /// Decompresses CSC-style compressed data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed) {
    var size = BinaryPrimitives.ReadInt32LittleEndian(compressed);
    if (size == 0)
      return [];

    using var input = new MemoryStream(compressed[4..].ToArray());
    var decoder = new ArithmeticDecoder(input);
    var state = new CscState();

    var result = new byte[size];
    var position = 0;
    while (position < size) {
      var isMatch = state.DecodeFlag(decoder);

      if (isMatch == 0) {
        var literal = state.DecodeLiteral(decoder);
        result[position++] = literal;
      }
      else {
        var length = state.DecodeLength(decoder) + CscCompressor.MinMatchLength;
        var distance = state.DecodeDistance(decoder) + 1;
        var src = position - distance;
        for (var i = 0; i < length; ++i) {
          var b = result[src + i];
          result[position++] = b;
          state.PushLiteralByte(b);
        }
      }
    }

    return result;
  }

  /// <summary>
  /// Holds every channel's model state, shared identically by the encoder and decoder.
  /// </summary>
  private sealed class CscState {
    // Flag channel: mixes order-1 (last flag) and order-2 (last two flags) contexts.
    private readonly ContextModel[] _flagModels = [new(2), new(4)];
    private readonly ContextMixer _flagMixer;
    private int _flagHistory; // low 2 bits = last two flags, MSB-first

    // Literal channel: order-0 and order-1 (previous output byte) contexts.
    private readonly ContextModel[] _literalModels = [new(9), new(16)];
    private readonly ContextMixer _literalMixer;
    private readonly Apm _literalApm = new(256);
    private int _previousByte;

    private readonly int[] _flagContexts = new int[2];
    private readonly int[] _literalContexts = new int[2];

    // Length/distance channels: order-0 adaptive bit-trees (context = c0 directly).
    private readonly ContextModel _lengthModel = new(9);
    private readonly ContextModel _distanceModel = new(17);

    public CscState() {
      this._flagMixer = new ContextMixer(this._flagModels);
      this._literalMixer = new ContextMixer(this._literalModels);
    }

    public void PushLiteralByte(byte value) => this._previousByte = value;

    public void EncodeFlag(ArithmeticEncoder encoder, int bit) {
      this.ComputeFlagContexts();
      var prob1 = this.PredictFlag();
      encoder.EncodeBit(bit, (1 << CscCompressor.ProbabilityScaleBits) - prob1);
      this.UpdateFlag(bit);
    }

    public int DecodeFlag(ArithmeticDecoder decoder) {
      this.ComputeFlagContexts();
      var prob1 = this.PredictFlag();
      var bit = decoder.DecodeBit((1 << CscCompressor.ProbabilityScaleBits) - prob1);
      this.UpdateFlag(bit);
      return bit;
    }

    private void ComputeFlagContexts() {
      this._flagContexts[0] = this._flagHistory & 0x1;
      this._flagContexts[1] = this._flagHistory & 0x3;
    }

    private int PredictFlag() {
      var mixed16 = this._flagMixer.Predict(this._flagContexts);
      return Math.Clamp(mixed16, 1, (1 << CscCompressor.ProbabilityScaleBits) - 1);
    }

    private void UpdateFlag(int bit) {
      this._flagMixer.Update(this._flagContexts, bit);
      this._flagHistory = ((this._flagHistory << 1) | bit) & 0x3;
    }

    public void EncodeLiteral(ArithmeticEncoder encoder, byte value) {
      var c0 = 1;
      for (var bit = 7; bit >= 0; --bit) {
        var bitVal = (value >> bit) & 1;
        this.ComputeLiteralContexts(c0);
        var prob1 = this.PredictLiteral(c0);
        encoder.EncodeBit(bitVal, (1 << CscCompressor.ProbabilityScaleBits) - prob1);
        this.UpdateLiteral(bitVal);
        c0 = (c0 << 1) | bitVal;
      }

      this._previousByte = value;
    }

    public byte DecodeLiteral(ArithmeticDecoder decoder) {
      var c0 = 1;
      for (var bit = 7; bit >= 0; --bit) {
        this.ComputeLiteralContexts(c0);
        var prob1 = this.PredictLiteral(c0);
        var bitVal = decoder.DecodeBit((1 << CscCompressor.ProbabilityScaleBits) - prob1);
        this.UpdateLiteral(bitVal);
        c0 = (c0 << 1) | bitVal;
      }

      var b = (byte)(c0 & 0xFF);
      this._previousByte = b;
      return b;
    }

    private void ComputeLiteralContexts(int c0) {
      this._literalContexts[0] = c0 & 0x1FF;
      this._literalContexts[1] = (int)(((uint)this._previousByte * 0x9E3779B1u) ^ (uint)c0) & 0xFFFF;
    }

    private int PredictLiteral(int c0) {
      _ = c0;
      var mixed16 = this._literalMixer.Predict(this._literalContexts);
      var mixed12 = mixed16 >> (CscCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits);
      var refined12 = this._literalApm.Refine(mixed12, this._previousByte);
      var blended12 = (mixed12 + refined12) >> 1;
      blended12 = Math.Clamp(blended12, 1, Logistic.ProbabilityScale - 1);
      var p16 = blended12 << (CscCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits);
      return Math.Clamp(p16, 1, (1 << CscCompressor.ProbabilityScaleBits) - 1);
    }

    private void UpdateLiteral(int bit) {
      this._literalMixer.Update(this._literalContexts, bit);
      this._literalApm.Update(bit);
    }

    public void EncodeLength(ArithmeticEncoder encoder, int value) =>
      CscState.EncodeOrderZero(encoder, this._lengthModel, value, 8);

    public int DecodeLength(ArithmeticDecoder decoder) =>
      CscState.DecodeOrderZero(decoder, this._lengthModel, 8);

    public void EncodeDistance(ArithmeticEncoder encoder, int value) =>
      CscState.EncodeOrderZero(encoder, this._distanceModel, value, 16);

    public int DecodeDistance(ArithmeticDecoder decoder) =>
      CscState.DecodeOrderZero(decoder, this._distanceModel, 16);

    private static void EncodeOrderZero(ArithmeticEncoder encoder, ContextModel model, int value, int numBits) {
      var c0 = 1;
      for (var bit = numBits - 1; bit >= 0; --bit) {
        var bitVal = (value >> bit) & 1;
        var prob1 = model.Predict(c0) << (CscCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits);
        prob1 = Math.Clamp(prob1, 1, (1 << CscCompressor.ProbabilityScaleBits) - 1);
        encoder.EncodeBit(bitVal, (1 << CscCompressor.ProbabilityScaleBits) - prob1);
        model.Update(c0, bitVal);
        c0 = (c0 << 1) | bitVal;
      }
    }

    private static int DecodeOrderZero(ArithmeticDecoder decoder, ContextModel model, int numBits) {
      var c0 = 1;
      for (var bit = numBits - 1; bit >= 0; --bit) {
        var prob1 = model.Predict(c0) << (CscCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits);
        prob1 = Math.Clamp(prob1, 1, (1 << CscCompressor.ProbabilityScaleBits) - 1);
        var bitVal = decoder.DecodeBit((1 << CscCompressor.ProbabilityScaleBits) - prob1);
        model.Update(c0, bitVal);
        c0 = (c0 << 1) | bitVal;
      }

      return c0 - (1 << numBits);
    }
  }
}
