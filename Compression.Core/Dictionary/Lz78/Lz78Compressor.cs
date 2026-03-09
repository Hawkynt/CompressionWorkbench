namespace Compression.Core.Dictionary.Lz78;

/// <summary>
/// Compresses data using the LZ78 algorithm with a trie-based dictionary.
/// </summary>
public sealed class Lz78Compressor {
  private readonly int _maxEntries;

  /// <summary>
  /// Initializes a new instance of the <see cref="Lz78Compressor"/> class.
  /// </summary>
  /// <param name="maxBits">
  /// Maximum number of bits for dictionary indices. The dictionary resets
  /// when it reaches 2^<paramref name="maxBits"/> entries. Default is 12.
  /// </param>
  public Lz78Compressor(int maxBits = 12) {
    this._maxEntries = 1 << maxBits;
  }

  /// <summary>
  /// Compresses the input data into a sequence of LZ78 tokens.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>A list of <see cref="Lz78Token"/> representing the compressed output.</returns>
  public List<Lz78Token> Compress(ReadOnlySpan<byte> data) {
    var tokens = new List<Lz78Token>();

    if (data.IsEmpty)
      return tokens;

    // Trie stored as (parentIndex, childByte) -> entryIndex.
    // Entry 0 is the root (empty string).
    var trie = new Dictionary<(int ParentIndex, byte Child), int>();
    int nextIndex = 1; // next dictionary entry index to assign
    int currentIndex = 0; // current node in the trie (0 = root)

    for (int i = 0; i < data.Length; ++i) {
      byte b = data[i];
      var key = (currentIndex, b);

      if (trie.TryGetValue(key, out int childIndex)) {
        // Extend the current match.
        currentIndex = childIndex;
      }
      else {
        // Mismatch: emit token and add new entry.
        tokens.Add(new Lz78Token(currentIndex, b));
        trie[key] = nextIndex;
        ++nextIndex;
        currentIndex = 0;

        // Reset dictionary when it reaches maximum size.
        if (nextIndex >= this._maxEntries) {
          trie.Clear();
          nextIndex = 1;
        }
      }
    }

    // If we ended mid-match, emit a terminal token.
    if (currentIndex > 0)
      tokens.Add(new Lz78Token(currentIndex, null));

    return tokens;
  }
}
