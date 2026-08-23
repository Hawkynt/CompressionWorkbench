namespace Compression.Core.Dictionary.Lzx;

/// <summary>
/// Which of the two arrangements of an LZX stream is in use.
/// </summary>
/// <remarks>
/// <para>The encoding is the same either way — the trees, the position slots,
/// the three remembered offsets. What differs is the framing around it, and a
/// reader of one arrangement is a bit or two out of step from the first symbol
/// of the other.</para>
/// </remarks>
public enum LzxStreamFormat {
  /// <summary>
  /// The arrangement a WIM uses: no header ahead of the first block, and a block
  /// size given as a single bit meaning "the usual 32 768" or sixteen bits
  /// otherwise. Each chunk is a stream of its own.
  /// </summary>
  Wim = 0,

  /// <summary>
  /// The arrangement a cabinet uses: the stream opens with a bit saying whether
  /// x86 call targets were rewritten (and a 32-bit size if they were), and block
  /// sizes are twenty-four bits. One stream covers a whole folder, however many
  /// data records it is cut into.
  /// </summary>
  Cab = 1,
}
