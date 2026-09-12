using System.Buffers.Binary;

namespace FileFormat.Szdd;

/// <summary>
/// Size optimizer for the older Microsoft <c>"SZ "</c> COMPRESS stream used by
/// the QBasic-era tooling. The emitted stream remains the original 12-byte
/// header plus SZDD-compatible LZSS body; only the token parse is improved.
/// </summary>
/// <remarks>
/// The format charges one control byte for each group of up to eight tokens,
/// one payload byte for a literal and two for a 3..18-byte match. Greedily
/// taking the longest match is therefore not always size-optimal: a shorter
/// token can expose a later match or move a token across a control-byte group
/// boundary. This optimizer first finds every position's longest legal match,
/// then runs dynamic programming over (input position, token slot) so those
/// group costs are part of the objective.
/// </remarks>
public static class SzCompressOptimizer {
  private const int TokenSlots = 8;
  private const int CostRingRows = SzddConstants.MaxMatchLength + 1;

  /// <summary>
  /// Compresses <paramref name="input"/> as an optimized legacy <c>"SZ "</c>
  /// stream and writes it to <paramref name="output"/>.
  /// </summary>
  public static void Compress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    using var buffer = new MemoryStream();
    input.CopyTo(buffer);
    var length = checked((int)buffer.Length);
    output.Write(Compress(buffer.GetBuffer().AsSpan(0, length)));
  }

  /// <summary>
  /// Compresses <paramref name="input"/> as an optimized legacy <c>"SZ "</c>
  /// stream and returns the complete encoded file.
  /// </summary>
  public static byte[] Compress(ReadOnlySpan<byte> input) {
    if (input.IsEmpty)
      return CreateHeader(0);

    var maximumMatches = FindMaximumMatches(input);
    var choices = BuildOptimalParse(input.Length, maximumMatches);
    return Emit(input, choices);
  }

  private static byte[] FindMaximumMatches(ReadOnlySpan<byte> input) {
    var result = GC.AllocateUninitializedArray<byte>(input.Length);
    var finder = new MatchFinder();

    for (var position = 0; position < input.Length; ++position) {
      result[position] = (byte)finder.FindLongest(input, position, out _);
      finder.Insert(input, position);
    }

    return result;
  }

  private static byte[] BuildOptimalParse(int inputLength, ReadOnlySpan<byte> maximumMatches) {
    var choices = GC.AllocateUninitializedArray<byte>(checked(inputLength * TokenSlots));
    Span<long> costs = stackalloc long[CostRingRows * TokenSlots];
    costs.Fill(long.MaxValue / 4);
    costs.Slice((inputLength % CostRingRows) * TokenSlots, TokenSlots).Clear();

    for (var position = inputLength - 1; position >= 0; --position) {
      var rowOffset = (position % CostRingRows) * TokenSlots;

      for (var slot = 0; slot < TokenSlots; ++slot) {
        var nextSlot = (slot + 1) & (TokenSlots - 1);
        var controlCost = slot == 0 ? 1L : 0L;
        var bestChoice = 1;
        var bestCost = controlCost + 1 + GetCost(costs, position + 1, nextSlot);

        for (var length = SzddConstants.MinMatchLength; length <= maximumMatches[position]; ++length) {
          var candidateCost = controlCost + 2 + GetCost(costs, position + length, nextSlot);
          if (candidateCost > bestCost || candidateCost == bestCost && length <= bestChoice)
            continue;

          bestCost = candidateCost;
          bestChoice = length;
        }

        costs[rowOffset + slot] = bestCost;
        choices[checked(position * TokenSlots + slot)] = (byte)bestChoice;
      }
    }

    return choices;
  }

  private static long GetCost(ReadOnlySpan<long> costs, int position, int slot)
    => costs[(position % CostRingRows) * TokenSlots + slot];

  private static byte[] Emit(ReadOnlySpan<byte> input, ReadOnlySpan<byte> choices) {
    using var output = new MemoryStream();
    output.Write(CreateHeader(input.Length));

    var finder = new MatchFinder();
    var position = 0;

    while (position < input.Length) {
      var controlOffset = output.Position;
      output.WriteByte(0);
      byte control = 0;

      for (var slot = 0; slot < TokenSlots && position < input.Length; ++slot) {
        var choice = choices[checked(position * TokenSlots + slot)];
        if (choice == 1) {
          control |= (byte)(1 << slot);
          output.WriteByte(input[position]);
          finder.Insert(input, position);
          ++position;
          continue;
        }

        var available = finder.FindLongest(input, position, out var offset);
        if (available < choice)
          throw new InvalidOperationException("SZ optimizer parse references a match that is no longer available.");

        output.WriteByte((byte)offset);
        output.WriteByte((byte)(((offset >> 4) & 0xF0) | (choice - SzddConstants.MinMatchLength)));

        for (var i = 0; i < choice; ++i)
          finder.Insert(input, position + i);
        position += choice;
      }

      var end = output.Position;
      output.Position = controlOffset;
      output.WriteByte(control);
      output.Position = end;
    }

    return output.ToArray();
  }

  private static byte[] CreateHeader(int inputLength) {
    var header = new byte[SzddConstants.QBasicHeaderSize];
    SzddConstants.QBasicMagic.CopyTo(header);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)inputLength);
    return header;
  }

  /// <summary>
  /// Maintains three-byte hash buckets for the 4096 physical ring slots.
  /// Each slot also remembers which logical input position currently occupies
  /// it, which lets match verification model overlapping LZSS copies without
  /// depending on a particular tokenization.
  /// </summary>
  private sealed class MatchFinder {
    private readonly int[] _head = new int[SzddConstants.WindowSize];
    private readonly int[] _next = new int[SzddConstants.WindowSize];
    private readonly int[] _previous = new int[SzddConstants.WindowSize];
    private readonly int[] _hash = new int[SzddConstants.WindowSize];
    private readonly int[] _logicalPosition = new int[SzddConstants.WindowSize];
    private int _writePosition = SzddConstants.WindowInitPos;

    public MatchFinder() {
      Array.Fill(this._head, -1);
      Array.Fill(this._next, -1);
      Array.Fill(this._previous, -1);
      Array.Fill(this._hash, -1);

      var initialHash = Hash(SzddConstants.WindowFill, SzddConstants.WindowFill, SzddConstants.WindowFill);
      for (var logicalPosition = -SzddConstants.WindowSize; logicalPosition < 0; ++logicalPosition) {
        var slot = (SzddConstants.WindowInitPos + logicalPosition) & (SzddConstants.WindowSize - 1);
        this._logicalPosition[slot] = logicalPosition;
        this.InsertIntoBucket(slot, initialHash);
      }
    }

    public int FindLongest(ReadOnlySpan<byte> input, int position, out int offset) {
      offset = 0;
      var maximum = Math.Min(SzddConstants.MaxMatchLength, input.Length - position);
      if (maximum < SzddConstants.MinMatchLength)
        return 0;

      var bestLength = 0;
      for (var slot = this._head[Hash(input, position)]; slot >= 0; slot = this._next[slot]) {
        var sourcePosition = this._logicalPosition[slot];
        var length = 0;

        while (length < maximum) {
          var sourceIndex = sourcePosition + length;
          var source = sourceIndex < 0 ? SzddConstants.WindowFill : input[sourceIndex];
          if (source != input[position + length])
            break;
          ++length;
        }

        if (length <= bestLength)
          continue;

        bestLength = length;
        offset = slot;
        if (bestLength == maximum)
          break;
      }

      return bestLength;
    }

    public void Insert(ReadOnlySpan<byte> input, int position) {
      var slot = this._writePosition;
      this.RemoveFromBucket(slot);
      this._logicalPosition[slot] = position;

      if (position + 2 < input.Length)
        this.InsertIntoBucket(slot, Hash(input, position));

      this._writePosition = (this._writePosition + 1) & (SzddConstants.WindowSize - 1);
    }

    private void InsertIntoBucket(int slot, int hash) {
      var oldHead = this._head[hash];
      this._hash[slot] = hash;
      this._previous[slot] = -1;
      this._next[slot] = oldHead;
      if (oldHead >= 0)
        this._previous[oldHead] = slot;
      this._head[hash] = slot;
    }

    private void RemoveFromBucket(int slot) {
      var hash = this._hash[slot];
      if (hash < 0)
        return;

      var previous = this._previous[slot];
      var next = this._next[slot];
      if (previous >= 0)
        this._next[previous] = next;
      else
        this._head[hash] = next;
      if (next >= 0)
        this._previous[next] = previous;

      this._hash[slot] = -1;
      this._previous[slot] = -1;
      this._next[slot] = -1;
    }

    private static int Hash(ReadOnlySpan<byte> input, int position)
      => Hash(input[position], input[position + 1], input[position + 2]);

    private static int Hash(byte a, byte b, byte c)
      => ((a << 4) ^ (b << 2) ^ c) & (SzddConstants.WindowSize - 1);
  }
}
