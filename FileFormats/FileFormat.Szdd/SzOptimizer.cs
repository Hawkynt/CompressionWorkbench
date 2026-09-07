using System.Buffers.Binary;

namespace FileFormat.Szdd;

/// <summary>
/// Size optimizer for the legacy <c>"SZ "</c> Microsoft COMPRESS stream.
/// </summary>
/// <remarks>
/// <para>
/// The wire format has no tunable compression parameters: every token is either
/// a one-byte literal or a two-byte LZSS match, and one control byte describes
/// each group of eight tokens. The useful optimization therefore is the parse.
/// </para>
/// <para>
/// This implementation finds every format-valid match of length 3..18 in the
/// 4096-byte ring, including overlapping copies, then uses dynamic programming
/// over the eight control-bit positions. The result is globally minimal for the
/// emitted SZ body size, not merely greedy-longest or bounded-chain optimal.
/// </para>
/// </remarks>
public static class SzOptimizer {
  private const int HashBits = 16;
  private const int HashSize = 1 << HashBits;
  private const int StateCount = 8;
  private const int CostRingSize = SzddConstants.MaxMatchLength + 1;
  private const int MaxGroupSpan = StateCount * SzddConstants.MaxMatchLength;
  private const int GroupStride = MaxGroupSpan + 1;

  /// <summary>
  /// Compresses <paramref name="input"/> to a size-optimal legacy <c>"SZ "</c>
  /// stream and writes it to <paramref name="output"/>.
  /// </summary>
  public static void Compress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    using var source = new MemoryStream();
    input.CopyTo(source);
    output.Write(Compress(source.ToArray()));
  }

  /// <summary>
  /// Compresses <paramref name="input"/> to a size-optimal legacy <c>"SZ "</c>
  /// stream.
  /// </summary>
  public static byte[] Compress(ReadOnlySpan<byte> input) {
    var data = input.ToArray();
    var (matchLengths, matchOffsets) = BuildMatchTable(data);
    var boundaryCosts = BuildBoundaryCosts(matchLengths);
    var body = EmitOptimalBody(data, matchLengths, matchOffsets, boundaryCosts);

    var result = new byte[SzddConstants.QBasicHeaderSize + body.Length];
    var header = result.AsSpan(0, SzddConstants.QBasicHeaderSize);
    SzddConstants.QBasicMagic.CopyTo(header);
    BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)data.Length);
    body.CopyTo(result.AsSpan(SzddConstants.QBasicHeaderSize));
    return result;
  }

  private static (byte[] Lengths, ushort[] Offsets) BuildMatchTable(byte[] input) {
    var lengths = new byte[input.Length];
    var offsets = new ushort[input.Length];
    if (input.Length < SzddConstants.MinMatchLength)
      return (lengths, offsets);

    var head = new int[HashSize];
    Array.Fill(head, -1);
    var previous = new int[input.Length];
    Array.Fill(previous, -1);

    for (var position = 0; position <= input.Length - SzddConstants.MinMatchLength; ++position) {
      var maxLength = Math.Min(SzddConstants.MaxMatchLength, input.Length - position);
      var bestLength = 0;
      var bestPosition = 0;
      var oldestPosition = position - SzddConstants.WindowSize;

      // Positions before byte zero represent the initial ring contents, which are
      // spaces. Only the final maxLength-1 such starts can cross into real input;
      // all earlier starts are equivalent for a match no longer than maxLength.
      if (oldestPosition < 0) {
        var firstCrossingCandidate = Math.Max(oldestPosition, 1 - maxLength);
        if (oldestPosition < firstCrossingCandidate)
          ConsiderCandidate(input, position, oldestPosition, maxLength, ref bestLength, ref bestPosition);

        for (var candidate = firstCrossingCandidate; candidate < 0 && bestLength < maxLength; ++candidate)
          ConsiderCandidate(input, position, candidate, maxLength, ref bestLength, ref bestPosition);
      }

      var hash = Hash(input, position);
      for (var candidate = head[hash]; candidate >= 0 && candidate >= oldestPosition && bestLength < maxLength; candidate = previous[candidate])
        ConsiderCandidate(input, position, candidate, maxLength, ref bestLength, ref bestPosition);

      if (bestLength >= SzddConstants.MinMatchLength) {
        lengths[position] = (byte)bestLength;
        offsets[position] = (ushort)((SzddConstants.WindowInitPos + bestPosition) & (SzddConstants.WindowSize - 1));
      }

      previous[position] = head[hash];
      head[hash] = position;
    }

    return (lengths, offsets);
  }

  private static void ConsiderCandidate(
      ReadOnlySpan<byte> input,
      int position,
      int candidate,
      int maxLength,
      ref int bestLength,
      ref int bestPosition) {
    var length = MatchLength(input, position, candidate, maxLength);
    if (length <= bestLength)
      return;

    bestLength = length;
    bestPosition = candidate;
  }

  private static int MatchLength(ReadOnlySpan<byte> input, int position, int candidate, int maxLength) {
    var length = 0;
    while (length < maxLength) {
      var sourcePosition = candidate + length;
      var source = sourcePosition < 0 ? SzddConstants.WindowFill : input[sourcePosition];
      if (source != input[position + length])
        break;
      ++length;
    }
    return length;
  }

  private static int Hash(ReadOnlySpan<byte> input, int position) {
    var value = input[position];
    value = (value * 251) ^ input[position + 1];
    value = (value * 251) ^ input[position + 2];
    return value & (HashSize - 1);
  }

  /// <summary>
  /// Computes the exact suffix cost for positions that begin a fresh control
  /// group. All eight token-position states are needed while walking backwards,
  /// but only the group-boundary state is retained for reconstruction.
  /// </summary>
  private static int[] BuildBoundaryCosts(ReadOnlySpan<byte> matchLengths) {
    var boundaryCosts = new int[matchLengths.Length + 1];
    var rollingCosts = new int[CostRingSize * StateCount];

    for (var position = matchLengths.Length - 1; position >= 0; --position) {
      var slot = (position % CostRingSize) * StateCount;
      for (var state = 0; state < StateCount; ++state) {
        var nextState = (state + 1) & (StateCount - 1);
        var bestPayloadCost = 1 + GetRollingCost(rollingCosts, position + 1, nextState);

        for (var length = SzddConstants.MinMatchLength; length <= matchLengths[position]; ++length) {
          var candidateCost = 2 + GetRollingCost(rollingCosts, position + length, nextState);
          if (candidateCost < bestPayloadCost)
            bestPayloadCost = candidateCost;
        }

        rollingCosts[slot + state] = (state == 0 ? 1 : 0) + bestPayloadCost;
      }

      boundaryCosts[position] = rollingCosts[slot];
    }

    return boundaryCosts;
  }

  private static int GetRollingCost(int[] rollingCosts, int position, int state) {
    var slot = (position % CostRingSize) * StateCount;
    return rollingCosts[slot + state];
  }

  private static byte[] EmitOptimalBody(
      ReadOnlySpan<byte> input,
      ReadOnlySpan<byte> matchLengths,
      ReadOnlySpan<ushort> matchOffsets,
      ReadOnlySpan<int> boundaryCosts) {
    using var body = new MemoryStream();
    var memo = new int[(StateCount + 1) * GroupStride];
    var choices = new byte[StateCount * GroupStride];
    var position = 0;

    while (position < input.Length) {
      var groupStart = position;
      Array.Fill(memo, -1);
      Array.Clear(choices);

      int SolveGroup(int currentPosition, int tokenCount) {
        if (currentPosition >= input.Length)
          return 0;
        if (tokenCount == StateCount)
          return boundaryCosts[currentPosition];

        var relativePosition = currentPosition - groupStart;
        var index = tokenCount * GroupStride + relativePosition;
        if (memo[index] >= 0)
          return memo[index];

        var bestCost = 1 + SolveGroup(currentPosition + 1, tokenCount + 1);
        byte bestLength = 1;

        for (var length = SzddConstants.MinMatchLength; length <= matchLengths[currentPosition]; ++length) {
          var candidateCost = 2 + SolveGroup(currentPosition + length, tokenCount + 1);
          if (candidateCost > bestCost || candidateCost == bestCost && length <= bestLength)
            continue;

          bestCost = candidateCost;
          bestLength = (byte)length;
        }

        memo[index] = bestCost;
        choices[index] = bestLength;
        return bestCost;
      }

      var expectedPayloadCost = SolveGroup(groupStart, 0);
      System.Diagnostics.Debug.Assert(1 + expectedPayloadCost == boundaryCosts[groupStart]);

      var flagOffset = body.Position;
      body.WriteByte(0);
      byte flags = 0;

      for (var token = 0; token < StateCount && position < input.Length; ++token) {
        var index = token * GroupStride + position - groupStart;
        var length = choices[index];
        if (length <= 1) {
          flags |= (byte)(1 << token);
          body.WriteByte(input[position]);
          ++position;
          continue;
        }

        var offset = matchOffsets[position];
        body.WriteByte((byte)offset);
        body.WriteByte((byte)(((offset >> 4) & 0xF0) | (length - SzddConstants.MinMatchLength)));
        position += length;
      }

      var endOffset = body.Position;
      body.Position = flagOffset;
      body.WriteByte(flags);
      body.Position = endOffset;
    }

    return body.ToArray();
  }
}
