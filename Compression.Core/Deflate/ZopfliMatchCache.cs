namespace Compression.Core.Deflate;

/// <summary>
/// Holds the match runs of every position in the input, searched once and read many times.
/// </summary>
/// <remarks>
/// <para>
/// Zopfli parses the same input over and over, each pass differing only in how it prices
/// symbols. The matches themselves never change: they depend on the bytes and the 32 KB
/// window, not on the cost model. Searching them once and keeping the answer — the
/// longest-match cache of the published method — is what makes a dozen passes affordable,
/// and it is also what lets the block splitter and every block's parse share one search.
/// </para>
/// <para>
/// Runs, not individual lengths, are stored: at one position all the lengths that share a
/// distance form a contiguous span (see <see cref="ZopfliHashChain"/>), and there are only
/// a handful of spans even where there are hundreds of lengths.
/// </para>
/// </remarks>
internal sealed class ZopfliMatchCache {
  /// <summary>The shortest back-reference RFC 1951 can express.</summary>
  public const int MinMatch = 3;

  /// <summary>The longest back-reference RFC 1951 can express.</summary>
  public const int MaxMatch = 258;

  private readonly int[] _runStart;
  private readonly ushort[] _runMaxLength;
  private readonly ushort[] _runDistance;

  private ZopfliMatchCache(int[] runStart, ushort[] runMaxLength, ushort[] runDistance) {
    this._runStart = runStart;
    this._runMaxLength = runMaxLength;
    this._runDistance = runDistance;
  }

  /// <summary>
  /// Searches every position of <paramref name="data"/> once and caches the result.
  /// </summary>
  /// <param name="data">The whole input.</param>
  /// <returns>The populated cache.</returns>
  public static ZopfliMatchCache Build(ReadOnlySpan<byte> data) {
    var chain = new ZopfliHashChain(DeflateConstants.WindowSize);
    var runStart = new int[data.Length + 1];
    List<ushort> maxLengths = [];
    List<ushort> distances = [];

    for (var position = 0; position < data.Length; ++position) {
      runStart[position] = maxLengths.Count;
      chain.FindMatchRuns(data, position, DeflateConstants.WindowSize, ZopfliMatchCache.MaxMatch, maxLengths, distances);
    }

    runStart[data.Length] = maxLengths.Count;
    return new(runStart, [.. maxLengths], [.. distances]);
  }

  /// <summary>Index of the first run belonging to <paramref name="position"/>.</summary>
  public int RunStart(int position) => this._runStart[position];

  /// <summary>Index one past the last run belonging to <paramref name="position"/>.</summary>
  public int RunEnd(int position) => this._runStart[position + 1];

  /// <summary>The greatest match length run <paramref name="run"/> covers.</summary>
  public int MaxLengthOf(int run) => this._runMaxLength[run];

  /// <summary>The distance run <paramref name="run"/> uses.</summary>
  public int DistanceOf(int run) => this._runDistance[run];

  /// <summary>
  /// The longest match at <paramref name="position"/>, or a length of zero if there is none.
  /// </summary>
  /// <param name="position">The position to look up.</param>
  /// <returns>The longest match's length and distance.</returns>
  public (int Length, int Distance) LongestMatch(int position) {
    var end = this.RunEnd(position);
    return end == this.RunStart(position)
      ? (0, 0)
      : (this._runMaxLength[end - 1], this._runDistance[end - 1]);
  }
}
