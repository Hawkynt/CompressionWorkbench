using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Lz77;

/// <summary>
/// Produces a sequence of LZ77 tokens from input data using a match finder.
/// </summary>
public sealed class Lz77Compressor {
  private readonly IMatchFinder _matchFinder;
  private readonly int _windowSize;
  private readonly int _maxMatchLength;
  private readonly int _minMatchLength;

  /// <summary>
  /// Initializes a new <see cref="Lz77Compressor"/>.
  /// </summary>
  /// <param name="matchFinder">The match finder to use.</param>
  /// <param name="windowSize">The sliding window size (max distance). Defaults to 32768.</param>
  /// <param name="maxMatchLength">The maximum match length. Defaults to 258.</param>
  /// <param name="minMatchLength">The minimum match length. Defaults to 3.</param>
  public Lz77Compressor(
    IMatchFinder matchFinder,
    int windowSize = 32768,
    int maxMatchLength = 258,
    int minMatchLength = 3) {
    this._matchFinder = matchFinder ?? throw new ArgumentNullException(nameof(matchFinder));
    this._windowSize = windowSize;
    this._maxMatchLength = maxMatchLength;
    this._minMatchLength = minMatchLength;
  }

  /// <summary>
  /// Compresses the input data into a list of LZ77 tokens.
  /// </summary>
  /// <param name="data">The input data to compress.</param>
  /// <returns>A list of LZ77 tokens representing the compressed data.</returns>
  public List<Lz77Token> Compress(ReadOnlySpan<byte> data) {
    var tokens = new List<Lz77Token>();
    int position = 0;

    while (position < data.Length) {
      var match = this._matchFinder.FindMatch(data, position, this._windowSize, this._maxMatchLength, this._minMatchLength);

      if (match.Length >= this._minMatchLength) {
        tokens.Add(Lz77Token.CreateMatch(match.Distance, match.Length));

        // Insert skipped positions into hash chain
        for (int i = 1; i < match.Length; ++i)
          if (this._matchFinder is HashChainMatchFinder hcmf)
            hcmf.InsertPosition(data, position + i);

        position += match.Length;
      }
      else {
        tokens.Add(Lz77Token.CreateLiteral(data[position]));
        ++position;
      }
    }

    return tokens;
  }
}
