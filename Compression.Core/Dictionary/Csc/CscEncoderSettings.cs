namespace Compression.Core.Dictionary.Csc;

/// <summary>
/// The complete encoder configuration libcsc derives from a window size and a level
/// (<c>CSCEncProps_Init</c>): match-finder geometry, parser mode and filters.
/// </summary>
internal readonly record struct CscEncoderSettings(
  uint DictionarySize,
  uint CscBlockSize,
  uint RawBlockSize,
  int HashBits,
  int HashWidth,
  int BinaryTreeHashBits,
  uint BinaryTreeSize,
  uint BinaryTreeCycles,
  uint GoodLength,
  int LzMode,
  bool DeltaFilter,
  bool TextFilter,
  bool ExecutableFilter) {
  private const uint KiB = 1024;
  private const uint MiB = 1024 * 1024;

  /// <summary>The property header these settings produce.</summary>
  public CscStreamProperties Properties => new(this.DictionarySize, this.CscBlockSize, this.RawBlockSize);

  /// <summary>Whether the analyzer runs at all.</summary>
  public bool UseFilters => this.DeltaFilter || this.TextFilter || this.ExecutableFilter;

  /// <summary>
  /// Builds the settings the reference <c>csc</c> tool would use for <paramref name="options"/> on an
  /// input of <paramref name="inputLength"/> bytes (<see langword="null"/> when unknown).
  /// </summary>
  public static CscEncoderSettings Create(CscEncoderOptions options, long? inputLength) {
    var requested = options.DictionarySize;
    if (inputLength is { } length && length < requested)
      requested = length;

    // libcsc keeps 8 KiB of the window as match-finder reserve, so the tool asks for 10 KiB more.
    var dictionary = (uint)Math.Clamp(requested + 10 * KiB, CscConstants.MinDictionarySize, CscConstants.MaxDictionarySize);
    var level = Math.Clamp(options.Level, 1, 5);

    var hashBits = dictionary switch {
      < MiB => 19,
      <= 4 * MiB => 20,
      <= 16 * MiB => 21,
      <= 64 * MiB => 22,
      <= 256 * MiB => 23,
      _ => 24,
    };
    while ((1u << hashBits) > dictionary)
      --hashBits;

    var treeSize = dictionary switch {
      <= 16 * MiB => dictionary,
      <= 64 * MiB => (dictionary - 16 * MiB) / 2 + 16 * MiB,
      <= 256 * MiB => (dictionary - 64 * MiB) / 4 + 40 * MiB,
      _ => (dictionary - 256 * MiB) / 8 + 88 * MiB,
    };

    var treeHashBits = hashBits + 1;
    var (hashWidth, lzMode, goodLength, treeCycles) = level switch {
      1 => (1, 2, 32u, 0u),
      2 => (8, 2, 24u, 0u),
      3 => (2, 3, 16u, 0u),
      4 => (8, 3, 24u, 0u),
      _ => (0, 3, 48u, 32u),
    };
    hashBits += level switch {
      1 or 3 => 1,
      2 or 4 => -1,
      _ => 0,
    };
    if (level < 5)
      treeSize = 0;
    if (treeSize == dictionary)
      hashWidth = 0;

    return new(
      dictionary,
      CscConstants.DefaultCscBlockSize,
      CscConstants.DefaultRawBlockSize,
      hashBits,
      hashWidth,
      treeHashBits,
      treeSize,
      treeCycles,
      goodLength,
      lzMode,
      options.DeltaFilter,
      options.TextFilter,
      options.ExecutableFilter);
  }
}
