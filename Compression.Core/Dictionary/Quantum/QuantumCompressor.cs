namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// Compresses data with the Quantum algorithm.
/// </summary>
/// <remarks>
/// <para>
/// LZ77 dictionary matching feeding a bit-oriented adaptive arithmetic coder. A
/// seven-state machine, driven by whether the previous tokens were literals or
/// matches, selects which literal and literal/match-flag models code the next token;
/// match lengths and distances are coded as magnitude slots (see
/// <see cref="QuantumSlotCoding"/>). Mirrors <see cref="QuantumDecompressor"/>.
/// </para>
/// <para>
/// The bitstream is an original design rather than a reconstruction of the Quantum
/// method found in Microsoft Cabinet archives, whose exact models and slot tables
/// Microsoft never published; see <see cref="QuantumBuildingBlock"/>.
/// </para>
/// </remarks>
public static class QuantumCompressor {
  /// <summary>
  /// Compresses a single block of data using the Quantum algorithm.
  /// </summary>
  /// <param name="data">The uncompressed input data.</param>
  /// <param name="windowLevel">Window level (1–7). The window size is 1024 &lt;&lt; (level − 1).</param>
  /// <returns>The compressed data, without any length header.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data, int windowLevel) {
    ArgumentOutOfRangeException.ThrowIfLessThan(windowLevel, QuantumConstants.MinWindowLevel, nameof(windowLevel));
    ArgumentOutOfRangeException.ThrowIfGreaterThan(windowLevel, QuantumConstants.MaxWindowLevel, nameof(windowLevel));

    if (data.Length == 0)
      return [];

    using var output = new MemoryStream();
    var encoder = new QuantumRangeEncoder(output);

    var literalModels = new QuantumModel[QuantumConstants.StateCount];
    var matchFlagModels = new QuantumModel[QuantumConstants.StateCount];
    for (var state = 0; state < QuantumConstants.StateCount; ++state) {
      literalModels[state] = new QuantumModel(QuantumConstants.LiteralSymbols);
      matchFlagModels[state] = new QuantumModel(2);
    }

    var lengthSlotModel = new QuantumModel(QuantumConstants.SlotSymbols);
    var distanceSlotModel = new QuantumModel(QuantumConstants.SlotSymbols);

    var windowSize = QuantumConstants.WindowSize(windowLevel);
    var currentState = 0;

    foreach (var token in Parse(data, windowSize)) {
      if (token.Length == 0) {
        encoder.EncodeSymbol(matchFlagModels[currentState], 0);
        encoder.EncodeSymbol(literalModels[currentState], token.Distance);
        currentState = QuantumConstants.LiteralNextState[currentState];
        continue;
      }

      encoder.EncodeSymbol(matchFlagModels[currentState], 1);
      QuantumSlotCoding.Encode(encoder, lengthSlotModel, token.Length - QuantumConstants.MinMatch + 1);
      QuantumSlotCoding.Encode(encoder, distanceSlotModel, token.Distance);
      currentState = QuantumConstants.MatchNextState[currentState];
    }

    encoder.Finish();
    return output.ToArray();
  }

  /// <summary>
  /// A parsed token: a literal (<see cref="Length"/> 0, <see cref="Distance"/> holding
  /// the byte value) or a match of <see cref="Length"/> bytes <see cref="Distance"/> back.
  /// </summary>
  private readonly record struct Token(int Length, int Distance);

  /// <summary>
  /// Greedy LZ77 parse over a hash chain keyed on the next three bytes.
  /// </summary>
  /// <remarks>
  /// The chain for a key holds the positions where that key was seen, in increasing
  /// order, and is walked newest first for at most
  /// <see cref="QuantumConstants.MaxMatchChain"/> candidates, stopping early once a
  /// candidate falls outside the window. The longest match wins; among equally long
  /// matches the most recent position wins, because the walk starts there and a later
  /// candidate must be strictly longer to replace it. The current position joins the
  /// chain after the search, so it is never its own candidate. Positions covered by an
  /// emitted match are not indexed.
  /// </remarks>
  private static List<Token> Parse(ReadOnlySpan<byte> data, int windowSize) {
    var tokens = new List<Token>();
    var chains = new Dictionary<int, List<int>>();
    var length = data.Length;

    for (var position = 0; position < length;) {
      var bestLength = 0;
      var bestDistance = 0;

      if (position + QuantumConstants.MinMatch <= length) {
        var key = (data[position] << 16) ^ (data[position + 1] << 8) ^ data[position + 2];

        if (chains.TryGetValue(key, out var chain))
          for (int index = chain.Count - 1, tries = 0; index >= 0 && tries < QuantumConstants.MaxMatchChain; --index, ++tries) {
            var candidate = chain[index];
            if (position - candidate > windowSize)
              break;

            var matchLength = MatchLength(data, candidate, position);
            if (matchLength <= bestLength)
              continue;

            bestLength = matchLength;
            bestDistance = position - candidate;
          }
        else
          chains[key] = chain = [];

        chain.Add(position);
      }

      if (bestLength >= QuantumConstants.MinMatch) {
        tokens.Add(new(bestLength, bestDistance));
        position += bestLength;
        continue;
      }

      tokens.Add(new(0, data[position]));
      ++position;
    }

    return tokens;
  }

  private static int MatchLength(ReadOnlySpan<byte> data, int candidate, int position) {
    var limit = data.Length - position;
    var length = 0;
    while (length < limit && data[candidate + length] == data[position + length])
      ++length;

    return length;
  }
}
