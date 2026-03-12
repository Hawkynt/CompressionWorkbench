using Compression.Core.Entropy.Huffman;

namespace Compression.Core.Deflate;

/// <summary>
/// Zopfli-style iterative optimal DEFLATE compression orchestrator.
/// Iteratively refines Huffman code lengths and LZ parse, then block-splits.
/// </summary>
internal static class ZopfliDeflate {
  private const int MaxIterations = 15;

  /// <summary>
  /// Produces an optimal DEFLATE parse of <paramref name="data"/>,
  /// returning per-block symbols and Huffman code lengths.
  /// </summary>
  public static List<(LzSymbol[] Symbols, int[] LitLenLengths, int[] DistLengths)> CompressOptimal(
    ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [([], DeflateConstants.GetStaticLiteralLengths(), DeflateConstants.GetStaticDistanceLengths())];

    // Start with static Huffman code lengths
    var litLenLengths = DeflateConstants.GetStaticLiteralLengths();
    var distLengths = DeflateConstants.GetStaticDistanceLengths();

    LzSymbol[] bestSymbols = [];
    var prevHash = 0L;

    for (var iteration = 0; iteration < MaxIterations; ++iteration) {
      // Fresh hash chain each iteration
      var hashChain = new ZopfliHashChain();

      // Optimal parse with current code lengths
      var symbols = OptimalParser.Parse(data, hashChain, litLenLengths, distLengths);
      bestSymbols = symbols;

      // Count frequencies
      var litLenFreqs = new long[DeflateConstants.LiteralLengthAlphabetSize];
      var distFreqs = new long[DeflateConstants.DistanceAlphabetSize];

      foreach (var sym in symbols) {
        if (sym.IsLiteral)
          ++litLenFreqs[sym.LitLen];
        else {
          var lenCode = DeflateConstants.GetLengthCode(sym.LitLen);
          ++litLenFreqs[lenCode];
          var distCode = DeflateConstants.GetDistanceCode(sym.Distance);
          ++distFreqs[distCode];
        }
      }
      litLenFreqs[DeflateConstants.EndOfBlock] = 1;

      // Ensure at least one distance code
      var hasDist = distFreqs.Any(t => t > 0);
      if (!hasDist) 
        distFreqs[0] = 1;

      // Build trees and extract code lengths
      var litLenRoot = HuffmanTree.BuildFromFrequencies(litLenFreqs);
      litLenLengths = HuffmanTree.GetCodeLengths(litLenRoot, DeflateConstants.LiteralLengthAlphabetSize);
      HuffmanTree.LimitCodeLengths(litLenLengths, DeflateConstants.MaxBits);

      // Pad to 288 for proper static table compatibility
      if (litLenLengths.Length < 288) {
        var padded = new int[288];
        litLenLengths.AsSpan().CopyTo(padded);
        litLenLengths = padded;
      }

      var distRoot = HuffmanTree.BuildFromFrequencies(distFreqs);
      distLengths = HuffmanTree.GetCodeLengths(distRoot, DeflateConstants.DistanceAlphabetSize);
      HuffmanTree.LimitCodeLengths(distLengths, DeflateConstants.MaxBits);

      // Convergence detection via symbol hash
      var currentHash = ComputeSymbolHash(symbols);
      if (currentHash == prevHash && iteration > 0)
        break;

      prevHash = currentHash;
    }

    // Block splitting
    var blocks = BlockSplitter.Split(bestSymbols);
    var result = new List<(LzSymbol[], int[], int[])>();

    foreach (var block in blocks) {
      var blockSymbols = bestSymbols[block.Start..block.End];

      // Compute per-block Huffman stats
      var blockLitLenFreqs = new long[DeflateConstants.LiteralLengthAlphabetSize];
      var blockDistFreqs = new long[DeflateConstants.DistanceAlphabetSize];

      foreach (var sym in blockSymbols)
        if (sym.IsLiteral)
          ++blockLitLenFreqs[sym.LitLen];
        else {
          var lenCode = DeflateConstants.GetLengthCode(sym.LitLen);
          ++blockLitLenFreqs[lenCode];
          var distCode = DeflateConstants.GetDistanceCode(sym.Distance);
          ++blockDistFreqs[distCode];
        }

      blockLitLenFreqs[DeflateConstants.EndOfBlock] = 1;

      var hasDist = blockDistFreqs.Any(t => t > 0);
      if (!hasDist) 
        blockDistFreqs[0] = 1;

      var blkLitRoot = HuffmanTree.BuildFromFrequencies(blockLitLenFreqs);
      var blkLitLengths = HuffmanTree.GetCodeLengths(blkLitRoot, DeflateConstants.LiteralLengthAlphabetSize);
      HuffmanTree.LimitCodeLengths(blkLitLengths, DeflateConstants.MaxBits);

      var blkDistRoot = HuffmanTree.BuildFromFrequencies(blockDistFreqs);
      var blkDistLengths = HuffmanTree.GetCodeLengths(blkDistRoot, DeflateConstants.DistanceAlphabetSize);
      HuffmanTree.LimitCodeLengths(blkDistLengths, DeflateConstants.MaxBits);

      result.Add((blockSymbols, blkLitLengths, blkDistLengths));
    }

    return result;
  }

  private static long ComputeSymbolHash(LzSymbol[] symbols) {
    var hash = 0L;
    foreach (var sym in symbols) {
      hash = hash * 31 + sym.LitLen;
      hash = hash * 31 + sym.Distance;
    }
    return hash;
  }
}
