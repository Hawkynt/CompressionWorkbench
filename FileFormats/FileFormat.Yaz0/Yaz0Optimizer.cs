using System.Buffers.Binary;

namespace FileFormat.Yaz0;

/// <summary>
/// Finds the smallest Yaz0 token stream for the supplied input.
/// </summary>
/// <remarks>
/// The parser minimizes the exact encoded body size, including one control byte
/// per group of eight tokens. Match discovery is exhaustive within Yaz0's 4 KiB
/// window; dynamic programming chooses between literals and every encodable
/// prefix of the longest match at each position.
/// </remarks>
public static class Yaz0Optimizer {
  private const int HeaderSize = 16;
  private const int WindowSize = 4096;
  private const int MinMatch = 3;
  private const int MaxShortMatch = 17;
  private const int MaxMatch = 273;
  private const int HashSize = 1 << 14;
  private const int HashMask = HashSize - 1;
  private const int MaxChain = WindowSize;
  private const int SlotsPerGroup = 8;

  /// <summary>
  /// Compresses all bytes from <paramref name="input"/> to <paramref name="output"/>
  /// using an exact minimum-size Yaz0 parse.
  /// </summary>
  public static void Optimize(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    using var buffer = new MemoryStream();
    input.CopyTo(buffer);
    var data = buffer.ToArray();

    Span<byte> header = stackalloc byte[HeaderSize];
    header.Clear();
    "Yaz0"u8.CopyTo(header);
    BinaryPrimitives.WriteUInt32BigEndian(header[4..], checked((uint)data.Length));
    output.Write(header);

    if (data.Length == 0)
      return;

    var matches = FindMatches(data);
    var choices = FindOptimalParse(matches.Lengths);
    WriteBody(output, data, matches.Distances, choices);
  }

  private static (ushort[] Lengths, ushort[] Distances) FindMatches(byte[] data) {
    var lengths = new ushort[data.Length];
    var distances = new ushort[data.Length];
    var head = new int[HashSize];
    var previous = new int[data.Length];
    Array.Fill(head, -1);
    Array.Fill(previous, -1);

    for (var position = 0; position < data.Length; ++position) {
      var (distance, length) = FindMatch(data, position, head, previous);
      if (length >= MinMatch) {
        lengths[position] = checked((ushort)length);
        distances[position] = checked((ushort)distance);
      }
      UpdateHash(data, position, head, previous);
    }

    return (lengths, distances);
  }

  private static ushort[][] FindOptimalParse(ushort[] matchLengths) {
    var choices = new ushort[SlotsPerGroup][];
    for (var slot = 0; slot < SlotsPerGroup; ++slot)
      choices[slot] = new ushort[matchLengths.Length];

    var tree = new RollingMinTree();
    for (var slot = 0; slot < SlotsPerGroup; ++slot)
      tree.Update(slot, matchLengths.Length, 0);

    Span<long> currentCosts = stackalloc long[SlotsPerGroup];

    for (var position = matchLengths.Length - 1; position >= 0; --position) {
      var maxMatch = matchLengths[position];

      for (var slot = 0; slot < SlotsPerGroup; ++slot) {
        var nextSlot = (slot + 1) & (SlotsPerGroup - 1);
        var groupOverhead = slot == 0 ? 1L : 0L;

        var best = AddCost(tree.Query(nextSlot, position + 1, position + 1), groupOverhead + 1);

        if (maxMatch >= MinMatch) {
          var end = position + Math.Min((int)maxMatch, MaxShortMatch);
          var candidate = AddCost(tree.Query(nextSlot, position + MinMatch, end), groupOverhead + 2);
          best = Better(best, candidate);
        }

        if (maxMatch > MaxShortMatch) {
          var candidate = AddCost(
            tree.Query(nextSlot, position + MaxShortMatch + 1, position + maxMatch),
            groupOverhead + 3);
          best = Better(best, candidate);
        }

        currentCosts[slot] = best.Cost;
        choices[slot][position] = checked((ushort)(best.Position - position));
      }

      for (var slot = 0; slot < SlotsPerGroup; ++slot)
        tree.Update(slot, position, currentCosts[slot]);
    }

    return choices;
  }

  private static void WriteBody(Stream output, byte[] data, ushort[] distances, ushort[][] choices) {
    var position = 0;
    var slot = 0;
    Span<byte> payload = stackalloc byte[SlotsPerGroup * 3];

    while (position < data.Length) {
      var control = 0;
      var payloadLength = 0;

      for (var bit = 7; bit >= 0 && position < data.Length; --bit) {
        var length = choices[slot][position];
        if (length == 1) {
          control |= 1 << bit;
          payload[payloadLength++] = data[position];
        } else {
          WriteReference(payload, ref payloadLength, distances[position], length);
        }

        position += length;
        slot = (slot + 1) & (SlotsPerGroup - 1);
      }

      output.WriteByte((byte)control);
      output.Write(payload[..payloadLength]);
    }
  }

  private static void WriteReference(Span<byte> payload, ref int payloadLength, int distance, int length) {
    var encodedDistance = distance - 1;
    if (length <= MaxShortMatch) {
      payload[payloadLength++] = (byte)((length - 2) << 4 | encodedDistance >> 8);
      payload[payloadLength++] = (byte)encodedDistance;
      return;
    }

    payload[payloadLength++] = (byte)(encodedDistance >> 8);
    payload[payloadLength++] = (byte)encodedDistance;
    payload[payloadLength++] = (byte)(length - 0x12);
  }

  private static (int Distance, int Length) FindMatch(byte[] data, int position, int[] head, int[] previous) {
    if (position + MinMatch > data.Length)
      return (0, 0);

    var hash = Hash3(data, position);
    var minimum = Math.Max(0, position - WindowSize);
    var maximumLength = Math.Min(MaxMatch, data.Length - position);
    var bestLength = 0;
    var bestDistance = 0;
    var candidate = head[hash];

    for (var walked = 0; candidate >= minimum && candidate >= 0 && walked < MaxChain; ++walked) {
      var length = 0;
      while (length < maximumLength && data[candidate + length] == data[position + length])
        ++length;

      if (length > bestLength) {
        bestLength = length;
        bestDistance = position - candidate;
        if (length == maximumLength)
          break;
      }

      candidate = previous[candidate];
    }

    return bestLength >= MinMatch ? (bestDistance, bestLength) : (0, 0);
  }

  private static void UpdateHash(byte[] data, int position, int[] head, int[] previous) {
    if (position + MinMatch > data.Length)
      return;

    var hash = Hash3(data, position);
    previous[position] = head[hash];
    head[hash] = position;
  }

  private static int Hash3(byte[] data, int position)
    => ((data[position] << 6) ^ (data[position + 1] << 3) ^ data[position + 2]) & HashMask;

  private static Candidate AddCost(Candidate candidate, long cost)
    => candidate.Position < 0 ? candidate : new(checked(candidate.Cost + cost), candidate.Position);

  private static Candidate Better(Candidate left, Candidate right) {
    if (left.Cost != right.Cost)
      return left.Cost < right.Cost ? left : right;
    return left.Position >= right.Position ? left : right;
  }

  private readonly record struct Candidate(long Cost, int Position) {
    public static Candidate Invalid => new(long.MaxValue, -1);
  }

  private sealed class RollingMinTree {
    // The active dependency window is at most 273 positions wide. A 512-entry
    // ring therefore gives every queried absolute position a unique live slot.
    private const int Capacity = 512;
    private const int Mask = Capacity - 1;

    private readonly Candidate[][] _trees = new Candidate[SlotsPerGroup][];

    public RollingMinTree() {
      for (var slot = 0; slot < this._trees.Length; ++slot) {
        var tree = new Candidate[Capacity * 2];
        Array.Fill(tree, Candidate.Invalid);
        this._trees[slot] = tree;
      }
    }

    public void Update(int slot, int position, long cost) {
      var tree = this._trees[slot];
      var index = Capacity + (position & Mask);
      tree[index] = new(cost, position);

      for (index >>= 1; index > 0; index >>= 1)
        tree[index] = Better(tree[index << 1], tree[(index << 1) | 1]);
    }

    public Candidate Query(int slot, int start, int end) {
      if (start > end)
        return Candidate.Invalid;

      var startBlock = start & ~Mask;
      var endBlock = end & ~Mask;
      if (startBlock == endBlock)
        return this.QueryModulo(slot, start & Mask, end & Mask);

      return Better(
        this.QueryModulo(slot, start & Mask, Mask),
        this.QueryModulo(slot, 0, end & Mask));
    }

    private Candidate QueryModulo(int slot, int start, int end) {
      var tree = this._trees[slot];
      var left = Capacity + start;
      var right = Capacity + end;
      var best = Candidate.Invalid;

      while (left <= right) {
        if ((left & 1) != 0)
          best = Better(best, tree[left++]);
        if ((right & 1) == 0)
          best = Better(best, tree[right--]);
        left >>= 1;
        right >>= 1;
      }

      return best;
    }
  }
}
