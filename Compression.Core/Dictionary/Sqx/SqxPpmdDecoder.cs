using Compression.Core.Entropy.Ppmd;

namespace Compression.Core.Dictionary.Sqx;

/// <summary>
/// SQX's carryless range decoder as method 6 drives it: an absolute code over bytes taken from
/// the SQX bit reader, 2^24 top and 2^16 bottom. Converted from Squeez-c (MIT) src/ppm8.c.
/// </summary>
internal sealed class SqxPpmdRangeDecoder {
  private const uint Top = 1u << 24, Bottom = 1u << 16;
  private readonly SqxBitReader _bits;
  public uint Low, Range = 0xFFFFFFFFu, Code;

  public SqxPpmdRangeDecoder(byte[] data) {
    this._bits = new SqxBitReader(data, 0, data.Length);
    for (var i = 0; i < 4; ++i)
      this.Code = (this.Code << 8) | this._bits.Read(8);
  }

  /// <summary>Bits taken from the stream so far.</summary>
  public long BitsRead => this._bits.Position;

  /// <summary>Where the code lies above the interval's low end.</summary>
  public uint Offset => this.Code - this.Low;

  public void Normalize() {
    // a range that collapsed to nothing only comes from data no encoder wrote
    if (this.Range == 0)
      throw new InvalidDataException("SQX: the text stream is malformed.");
    while (true) {
      if ((this.Low ^ (this.Low + this.Range)) >= Top) {
        if (this.Range >= Bottom)
          return;
        this.Range = (0u - this.Low) & (Bottom - 1);
        if (this.Range == 0)
          throw new InvalidDataException("SQX: the text stream is malformed.");
      }
      this.Code = (this.Code << 8) | this._bits.Read(8);
      this.Range <<= 8;
      this.Low <<= 8;
    }
  }

  /// <summary>Scales the range to <paramref name="total"/> and returns where the code falls.</summary>
  public uint Threshold(uint total) {
    if (total == 0)
      return uint.MaxValue;
    this.Range /= total;
    return this.Range == 0 ? uint.MaxValue : (this.Code - this.Low) / this.Range;
  }

  /// <summary>Narrows to [start, start + size) of the scaled range (no normalization).</summary>
  public void Decode(uint start, uint size) {
    this.Low += this.Range * start;
    this.Range *= size;
  }

  /// <summary><paramref name="count"/> equiprobable bits, most significant first.</summary>
  public uint Bits(int count) {
    var value = 0u;
    while (count-- > 0) {
      var half = this.Range >> 1;
      this.Range = half;
      if (half == 0)
        return value;
      var q = (this.Code - this.Low) / half;
      value = (value << 1) | q;
      this.Low += q * half;
      this.Normalize();
    }
    return value;
  }
}

/// <summary>
/// SQX 2.0 method 6 ("text"): PPMd var. I driven by SQX's carryless range coder. Each stream
/// starts with a flag bit: 0 opens a new model (8-bit order, 10-bit memory in MiB), 1 continues
/// the model of the file before (solid). Converted from Squeez-c (MIT) src/ppm8.c; the model is
/// <see cref="Ppmd8"/> with the cut-off restore its memory runs out into.
/// </summary>
public sealed class SqxPpmdDecoder {
  /// <summary>The largest model memory this decoder allocates, in MiB.</summary>
  public const int MaxMemoryMb = 256;

  private Ppmd8? _model;

  /// <summary>Decodes one file of <paramref name="originalSize"/> bytes.</summary>
  /// <exception cref="InvalidDataException">The stream is malformed, or continues a model no earlier file started.</exception>
  /// <exception cref="NotSupportedException">The model asks for more than <see cref="MaxMemoryMb"/> MiB.</exception>
  public byte[] Decode(byte[] packed, long originalSize) {
    ArgumentNullException.ThrowIfNull(packed);
    if (originalSize is < 0 or > int.MaxValue)
      throw new NotSupportedException("SQX: text files over 2 GiB are not supported.");
    if (packed.Length == 0 && originalSize == 0)
      return [];
    var rc = new SqxPpmdRangeDecoder(packed);
    if (rc.Bits(1) == 0) {
      var order = (int)rc.Bits(8);
      var memory = (int)rc.Bits(10);
      if (order is < Ppmd8.MinOrder or > Ppmd8.MaxOrderLimit || memory == 0)
        throw new InvalidDataException($"SQX: a text stream with order {order} and {memory} MiB is malformed.");
      if (memory > MaxMemoryMb)
        throw new NotSupportedException($"SQX: a text model of {memory} MiB is more than this decoder allocates.");
      this._model = new Ppmd8(memory << 20, order, Ppmd8RestoreMethod.CutOff);
    } else if (this._model == null)
      throw new InvalidDataException("SQX: a text stream continues a model no earlier file started.");

    var output = new byte[originalSize];
    for (var i = 0; i < output.Length; ++i) {
      var symbol = this._model.DecodeSymbolSqx(rc);
      if (symbol < 0)
        throw new InvalidDataException("SQX: the text stream is malformed.");
      if (rc.BitsRead > (packed.Length + 16L) * 8)
        throw new InvalidDataException("SQX: the text stream ends early.");
      output[i] = (byte)symbol;
    }
    return output;
  }
}
