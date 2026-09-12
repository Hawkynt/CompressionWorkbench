using System.Buffers.Binary;

namespace FileFormat.Szdd;

/// <summary>
/// Size-optimal parser for the standard SZDD LZSS stream.
/// </summary>
/// <remarks>
/// SZDD charges one flag byte per eight tokens, one byte for a literal and two
/// bytes for a match. A longest-match greedy parse is therefore not generally
/// optimal: a shorter token now can expose a longer match later, and crossing an
/// eight-token boundary costs another flag byte. This implementation finds the
/// longest legal match at every input position, then runs dynamic programming
/// over (input position, token slot) to minimize the exact encoded byte count.
/// </remarks>
internal static class SzddOptimizer {
  private const int FlagGroupSize = 8;
  private const int MatchHashSize = 1 << 16;
  private const int NoPosition = int.MinValue;
  private const int MatchRingSize = SzddConstants.MaxMatchLength + 1;

  private readonly record struct Match(byte Length, ushort Offset);

  /// <summary>Compresses to the standard 14-byte SZDD envelope using an optimal parse.</summary>
  public static byte[] Compress(ReadOnlySpan<byte> input, char missingChar = '_') {
    var matches = FindMatches(input);
    var tokenLengths = Plan(input.Length, matches);
    var body = EncodeBody(input, matches, tokenLengths);

    var result = new byte[SzddConstants.HeaderSize + body.Length];
    var header = result.AsSpan(0, SzddConstants.HeaderSize);
    SzddConstants.Magic.CopyTo(header);
    SzddConstants.MagicSuffix.CopyTo(header[4..]);
    header[8] = SzddConstants.CompressionModeA;
    header[9] = (byte)missingChar;
    BinaryPrimitives.WriteUInt32LittleEndian(header[10..], (uint)input.Length);
    body.AsSpan().CopyTo(result.AsSpan(SzddConstants.HeaderSize));
    return result;
  }

  /// <summary>
  /// Finds the longest encodable match at each input position. The logical
  /// history before position zero is the format-defined 4096 spaces; logical
  /// positions at or after zero are bytes already decoded from <paramref name="input"/>.
  /// This representation also models overlapping matches exactly.
  /// </summary>
  private static Match[] FindMatches(ReadOnlySpan<byte> input) {
    var result = new Match[input.Length];
    if (input.Length < SzddConstants.MinMatchLength)
      return result;

    var head = new int[MatchHashSize];
    var previous = new int[input.Length + SzddConstants.WindowSize];
    Array.Fill(head, NoPosition);
    Array.Fill(previous, NoPosition);

    // Seed every logical position represented by the initially space-filled ring.
    // Positions close to zero may form legal overlapping matches into the output,
    // so their 3-byte prefixes are evaluated through VirtualByte as well.
    for (var logicalPosition = -SzddConstants.WindowSize; logicalPosition < 0; ++logicalPosition) {
      var hash = Hash3(input, logicalPosition);
      var index = logicalPosition + SzddConstants.WindowSize;
      previous[index] = head[hash];
      head[hash] = logicalPosition;
    }

    for (var position = 0; position < input.Length; ++position) {
      var maxLength = Math.Min(SzddConstants.MaxMatchLength, input.Length - position);
      if (maxLength < SzddConstants.MinMatchLength)
        continue;

      var hash = Hash3(input, position);
      var oldest = position - SzddConstants.WindowSize;
      var best = result[position];

      for (var candidate = head[hash]; candidate != NoPosition && candidate >= oldest;
           candidate = previous[candidate + SzddConstants.WindowSize]) {
        var matchLength = 0;
        while (matchLength < maxLength
               && VirtualByte(input, candidate + matchLength) == input[position + matchLength])
          ++matchLength;

        if (matchLength < SzddConstants.MinMatchLength || matchLength <= best.Length)
          continue;

        var ringOffset = (SzddConstants.StandardWindowInitPos + candidate) & (SzddConstants.WindowSize - 1);
        best = new Match((byte)matchLength, (ushort)ringOffset);
        if (matchLength == maxLength)
          break;
      }

      result[position] = best;

      // Insert only after searching so a token can never reference bytes that have
      // not yet been produced. The chain stores logical positions rather than ring
      // slots, which avoids stale links when the 4096-byte window wraps.
      var previousIndex = position + SzddConstants.WindowSize;
      previous[previousIndex] = head[hash];
      head[hash] = position;
    }

    return result;
  }

  /// <summary>
  /// Finds a minimum-byte tokenization. The second state dimension is the number
  /// of tokens already occupying the current flag byte (0..7); starting a token
  /// in slot zero incurs the one-byte flag-group cost.
  /// </summary>
  private static byte[] Plan(int inputLength, ReadOnlySpan<Match> matches) {
    if (inputLength == 0)
      return [];

    var costs = new long[MatchRingSize * FlagGroupSize];
    Array.Fill(costs, long.MaxValue);
    costs[0] = 0;

    // One byte of predecessor token length for each of the eight destination
    // slots, packed into a ulong. The predecessor slot itself is always
    // (destinationSlot - 1) mod 8, so it need not be stored.
    var choices = new ulong[inputLength + 1];

    for (var position = 0; position < inputLength; ++position) {
      var sourceBase = position % MatchRingSize * FlagGroupSize;

      for (var slot = 0; slot < FlagGroupSize; ++slot) {
        var currentCost = costs[sourceBase + slot];
        if (currentCost == long.MaxValue)
          continue;

        var nextSlot = (slot + 1) & (FlagGroupSize - 1);
        var flagCost = slot == 0 ? 1 : 0;

        Relax(costs, choices, position + 1, nextSlot, currentCost + flagCost + 1, 1);

        var longest = matches[position].Length;
        for (var length = SzddConstants.MinMatchLength; length <= longest; ++length)
          Relax(costs, choices, position + length, nextSlot, currentCost + flagCost + 2, (byte)length);
      }

      // No transition spans MatchRingSize positions, so this ring row cannot be a
      // future destination after position has been processed.
      costs.AsSpan(sourceBase, FlagGroupSize).Fill(long.MaxValue);
    }

    var finalBase = inputLength % MatchRingSize * FlagGroupSize;
    var finalSlot = 0;
    var finalCost = costs[finalBase];
    for (var slot = 1; slot < FlagGroupSize; ++slot) {
      if (costs[finalBase + slot] >= finalCost)
        continue;
      finalCost = costs[finalBase + slot];
      finalSlot = slot;
    }

    if (finalCost == long.MaxValue)
      throw new InvalidOperationException("SZDD optimizer could not reach the end of the input.");

    var tokenLengths = new byte[inputLength];
    var currentPosition = inputLength;
    var currentSlot = finalSlot;
    while (currentPosition > 0) {
      var length = GetChoice(choices[currentPosition], currentSlot);
      if (length == 0 || length > currentPosition)
        throw new InvalidOperationException("SZDD optimizer produced an invalid predecessor chain.");

      currentPosition -= length;
      tokenLengths[currentPosition] = length;
      currentSlot = (currentSlot + FlagGroupSize - 1) & (FlagGroupSize - 1);
    }

    return tokenLengths;
  }

  private static void Relax(
      long[] costs,
      ulong[] choices,
      int destinationPosition,
      int destinationSlot,
      long candidateCost,
      byte tokenLength) {
    var costIndex = destinationPosition % MatchRingSize * FlagGroupSize + destinationSlot;
    if (candidateCost >= costs[costIndex])
      return;

    costs[costIndex] = candidateCost;
    var shift = destinationSlot * 8;
    var mask = 0xFFUL << shift;
    choices[destinationPosition] =
      (choices[destinationPosition] & ~mask) | ((ulong)tokenLength << shift);
  }

  private static byte[] EncodeBody(
      ReadOnlySpan<byte> input,
      ReadOnlySpan<Match> matches,
      ReadOnlySpan<byte> tokenLengths) {
    using var body = new MemoryStream();
    var position = 0;
    Span<int> starts = stackalloc int[FlagGroupSize];
    Span<byte> lengths = stackalloc byte[FlagGroupSize];

    while (position < input.Length) {
      byte flags = 0;
      var count = 0;
      var nextPosition = position;

      while (count < FlagGroupSize && nextPosition < input.Length) {
        var length = tokenLengths[nextPosition];
        if (length == 0)
          throw new InvalidOperationException("SZDD optimizer produced a gap in its token plan.");

        starts[count] = nextPosition;
        lengths[count] = length;
        if (length == 1)
          flags |= (byte)(1 << count);

        nextPosition += length;
        ++count;
      }

      body.WriteByte(flags);
      for (var i = 0; i < count; ++i) {
        var start = starts[i];
        var length = lengths[i];
        if (length == 1) {
          body.WriteByte(input[start]);
          continue;
        }

        var offset = matches[start].Offset;
        body.WriteByte((byte)offset);
        body.WriteByte((byte)(((offset >> 4) & 0xF0) | (length - SzddConstants.MinMatchLength)));
      }

      position = nextPosition;
    }

    return body.ToArray();
  }

  private static int Hash3(ReadOnlySpan<byte> input, int logicalPosition) {
    int hash = VirtualByte(input, logicalPosition);
    hash = hash * 251 + VirtualByte(input, logicalPosition + 1);
    hash = hash * 251 + VirtualByte(input, logicalPosition + 2);
    return hash & (MatchHashSize - 1);
  }

  private static byte VirtualByte(ReadOnlySpan<byte> input, int logicalPosition) =>
    logicalPosition < 0 ? SzddConstants.WindowFill : input[logicalPosition];

  private static byte GetChoice(ulong packedChoices, int slot) =>
    (byte)(packedChoices >> (slot * 8));
}
