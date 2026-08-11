namespace Compression.Core.Deflate;

/// <summary>
/// Hash-chain match finder that reports, for one position, every match length the
/// shortest-path parser could use, each paired with the shortest distance that achieves it.
/// </summary>
/// <remarks>
/// <para>
/// A shortest-path parse needs more than the single longest match at a position: a shorter
/// match may leave the remaining input in a cheaper state, so every reachable length is a
/// candidate edge. RFC 1951 lets a match run to 258 bytes, so a position can offer up to
/// 256 distinct lengths.
/// </para>
/// <para>
/// The chain is walked newest-first, so candidate distances only ever grow as the walk
/// proceeds. The first candidate to reach a given length therefore reaches it at the
/// shortest distance available, and no later candidate can improve on it. That turns the
/// answer into a short list of runs: lengths 3 up to the first candidate's length share
/// its distance, the next lengths up to the second candidate's length share the second
/// candidate's distance, and so on. Shorter distances also cost fewer bits, so preferring
/// them is never wrong.
/// </para>
/// </remarks>
internal sealed class ZopfliHashChain {
  private const int HashBits = 15;
  private const int HashSize = 1 << ZopfliHashChain.HashBits;
  private const int HashMask = ZopfliHashChain.HashSize - 1;

  /// <summary>
  /// How many chain links a single position may examine. The walk also stops as soon as a
  /// maximal match is in hand, which is what keeps runs of one repeated byte cheap, so the
  /// cap only bites on input whose three-byte prefixes collide often without the matches
  /// themselves getting long. Four thousand links is where the ratio stops improving
  /// measurably on such input; it is also the depth zlib's own strongest setting uses.
  /// </summary>
  private const int MaxChainHits = 4096;

  private readonly int[] _head;
  private readonly int[] _prev;
  private readonly int _windowSize;

  /// <summary>
  /// Initializes a new <see cref="ZopfliHashChain"/>.
  /// </summary>
  /// <param name="windowSize">The maximum sliding window size.</param>
  public ZopfliHashChain(int windowSize = 32768) {
    this._windowSize = windowSize;
    this._head = new int[ZopfliHashChain.HashSize];
    this._prev = new int[windowSize];
    this._head.AsSpan().Fill(-1);
    this._prev.AsSpan().Fill(-1);
  }

  /// <summary>
  /// Appends the match runs available at <paramref name="position"/> and inserts that
  /// position into the chain.
  /// </summary>
  /// <param name="data">The whole input.</param>
  /// <param name="position">The position to search from.</param>
  /// <param name="maxDistance">The furthest back a match may reach.</param>
  /// <param name="maxLength">The longest match to consider.</param>
  /// <param name="runMaxLength">Receives the greatest length each run covers.</param>
  /// <param name="runDistance">Receives the distance each run uses.</param>
  /// <remarks>
  /// Run <c>k</c> covers the lengths from <c>runMaxLength[k-1] + 1</c> (or 3 for the first
  /// run) through <c>runMaxLength[k]</c>, all at distance <c>runDistance[k]</c>.
  /// </remarks>
  public void FindMatchRuns(
    ReadOnlySpan<byte> data,
    int position,
    int maxDistance,
    int maxLength,
    List<ushort> runMaxLength,
    List<ushort> runDistance) {
    // The hash covers three bytes, so the last two positions can neither be searched
    // for nor entered into the chain.
    if (position + 2 >= data.Length)
      return;

    var hash = ComputeHash(data, position);
    var candidate = this._head[hash];
    var windowStart = Math.Max(0, position - maxDistance);
    var effectiveMaxLength = Math.Min(maxLength, data.Length - position);

    var mask = this._windowSize - 1;
    var hits = 0;
    var bestLength = 2; // one below the shortest match RFC 1951 can express

    while (candidate >= windowStart && hits < ZopfliHashChain.MaxChainHits) {
      var distance = position - candidate;

      var length = 0;
      while (length < effectiveMaxLength && data[candidate + length] == data[position + length])
        ++length;

      if (length > bestLength) {
        runMaxLength.Add((ushort)length);
        runDistance.Add((ushort)distance);
        bestLength = length;

        // Nothing further back can beat a maximal match, and no shorter length is left
        // uncovered, so the walk is done.
        if (bestLength >= effectiveMaxLength)
          break;
      }

      var next = this._prev[candidate & mask];

      // The chain runs strictly backwards; anything else is an entry from a previous
      // trip round the window and must not be followed.
      if (next < 0 || next >= candidate)
        break;

      candidate = next;
      ++hits;
    }

    this._prev[position & mask] = this._head[hash];
    this._head[hash] = position;
  }

  private static int ComputeHash(ReadOnlySpan<byte> data, int position) =>
    ((data[position] << 10) ^ (data[position + 1] << 5) ^ data[position + 2]) & ZopfliHashChain.HashMask;
}
