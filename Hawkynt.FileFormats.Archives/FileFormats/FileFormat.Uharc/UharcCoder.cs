namespace FileFormat.Uharc;

/// <summary>
/// One side of the range coder, so a model can be written once and both decode and encode: every
/// call takes the value the encoder wants coded and returns the value the decoder read. Models are
/// generic over a struct implementing this, so the decoding instantiation compiles to direct calls
/// and pays nothing for the encoder's arguments, which it computes only when
/// <see cref="Encoding"/> is set.
/// </summary>
internal interface IUharcCoder {

  /// <summary>True for the encoder: callers must supply the values to code.</summary>
  static abstract bool Encoding { get; }

  /// <summary>
  /// Scales to <paramref name="total"/> slots and returns the slot of the code; the encoder
  /// returns <paramref name="target"/>, a slot inside the interval it will consume.
  /// </summary>
  uint Count(uint total, uint target);

  /// <summary>Narrows to <c>[low, low + size)</c> of the slots set by the last <see cref="Count"/>.</summary>
  void Consume(uint low, uint size);

  /// <summary>An equiprobable value in <c>[0, total)</c>.</summary>
  uint Uniform(uint total, uint value);

  /// <summary>One bit against a 12-bit probability of zero, adapted by <paramref name="shift"/>.</summary>
  int Bit(ref ushort probability, int shift, int bit);

  /// <summary>
  /// A binary decision whose first outcome takes <paramref name="probability"/> of
  /// <paramref name="total"/> slots; true for the first outcome.
  /// </summary>
  bool Scaled(uint probability, uint total, bool first);
}

/// <summary>The decoding side: forwards to <see cref="UharcRangeDecoder"/> and ignores the targets.</summary>
internal readonly struct UharcDecoding(UharcRangeDecoder coder) : IUharcCoder {

  public static bool Encoding => false;

  public uint Count(uint total, uint target) => coder.GetCount(total);

  public void Consume(uint low, uint size) => coder.Consume(low, size);

  public uint Uniform(uint total, uint value) => coder.DecodeUniform(total);

  public int Bit(ref ushort probability, int shift, int bit) => coder.DecodeBit(ref probability, shift);

  public bool Scaled(uint probability, uint total, bool first) => coder.DecodeScaled(probability, total);
}

/// <summary>The encoding side: codes the targets with <see cref="UharcRangeEncoder"/>.</summary>
internal readonly struct UharcEncoding(UharcRangeEncoder coder) : IUharcCoder {

  public static bool Encoding => true;

  public uint Count(uint total, uint target) {
    if (target >= total)
      throw new InvalidOperationException("UHARC: encoder target outside its interval.");
    coder.PendingTotal = total;
    return target;
  }

  public void Consume(uint low, uint size) => coder.Encode(low, size, coder.PendingTotal);

  public uint Uniform(uint total, uint value) {
    coder.Encode(value, 1, total);
    return value;
  }

  public int Bit(ref ushort probability, int shift, int bit) {
    coder.EncodeBit(ref probability, shift, bit);
    return bit;
  }

  public bool Scaled(uint probability, uint total, bool first) {
    if (first)
      coder.Encode(0, probability, total);
    else
      coder.Encode(probability, total - probability, total);
    return first;
  }
}
