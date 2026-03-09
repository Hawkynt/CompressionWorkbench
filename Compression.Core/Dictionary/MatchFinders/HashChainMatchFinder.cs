namespace Compression.Core.Dictionary.MatchFinders;

/// <summary>
/// Hash-chain match finder using a 3-byte hash with configurable chain depth.
/// </summary>
public sealed class HashChainMatchFinder : IMatchFinder {
  private const int HashBits = 15;
  private const int HashSize = 1 << HashBits;
  private const int HashMask = HashSize - 1;

  private readonly int _maxChainDepth;
  private readonly int[] _head;
  private readonly int[] _prev;

  /// <summary>
  /// Initializes a new <see cref="HashChainMatchFinder"/>.
  /// </summary>
  /// <param name="windowSize">The maximum sliding window size.</param>
  /// <param name="maxChainDepth">The maximum hash chain depth to search. Defaults to 128.</param>
  public HashChainMatchFinder(int windowSize, int maxChainDepth = 128) {
    this._maxChainDepth = maxChainDepth;
    this._head = new int[HashSize];
    this._prev = new int[windowSize];
    Array.Fill(this._head, -1);
  }

  /// <inheritdoc />
  public Match FindMatch(ReadOnlySpan<byte> data, int position, int maxDistance, int maxLength, int minLength = 3) {
    if (position + 2 >= data.Length)
      return default;

    int bestDistance = 0;
    int bestLength = 0;

    int hash = ComputeHash(data, position);
    int candidate = this._head[hash];
    int chainCount = 0;

    int windowStart = Math.Max(0, position - maxDistance);

    while (candidate >= windowStart && chainCount < this._maxChainDepth) {
      int distance = position - candidate;

      // Quick check: compare first and last bytes of current best
      int limit = Math.Min(maxLength, Math.Min(data.Length - position, data.Length - candidate));
      if (bestLength == 0 || (bestLength < limit && data[candidate + bestLength] == data[position + bestLength])) {
        int length = 0;

        while (length < limit && data[candidate + length] == data[position + length])
          ++length;

        if (length >= minLength && length > bestLength) {
          bestLength = length;
          bestDistance = distance;

          if (bestLength >= maxLength)
            break;
        }
      }

      candidate = this._prev[candidate & (this._prev.Length - 1)];
      if (candidate <= windowStart)
        break;

      ++chainCount;
    }

    // Update hash chain
    this._prev[position & (this._prev.Length - 1)] = this._head[hash];
    this._head[hash] = position;

    return bestLength >= minLength ? new Match(bestDistance, bestLength) : default;
  }

  /// <summary>
  /// Inserts a position into the hash chain without searching for a match.
  /// Call this for positions that are skipped (e.g., inside a matched region).
  /// </summary>
  /// <param name="data">The input data buffer.</param>
  /// <param name="position">The position to insert.</param>
  public void InsertPosition(ReadOnlySpan<byte> data, int position) {
    if (position + 2 >= data.Length)
      return;

    int hash = ComputeHash(data, position);
    this._prev[position & (this._prev.Length - 1)] = this._head[hash];
    this._head[hash] = position;
  }

  private static int ComputeHash(ReadOnlySpan<byte> data, int position) =>
    ((data[position] << 10) ^ (data[position + 1] << 5) ^ data[position + 2]) & HashMask;
}
