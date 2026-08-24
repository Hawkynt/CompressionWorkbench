namespace Compression.Core.Dictionary.Lzms;

/// <summary>
/// Decodes one LZMS chunk.
/// </summary>
/// <remarks>
/// The two streams run towards each other through the same buffer: the range
/// coder forwards from the start, the Huffman codes backwards from the end.
/// Item by item a main bit chooses a literal or a match; a match then spends two
/// more range-coded decisions before its offset and length are taken from the
/// backward stream.
/// </remarks>
public sealed class LzmsDecompressor {

  /// <summary>Decodes a chunk that was compressed with <see cref="LzmsCompressor"/>.</summary>
  /// <param name="compressed">The chunk.</param>
  /// <param name="uncompressedSize">Its uncompressed size, which also fixes the offset alphabet.</param>
  /// <returns>The decoded bytes.</returns>
  public byte[] Decompress(ReadOnlySpan<byte> compressed, int uncompressedSize) {
    if (uncompressedSize <= 0) return [];

    var chunk = compressed.ToArray();
    var range = new LzmsRangeDecoder(chunk);
    var bits = new LzmsBackwardBitReader(chunk);

    var main = new LzmsProbability[1 << LzmsConstants.MainStateBits];
    for (var i = 0; i < main.Length; ++i) main[i] = new();
    var matchKind = new LzmsProbability[1 << LzmsConstants.MatchKindStateBits];
    for (var i = 0; i < matchKind.Length; ++i) matchKind[i] = new();
    var kindState = 0;
    var kindMask = (1 << LzmsConstants.MatchKindStateBits) - 1;
    var explicitOffset = new LzmsProbability[1 << LzmsConstants.LzExplicitStateBits];
    for (var i = 0; i < explicitOffset.Length; ++i) explicitOffset[i] = new();
    var explicitState = 0;
    var explicitMask = (1 << LzmsConstants.LzExplicitStateBits) - 1;
    var repeatIndex = Contexted(LzmsConstants.NumRecentLzOffsets - 1);
    var repeatIndexState = new int[repeatIndex.Length];

    var deltaExplicit = new LzmsProbability[1 << LzmsConstants.DeltaExplicitStateBits];
    for (var i = 0; i < deltaExplicit.Length; ++i) deltaExplicit[i] = new();
    var deltaState = 0;
    var deltaMask = (1 << LzmsConstants.DeltaExplicitStateBits) - 1;
    var deltaRepeatIndex = Contexted(LzmsConstants.NumRecentDeltas - 1);
    var deltaRepeatIndexState = new int[deltaRepeatIndex.Length];
    var indexMask = (1 << LzmsConstants.RepeatIndexStateBits) - 1;

    var recentDeltas = new (int Power, int Offset)[LzmsConstants.NumRecentDeltas];
    for (var i = 0; i < recentDeltas.Length; ++i) recentDeltas[i] = (0, i + 1);
    var nextDeltaSeed = LzmsConstants.NumRecentDeltas + 1;
    (int Power, int Offset)? deltaPending = null, deltaCarried = null;
    int? lzPending = null, lzCarried = null;
    var deltaPowers = new LzmsHuffmanCode(LzmsConstants.NumDeltaPowers, LzmsConstants.LzOffsetRebuildInterval);
    var deltaOffsets = new LzmsHuffmanCode(LzmsConstants.OffsetSlotCount(uncompressedSize), LzmsConstants.LzOffsetRebuildInterval);

    var literals = new LzmsHuffmanCode(LzmsConstants.NumLiteralSymbols, LzmsConstants.LiteralRebuildInterval);
    var offsets = new LzmsHuffmanCode(LzmsConstants.OffsetSlotCount(uncompressedSize), LzmsConstants.LzOffsetRebuildInterval);
    var lengths = new LzmsHuffmanCode(LzmsConstants.NumLengthSlots, LzmsConstants.LengthRebuildInterval);

    var recent = new int[LzmsConstants.NumRecentLzOffsets];
    for (var i = 0; i < recent.Length; ++i) recent[i] = i + 1;
    var nextSeed = LzmsConstants.NumRecentLzOffsets + 1;
    var output = new byte[uncompressedSize];
    var produced = 0;
    var state = 0;
    var mask = (1 << LzmsConstants.MainStateBits) - 1;

    // Both queues take an item's reference only after the item that follows it,
    // so during an item they hold what was in use two items ago. Measured: a
    // table whose reference alternates between two entries decodes correctly
    // only under this delay, and parts at the first carry without it.
    void EndOfItem() {
      if (deltaCarried is not null) {
        Array.Copy(recentDeltas, 0, recentDeltas, 1, recentDeltas.Length - 1);
        recentDeltas[0] = deltaCarried.Value;
      }
      deltaCarried = deltaPending;
      deltaPending = null;

      if (lzCarried is not null) {
        Array.Copy(recent, 0, recent, 1, recent.Length - 1);
        recent[0] = lzCarried.Value;
      }
      lzCarried = lzPending;
      lzPending = null;
    }

    while (produced < uncompressedSize) {
      var bit = range.ReadBit(main[state]);
      state = ((state << 1) | bit) & mask;
      if (bit == 0) {
        output[produced++] = (byte)literals.Read(bits);
        EndOfItem();
        continue;
      }

      var kind = range.ReadBit(matchKind[kindState]);
      kindState = ((kindState << 1) | kind) & kindMask;
      if (kind != 0) {
        // A delta match rebuilds data whose entries step by a constant: the span
        // is a power of two and the reference is that many spans back, so each
        // byte is its predecessor a span back plus the same step taken there.
        int power;
        int deltaOffset;
        if (range.ReadBit(deltaExplicit[deltaState]) != 0) {
          deltaState = ((deltaState << 1) | 1) & deltaMask;
          var index = 0;
          while (index < deltaRepeatIndex.Length) {
            var more = range.ReadBit(deltaRepeatIndex[index][deltaRepeatIndexState[index]]);
            deltaRepeatIndexState[index] = ((deltaRepeatIndexState[index] << 1) | more) & indexMask;
            if (more == 0) break;

            ++index;
          }
          // spent on naming, exactly as an LZ offset is
          (power, deltaOffset) = recentDeltas[index];
          for (var i = index; i < recentDeltas.Length - 1; ++i) recentDeltas[i] = recentDeltas[i + 1];
          recentDeltas[^1] = (0, nextDeltaSeed++);
        } else {
          deltaState = (deltaState << 1) & deltaMask;
          power = deltaPowers.Read(bits);
          var deltaSlot = deltaOffsets.Read(bits);
          var (deltaBase, deltaWidth) = LzmsConstants.OffsetSlots[deltaSlot];
          deltaOffset = deltaBase + ReadExtra(bits, deltaWidth);
        }

        deltaPending = (power, deltaOffset);
        var deltaLengthSlot = lengths.Read(bits);
        var (deltaLengthBase, deltaLengthWidth) = LzmsConstants.LengthSlots[deltaLengthSlot];
        var deltaExtra = ReadExtra(bits, deltaLengthWidth);
        var deltaLength = deltaLengthSlot == LzmsConstants.RunToEndLengthSlot
          ? uncompressedSize - produced
          : deltaLengthBase + deltaExtra;

        var span = 1 << power;
        var reference = deltaOffset * span;
        if (reference + span > produced)
          throw new InvalidDataException($"LZMS delta match reaches {reference + span} bytes back with {produced} produced.");

        for (var i = 0; i < deltaLength && produced < uncompressedSize; ++i, ++produced)
          output[produced] = (byte)(output[produced - span]
            + output[produced - reference] - output[produced - reference - span]);
        EndOfItem();
        continue;
      }

      int distance;
      var explicitBit = range.ReadBit(explicitOffset[explicitState]);
      explicitState = ((explicitState << 1) | explicitBit) & explicitMask;
      if (explicitBit == 0) {
        var slot = offsets.Read(bits);
        var (baseValue, width) = LzmsConstants.OffsetSlots[slot];
        distance = baseValue + ReadExtra(bits, width);
      } else {
        var index = 0;
        while (index < repeatIndex.Length) {
          var more = range.ReadBit(repeatIndex[index][repeatIndexState[index]]);
          repeatIndexState[index] = ((repeatIndexState[index] << 1) | more) & indexMask;
          if (more == 0) break;

          ++index;
        }

        // Naming an entry spends it: the ones above it move down and the seed that
        // has not been used yet takes the last place. Measured both ways - it is the
        // only arrangement under which wimlib's own chunks all decode, and a chunk
        // written to need it verifies.
        distance = recent[index];
        for (var i = index; i < recent.Length - 1; ++i) recent[i] = recent[i + 1];
        recent[^1] = nextSeed++;
      }

      lzPending = distance;

      var lengthSlot = lengths.Read(bits);
      var (lengthBase, lengthWidth) = LzmsConstants.LengthSlots[lengthSlot];
      var lengthExtra = ReadExtra(bits, lengthWidth);
      var length = lengthSlot == LzmsConstants.RunToEndLengthSlot
        ? uncompressedSize - produced
        : lengthBase + lengthExtra;

      if (distance > produced || length <= 0)
        throw new InvalidDataException($"LZMS match of {length} at distance {distance} is out of range.");

      for (var i = 0; i < length && produced < uncompressedSize; ++i, ++produced)
        output[produced] = output[produced - distance];
      EndOfItem();
    }

    return LzmsX86Filter.Apply(output, forward: false);
  }

  /// <summary>One probability per context for each position of a unary index.</summary>
  private static LzmsProbability[][] Contexted(int positions) {
    var all = new LzmsProbability[positions][];
    for (var i = 0; i < positions; ++i) {
      all[i] = new LzmsProbability[1 << LzmsConstants.RepeatIndexStateBits];
      for (var j = 0; j < all[i].Length; ++j) all[i][j] = new();
    }

    return all;
  }

  private static int ReadExtra(LzmsBackwardBitReader bits, int width)
    => width <= 1 ? 0 : bits.Read(LzmsConstants.ExtraBits(width));
}
