using Compression.Core.Entropy.Huffman;

namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Code lengths for the RAR 5 Huffman tables: optimal lengths from symbol frequencies,
/// limited to 15 bits (the longest code a RAR 5 table holds).
/// </summary>
internal sealed class Rar5HuffmanEncoder {
  private int[] _codeLengths = [];

  /// <summary>Gets the code lengths array.</summary>
  public int[] CodeLengths => this._codeLengths;

  /// <summary>Builds code lengths from frequency counts.</summary>
  /// <param name="frequencies">Per-symbol frequency counts.</param>
  /// <param name="numSymbols">Number of symbols.</param>
  /// <param name="maxBits">Maximum code length (default 15).</param>
  public void Build(int[] frequencies, int numSymbols, int maxBits = Rar5Constants.MaxCodeLength) {
    // Tie-break between equally frequent symbols is defined by DeterministicHuffman, not
    // by whatever order a container happens to hand back equal keys.
    var lengths = DeterministicHuffman.BuildCodeLengths(frequencies.AsSpan(0, numSymbols));
    ClampAndFix(lengths, numSymbols, maxBits);
    this._codeLengths = lengths;
  }

  private static void ClampAndFix(int[] lengths, int numSymbols, int maxBits) {
    for (var i = 0; i < numSymbols; ++i)
      if (lengths[i] > maxBits)
        lengths[i] = maxBits;

    var kraftMax = 1L << maxBits;
    long kraftSum = 0;
    for (var i = 0; i < numSymbols; ++i)
      if (lengths[i] > 0)
        kraftSum += kraftMax >> lengths[i];

    while (kraftSum > kraftMax) {
      for (var i = numSymbols - 1; i >= 0; --i) {
        if (lengths[i] > 0 && lengths[i] < maxBits) {
          kraftSum -= kraftMax >> lengths[i];
          ++lengths[i];
          kraftSum += kraftMax >> lengths[i];
          if (kraftSum <= kraftMax) break;
        }
      }
    }
  }
}
