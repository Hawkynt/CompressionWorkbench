using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Deflate;

/// <summary>
/// Hash-chain match finder that returns all matches at each position,
/// sorted by ascending length, for use by the optimal parser.
/// </summary>
internal sealed class ZopfliHashChain {
  private const int HashBits = 15;
  private const int HashSize = 1 << HashBits;
  private const int HashMask = HashSize - 1;

  private readonly int[] _head;
  private readonly int[] _prev;
  private readonly int _windowSize;

  /// <summary>
  /// Initializes a new <see cref="ZopfliHashChain"/>.
  /// </summary>
  /// <param name="windowSize">The maximum sliding window size.</param>
  public ZopfliHashChain(int windowSize = 32768) {
    this._windowSize = windowSize;
    this._head = new int[HashSize];
    this._prev = new int[windowSize];
    Array.Fill(this._head, -1);
  }

  /// <summary>
  /// Finds all matches at the given position, deduplicated by length (shortest distance per length),
  /// sorted by ascending length.
  /// </summary>
  public List<Match> FindAllMatches(ReadOnlySpan<byte> data, int position, int maxDistance, int maxLength) {
    var result = new List<Match>();

    if (position + 2 >= data.Length)
      return result;

    int hash = ComputeHash(data, position);
    var candidate = this._head[hash];
    var windowStart = Math.Max(0, position - maxDistance);

    int chainDepth = ComputeChainDepth(data, position);
    var chainCount = 0;

    // Track best distance per length: bestDistByLen[len] = shortest distance
    var effectiveMaxLen = Math.Min(maxLength, data.Length - position);
    var bestDistByLen = new int[effectiveMaxLen + 1];
    Array.Fill(bestDistByLen, int.MaxValue);

    while (candidate >= windowStart && chainCount < chainDepth) {
      int distance = position - candidate;
      var limit = Math.Min(effectiveMaxLen, data.Length - candidate);

      var length = 0;
      while (length < limit && data[candidate + length] == data[position + length])
        ++length;

      // Record best (shortest) distance for each achievable length
      if (length >= 3)
        for (int l = 3; l <= length; ++l)
          if (distance < bestDistByLen[l])
            bestDistByLen[l] = distance;

      candidate = this._prev[candidate & (this._windowSize - 1)];
      if (candidate <= windowStart)
        break;

      ++chainCount;
    }

    // Update hash chain
    this._prev[position & (this._windowSize - 1)] = this._head[hash];
    this._head[hash] = position;

    // Collect unique matches sorted by ascending length
    for (int l = 3; l <= effectiveMaxLen; ++l)
      if (bestDistByLen[l] < int.MaxValue)
        result.Add(new Match(bestDistByLen[l], l));

    return result;
  }

  /// <summary>
  /// Inserts a position into the hash chain without searching for matches.
  /// </summary>
  public void Insert(ReadOnlySpan<byte> data, int position) {
    if (position + 2 >= data.Length)
      return;

    int hash = ComputeHash(data, position);
    this._prev[position & (this._windowSize - 1)] = this._head[hash];
    this._head[hash] = position;
  }

  /// <summary>
  /// Computes adaptive chain depth based on byte diversity in a 64-byte window.
  /// </summary>
  private static int ComputeChainDepth(ReadOnlySpan<byte> data, int position) {
    var windowLen = Math.Min(64, data.Length - position);
    if (windowLen <= 0)
      return 512;

    Span<bool> seen = stackalloc bool[256];
    var unique = 0;
    for (int i = 0; i < windowLen; ++i) {
      byte dataByte = data[position + i];
      if (!seen[dataByte]) {
        seen[dataByte] = true;
        ++unique;
      }
    }

    double diversity = (double)unique / windowLen;

    if (diversity < 0.1)
      return 2048; // Low diversity — search deeper
    if (diversity > 0.6)
      return 128;  // High diversity — search shallower

    return 512;
  }

  private static int ComputeHash(ReadOnlySpan<byte> data, int position) =>
    ((data[position] << 10) ^ (data[position + 1] << 5) ^ data[position + 2]) & HashMask;
}
