using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Lzms;

/// <summary>
/// Produces one LZMS chunk.
/// </summary>
/// <remarks>
/// <para>An encoder owes the format nothing about how it factorises its input, so
/// this one keeps to the item set that is fully derived: literals, explicit
/// matches and explicit delta matches. The repeat forms of both are legal and
/// would compress a little better, but their queue update rules are not settled,
/// and declining to use them costs nothing but a few bytes.</para>
///
/// <para>The x86 filter is not optional in the same way. A decoder applies it to
/// every chunk, so the payload is filtered first.</para>
/// </remarks>
public sealed class LzmsCompressor {
  private const int MinimumMatch = 3;
  private const int MaximumCandidates = 24;

  // A delta only pays for itself over a long run, and searching every span and
  // offset is what costs; both bounds are ours to choose, not the format's.
  private const int MinimumDelta = 16;
  private const int MaximumDeltaOffset = 16;

  /// <summary>Compresses a chunk.</summary>
  /// <param name="data">The payload.</param>
  /// <returns>The chunk, which the decoder needs the uncompressed size to read.</returns>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var filtered = LzmsX86Filter.Apply(data, forward: true);
    var items = Parse(filtered);

    var range = new LzmsRangeEncoder();
    var bits = new LzmsBackwardBitWriter();
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
    var deltaExplicit = new LzmsProbability[1 << LzmsConstants.DeltaExplicitStateBits];
    for (var i = 0; i < deltaExplicit.Length; ++i) deltaExplicit[i] = new();
    var deltaState = 0;
    var deltaMask = (1 << LzmsConstants.DeltaExplicitStateBits) - 1;
    var deltaRepeatIndex = new LzmsProbability[LzmsConstants.NumRecentDeltas - 1][];
    for (var i = 0; i < deltaRepeatIndex.Length; ++i) {
      deltaRepeatIndex[i] = new LzmsProbability[1 << LzmsConstants.RepeatIndexStateBits];
      for (var j = 0; j < deltaRepeatIndex[i].Length; ++j) deltaRepeatIndex[i][j] = new();
    }
    var deltaRepeatIndexState = new int[deltaRepeatIndex.Length];
    var indexMask = (1 << LzmsConstants.RepeatIndexStateBits) - 1;
    var lastDelta = (Power: 0, Offset: 1);
    var previousDelta = (Power: 0, Offset: 1);
    var deltaPowers = new LzmsHuffmanCode(LzmsConstants.NumDeltaPowers, LzmsConstants.LzOffsetRebuildInterval);
    var deltaOffsets = new LzmsHuffmanCode(LzmsConstants.OffsetSlotCount(filtered.Length), LzmsConstants.LzOffsetRebuildInterval);

    var literals = new LzmsHuffmanCode(LzmsConstants.NumLiteralSymbols, LzmsConstants.LiteralRebuildInterval);
    var offsets = new LzmsHuffmanCode(LzmsConstants.OffsetSlotCount(filtered.Length), LzmsConstants.LzOffsetRebuildInterval);
    var lengths = new LzmsHuffmanCode(LzmsConstants.NumLengthSlots, LzmsConstants.LengthRebuildInterval);

    var state = 0;
    var mask = (1 << LzmsConstants.MainStateBits) - 1;
    foreach (var item in items) {
      if (item.Length == 0) {
        range.WriteBit(main[state], 0);
        state = (state << 1) & mask;
        literals.Write(bits, item.Literal);
        continue;
      }

      range.WriteBit(main[state], 1);
      state = ((state << 1) | 1) & mask;

      if (item.Power >= 0) {
        range.WriteBit(matchKind[kindState], 1);
        kindState = ((kindState << 1) | 1) & kindMask;
        // Which reference a repeat names is not settled: index zero reads either as
        // the last delta or as the one before it, and no written chunk tells them
        // apart. A repeat is therefore only written where the two agree - the last
        // two deltas being the same reference - which wimlib accepts.
        if (lastDelta == (item.Power, item.Distance) && previousDelta == lastDelta) {
          range.WriteBit(deltaExplicit[deltaState], 1);
          deltaState = ((deltaState << 1) | 1) & deltaMask;
          range.WriteBit(deltaRepeatIndex[0][deltaRepeatIndexState[0]], 0);
          deltaRepeatIndexState[0] = (deltaRepeatIndexState[0] << 1) & indexMask;
          WriteLength(bits, lengths, item.Length);
          continue;
        }

        range.WriteBit(deltaExplicit[deltaState], 0);
        deltaState = (deltaState << 1) & deltaMask;
        deltaPowers.Write(bits, item.Power);

        var deltaSlot = LzmsConstants.SlotOf(LzmsConstants.OffsetSlots, item.Distance);
        var (deltaBase, deltaWidth) = LzmsConstants.OffsetSlots[deltaSlot];
        deltaOffsets.Write(bits, deltaSlot);
        if (deltaWidth > 1) bits.Write(item.Distance - deltaBase, LzmsConstants.ExtraBits(deltaWidth));

        previousDelta = lastDelta;
        lastDelta = (item.Power, item.Distance);

        WriteLength(bits, lengths, item.Length);
        continue;
      }

      range.WriteBit(matchKind[kindState], 0);
      kindState = (kindState << 1) & kindMask;
      range.WriteBit(explicitOffset[explicitState], 0);
      explicitState = (explicitState << 1) & explicitMask;

      var slot = LzmsConstants.SlotOf(LzmsConstants.OffsetSlots, item.Distance);
      var (offsetBase, offsetWidth) = LzmsConstants.OffsetSlots[slot];
      offsets.Write(bits, slot);
      if (offsetWidth > 1) bits.Write(item.Distance - offsetBase, LzmsConstants.ExtraBits(offsetWidth));

      WriteLength(bits, lengths, item.Length);
    }

    var forward = range.Finish();
    var backward = bits.Units();
    var chunk = new byte[2 * (forward.Count + backward.Count)];
    var at = 0;
    foreach (var unit in forward) {
      BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(at), unit);
      at += 2;
    }

    // the backward half is laid out tail first, so the first unit written lands last
    for (var i = backward.Count - 1; i >= 0; --i) {
      BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(at), backward[i]);
      at += 2;
    }

    return chunk;
  }

  /// <summary>
  /// Writes a match's length. Never the last symbol, which a reader takes as running
  /// to the end of the chunk: writing it is refused for the matches this parse makes,
  /// and declining to use it costs only the bytes it would have saved.
  /// </summary>
  private static void WriteLength(LzmsBackwardBitWriter bits, LzmsHuffmanCode lengths, int length) {
    var slot = LzmsConstants.SlotOf(LzmsConstants.LengthSlots, length);
    var (baseValue, width) = LzmsConstants.LengthSlots[slot];
    lengths.Write(bits, slot);
    if (width > 1) bits.Write(length - baseValue, LzmsConstants.ExtraBits(width));
  }

  /// <summary>A literal, an ordinary match, or - when Power is not -1 - a delta match.</summary>
  private readonly record struct Item(byte Literal, int Distance, int Length, int Power = -1);

  private static List<Item> Parse(ReadOnlySpan<byte> data) {
    var items = new List<Item>();
    var positions = new Dictionary<int, List<int>>();
    var maxLength = LzmsConstants.MaxMatchLength;
    var i = 0;
    while (i < data.Length) {
      var bestLength = 0;
      var bestDistance = 0;
      if (i + MinimumMatch <= data.Length) {
        var key = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];
        if (positions.TryGetValue(key, out var candidates)) {
          var from = Math.Max(0, candidates.Count - MaximumCandidates);
          for (var c = candidates.Count - 1; c >= from; --c) {
            var previous = candidates[c];
            var distance = i - previous;
            if (distance <= 0 || distance > i) continue;

            var length = 0;
            while (i + length < data.Length && length < maxLength && data[previous + length] == data[i + length])
              ++length;
            if (length <= bestLength) continue;

            bestLength = length;
            bestDistance = distance;
          }
        }
      }

      var (deltaPower, deltaOffset, deltaLength) = FindDelta(data, i);
      if (deltaLength >= MinimumDelta && deltaLength > bestLength) {
        items.Add(new(0, deltaOffset, deltaLength, deltaPower));
        var deltaStop = Math.Min(i + deltaLength, data.Length - 2);
        for (var k = i; k < deltaStop; ++k) Remember(positions, data, k);
        i += deltaLength;
        continue;
      }

      if (bestLength >= MinimumMatch) {
        items.Add(new(0, bestDistance, bestLength));
        var stop = Math.Min(i + bestLength, data.Length - 2);
        for (var k = i; k < stop; ++k) Remember(positions, data, k);
        i += bestLength;
        continue;
      }

      items.Add(new(data[i], 0, 0));
      if (i + MinimumMatch <= data.Length) Remember(positions, data, i);
      ++i;
    }

    return items;
  }

  /// <summary>
  /// The longest run from here that a delta match rebuilds: each byte is its
  /// predecessor a span back plus the same step taken a reference further back.
  /// </summary>
  private static (int Power, int Offset, int Length) FindDelta(ReadOnlySpan<byte> data, int at) {
    var bestPower = 0;
    var bestOffset = 0;
    var bestLength = 0;
    for (var power = 0; power < LzmsConstants.NumDeltaPowers; ++power) {
      var span = 1 << power;
      for (var offset = 1; offset <= MaximumDeltaOffset; ++offset) {
        var reference = offset * span;
        if (reference + span > at) break;

        var length = 0;
        while (at + length < data.Length && length < LzmsConstants.MaxMatchLength) {
          var p = at + length;
          var want = (byte)(data[p - span] + data[p - reference] - data[p - reference - span]);
          if (data[p] != want) break;

          ++length;
        }

        if (length <= bestLength) continue;

        bestLength = length;
        bestPower = power;
        bestOffset = offset;
      }
    }

    return (bestPower, bestOffset, bestLength);
  }

  private static void Remember(Dictionary<int, List<int>> positions, ReadOnlySpan<byte> data, int at) {
    var key = (data[at] << 16) | (data[at + 1] << 8) | data[at + 2];
    if (!positions.TryGetValue(key, out var list)) positions[key] = list = [];
    list.Add(at);
  }
}
