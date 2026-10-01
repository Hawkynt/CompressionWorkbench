namespace Compression.Core.Dictionary.Csc;

/// <summary>
/// libcsc's per-8-KiB data classifier: order-0 entropy, a text heuristic, an x86 heuristic and a
/// channel-similarity test for data tables decide which coder a block goes through.
/// </summary>
/// <remarks>
/// Entropies are in 1/100 bit, using a table of <c>100 * log2</c> sampled every 16 counts, so the
/// thresholds below (300, 400, 780, 795 per byte) are bits-per-byte times 100.
/// </remarks>
internal static class CscAnalyzer {
  private static readonly uint[] _log2Table = BuildLogTable();

  /// <summary>Classifies (up to) the first 8 KiB of <paramref name="block"/>; <paramref name="bitsPerByte"/> receives its order-0 entropy.</summary>
  /// <returns>One of the <c>CscConstants.Type*</c> values; <see cref="CscConstants.TypeSkip"/> for blocks too small to judge.</returns>
  public static uint Analyze(ReadOnlySpan<byte> block, ref uint bitsPerByte) {
    if (block.Length > CscConstants.MinBlockSize)
      block = block[..CscConstants.MinBlockSize];
    var size = (uint)block.Length;
    if (size < 512)
      return CscConstants.TypeSkip;

    Span<uint> freq = stackalloc uint[256];
    freq.Clear();
    foreach (var b in block)
      ++freq[b];

    var log = _log2Table;
    uint diffNum = 0, high = 0;
    var entropy = size * log[size >> 4];
    for (var i = 0; i < 256; ++i) {
      entropy -= freq[i] * log[freq[i] >> 4];
      if (freq[i] > 0)
        ++diffNum;
      if (i >= 0x80)
        high += freq[i];
    }

    bitsPerByte = entropy / size;
    var averageFreq = size >> 8;

    uint alphaNum = 0;
    for (var i = 'a'; i <= 'z'; ++i)
      alphaNum += freq[i];

    if (high < size >> 3
        && freq[' '] + freq['\n'] + freq[':'] + freq['.'] + freq['/'] > size >> 4
        && freq['a'] + freq['e'] + freq['t'] > size >> 4
        && entropy > 300 * size
        && alphaNum > size / 3)
      return CscConstants.TypeEnglishText;

    if (freq[0x8B] > averageFreq && freq[0x00] > averageFreq * 2 && freq[0xE8] > 6)
      return CscConstants.TypeExecutable;

    if (entropy > (Math.Log((double)diffNum - 2) / Math.Log(2) - 0.6) * 100.0 * size && diffNum < 16 && diffNum >= 6)
      return CscConstants.TypeEntropy;

    if (entropy < 400 * size && diffNum < 200)
      return CscConstants.TypeNormal;

    var channel = DeltaChannelIndex(block);
    if (channel >= 0)
      return CscConstants.TypeDelta + (uint)channel;

    if (entropy > 795 * size)
      return CscConstants.TypeBad;
    if (entropy > 780 * size)
      return CscConstants.TypeFast;

    return CscConstants.TypeNormal;
  }

  /// <summary>Order-0 entropy (1/100 bit per byte) of <paramref name="block"/> after a <paramref name="channels"/>-way delta.</summary>
  public static uint DeltaBitsPerByte(ReadOnlySpan<byte> block, int channels) {
    Span<uint> freq = stackalloc uint[256];
    freq.Clear();
    byte previous = 0;
    for (var i = 0; i < channels; ++i)
      for (var j = i; j < block.Length; j += channels) {
        ++freq[(byte)(block[j] - previous)];
        previous = block[j];
      }

    var log = _log2Table;
    var size = (uint)block.Length;
    var bpb = size * log[size >> 4];
    for (var i = 0; i < 256; ++i)
      bpb -= freq[i] * log[freq[i] >> 4];
    return bpb / size;
  }

  /// <summary>Finds the stride (1, 2, 3, 4 or 8) at which the block is markedly self-similar, or -1.</summary>
  private static int DeltaChannelIndex(ReadOnlySpan<byte> block) {
    Span<uint> same = stackalloc uint[(int)CscConstants.DeltaChannelLayouts];
    Span<uint> successive = stackalloc uint[(int)CscConstants.DeltaChannelLayouts];
    same.Clear();
    successive.Clear();
    var size = (uint)block.Length;
    for (var i = 0; i + 16 < block.Length; ++i) {
      int v = block[i];
      Accumulate(same, successive, 0, v, block[i + 1]);
      Accumulate(same, successive, 1, v, block[i + 2]);
      Accumulate(same, successive, 2, v, block[i + 3]);
      Accumulate(same, successive, 3, v, block[i + 4]);
      Accumulate(same, successive, 4, v, block[i + 8]);
    }

    uint minSame = same[0], maxSucc = successive[0], minSucc = successive[0];
    var best = 0;
    for (var i = 0; i < same.Length; ++i) {
      if (same[i] < minSame)
        minSame = same[i];
      if (successive[i] > maxSucc)
        maxSucc = successive[i];
      if (successive[i] < minSucc) {
        minSucc = successive[i];
        best = i;
      }
    }

    return (maxSucc > successive[best] * 4 || maxSucc > successive[best] + 40 * size)
           && same[best] > minSame * 3
           && same[0] < 0.3 * size
      ? best
      : -1;

    static void Accumulate(Span<uint> same, Span<uint> successive, int index, int a, int b) {
      if (a == b)
        ++same[index];
      successive[index] += (uint)Math.Abs(a - b);
    }
  }

  private static uint[] BuildLogTable() {
    var result = new uint[(CscConstants.MinBlockSize >> 4) + 1];
    for (var i = 0; i < CscConstants.MinBlockSize >> 4; ++i)
      result[i] = (uint)(100.0 * Math.Log((double)i * 16 + 8) / Math.Log(2.0));
    result[CscConstants.MinBlockSize >> 4] = (uint)(100.0 * Math.Log(CscConstants.MinBlockSize) / Math.Log(2.0));
    return result;
  }
}
