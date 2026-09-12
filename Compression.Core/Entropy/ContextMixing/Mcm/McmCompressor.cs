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
/// Modelled after Mathieu Chartier's MCM (<see href="https://github.com/mathieuchartier/mcm"/>),
/// discussed at <see href="https://encode.su/threads/2121-MCM-new-compressor-by-Mathieu-Chartier"/>),
/// which mixes multiple specialised context maps through a network of mixers
/// rather than one flat mixer over every model. See also
/// <see href="https://en.wikipedia.org/wiki/Context_mixing"/> for the general
/// technique.
/// </para>
/// <para>
/// This is a reduced, from-specification reimplementation. The public
/// <see cref="McmCompressionProfile"/> modes progressively enable more of the
/// independently designed model graph: local contexts, medium contexts, wide
/// and sparse contexts, then one or two SSE/APM refinement stages. The profile
/// names follow MCM's documented command-line modes; no upstream implementation
/// code is copied or translated.
/// </para>
/// <para>
/// At the maximum profile the model graph is the historical managed MCM graph:
/// local orders 0, 1 and 2; medium orders 3 and 4; a wide order-6 context plus
/// a sparse skip-1 context; a second-level mixer; and two chained
/// <see cref="Apm"/> refinement stages.
/// </para>
/// </remarks>
public static class McmCompressor {
  private const int ProbabilityScaleBits = 16;
  private const int WeightShift = 16;
  private const int LearningRate = 3;

  /// <summary>
  /// Compresses data using the full reduced two-level context-mixing network.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) => Compress(data, McmCompressionProfile.Max);

  /// <summary>
  /// Compresses data using the selected reduced MCM profile.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="profile">Amount of model structure to enable.</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data, McmCompressionProfile profile) {
    ValidateProfile(profile);

    using var output = new MemoryStream();

    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    output.Write(header);

    if (data.Length == 0)
      return output.ToArray();

    var encoder = new ArithmeticEncoder(output);
    var state = new McmState(profile);

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
  /// Decompresses data produced by the full reduced MCM profile.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed) => Decompress(compressed, McmCompressionProfile.Max);

  /// <summary>
  /// Decompresses data produced by the selected reduced MCM profile.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="profile">Profile that was used for compression.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> compressed, McmCompressionProfile profile) {
    ValidateProfile(profile);
    if (compressed.Length < sizeof(int))
      throw new InvalidDataException("Truncated reduced MCM stream: missing size header.");

    var size = BinaryPrimitives.ReadInt32LittleEndian(compressed);
    if (size < 0)
      throw new InvalidDataException("Invalid reduced MCM stream: negative uncompressed size.");
    if (size == 0)
      return [];

    using var input = new MemoryStream(compressed[4..].ToArray());
    var decoder = new ArithmeticDecoder(input);
    var state = new McmState(profile);

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

  private static void ValidateProfile(McmCompressionProfile profile) {
    if (!Enum.IsDefined(profile))
      throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown MCM compression profile.");
  }

  /// <summary>
  /// The two-level mixing network. Profiles progressively add model groups and
  /// SSE/APM refinement stages so the caller can trade CPU/memory for coding
  /// density without changing the entropy-coder contract.
  /// </summary>
  private sealed class McmState {
    private static readonly int[] _LocalOrders = [0, 1, 2];
    private static readonly int[] _MediumOrders = [3, 4];

    private readonly ContextMixer _localMixer;
    private readonly ContextMixer? _mediumMixer;
    private readonly ContextMixer? _wideMixer;

    private readonly int[] _localCtx = new int[3];
    private readonly int[] _mediumCtx = new int[2];
    private readonly int[] _wideCtx = new int[2];

    private readonly int[] _networkWeights;
    private readonly int[] _networkStretch;
    private int _preApmProbability12;

    private readonly Apm? _apm1;
    private readonly Apm? _apm2;

    private readonly int[] _history = new int[8];

    public McmState(McmCompressionProfile profile) {
      var local = new ContextModel[] { new(9), new(16), new(20) };
      this._localMixer = new ContextMixer(local);

      var rank = (int)profile;
      if (rank >= (int)McmCompressionProfile.Fast) {
        var medium = new ContextModel[] { new(21), new(22) };
        this._mediumMixer = new ContextMixer(medium);
      }

      if (rank >= (int)McmCompressionProfile.Mid) {
        var wide = new ContextModel[] { new(22), new(18) };
        this._wideMixer = new ContextMixer(wide);
      }

      if (rank >= (int)McmCompressionProfile.High)
        this._apm1 = new Apm(256);
      if (rank >= (int)McmCompressionProfile.Max)
        this._apm2 = new Apm(1 << 12);

      var groupCount = 1 + (this._mediumMixer is null ? 0 : 1) + (this._wideMixer is null ? 0 : 1);
      this._networkWeights = new int[groupCount];
      this._networkStretch = new int[groupCount];
      this._networkWeights.AsSpan().Fill((1 << McmCompressor.WeightShift) / groupCount);
    }

    public int Predict(int c0) {
      this.ComputeContexts(c0);

      var group = 0;
      this._networkStretch[group++] = Logistic.Stretch(
        this._localMixer.Predict(this._localCtx) >> (McmCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits));

      if (this._mediumMixer is { } mediumMixer)
        this._networkStretch[group++] = Logistic.Stretch(
          mediumMixer.Predict(this._mediumCtx) >> (McmCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits));

      if (this._wideMixer is { } wideMixer)
        this._networkStretch[group] = Logistic.Stretch(
          wideMixer.Predict(this._wideCtx) >> (McmCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits));

      long dot = 0;
      for (var i = 0; i < this._networkWeights.Length; ++i)
        dot += (long)this._networkWeights[i] * this._networkStretch[i];

      var logit = (int)(dot >> McmCompressor.WeightShift);
      var p12 = Logistic.Squash(logit);
      this._preApmProbability12 = p12;

      var blended = p12;
      if (this._apm1 is { } apm1) {
        var refined1 = apm1.Refine(p12, this._history[0]);
        blended = (p12 + refined1) >> 1;

        if (this._apm2 is { } apm2) {
          var apm2Context = ((this._history[0] << 4) ^ (this._history[1] >> 4)) & 0xFFF;
          var refined2 = apm2.Refine(refined1, apm2Context);
          blended = (p12 + refined1 + 2 * refined2) >> 2;
        }
      }

      blended = Math.Clamp(blended, 1, Logistic.ProbabilityScale - 1);
      var p16 = blended << (McmCompressor.ProbabilityScaleBits - Logistic.ProbabilityBits);
      return Math.Clamp(p16, 1, (1 << McmCompressor.ProbabilityScaleBits) - 1);
    }

    public void Update(int bit) {
      this._localMixer.Update(this._localCtx, bit);
      this._mediumMixer?.Update(this._mediumCtx, bit);
      this._wideMixer?.Update(this._wideCtx, bit);

      var error = (bit << Logistic.ProbabilityBits) - this._preApmProbability12;
      for (var i = 0; i < this._networkWeights.Length; ++i) {
        var grad = McmCompressor.LearningRate * error * this._networkStretch[i];
        this._networkWeights[i] += grad >> Logistic.ProbabilityBits;
      }

      this._apm1?.Update(bit);
      this._apm2?.Update(bit);
    }

    public void PushByte(int value) {
      for (var k = this._history.Length - 1; k > 0; --k)
        this._history[k] = this._history[k - 1];
      this._history[0] = value & 0xFF;
    }

    private void ComputeContexts(int c0) {
      for (var i = 0; i < McmState._LocalOrders.Length; ++i)
        this._localCtx[i] = this.HashOrder(McmState._LocalOrders[i], c0);

      if (this._mediumMixer is not null)
        for (var i = 0; i < McmState._MediumOrders.Length; ++i)
          this._mediumCtx[i] = this.HashOrder(McmState._MediumOrders[i], c0);

      if (this._wideMixer is not null) {
        this._wideCtx[0] = this.HashOrder(6, c0);
        var sparseSeed = 0xC2B2AE35u;
        var h = McmState.Mix(sparseSeed, (uint)this._history[1]);
        h = McmState.Mix(h, (uint)c0);
        this._wideCtx[1] = (int)(h & 0x7FFFFFFF);
      }
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
