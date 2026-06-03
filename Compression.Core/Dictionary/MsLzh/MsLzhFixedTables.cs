using Compression.Core.Entropy.Huffman;

namespace Compression.Core.Dictionary.MsLzh;

/// <summary>
/// Static (fixed) Huffman code-length tables used by the effort-0 MS LZH
/// encoder. Modelled on RFC 1951 §3.2.6 fixed Huffman: literals 0..143 are
/// 8 bits, 144..255 are 9 bits, the end-of-block marker (256) and length
/// symbols 257..279 are 7 bits, and the remaining length symbols 280..285
/// are 8 bits. Distance symbols are a flat 5 bits each.
/// <para>
/// The original DriveSpace 3 codec uses <em>dynamic</em> per-block Huffman
/// trees stored in a small prefix table; producing those tables and the
/// matching encoder is a significant additional engineering investment and
/// is currently deferred. The fixed-table form here is fully self-consistent
/// — the MS LZH decoder in this project reads back its own encoder's output,
/// but the byte stream is NOT bit-compatible with a Microsoft-produced
/// DriveSpace 3 image.
/// </para>
/// </summary>
internal static class MsLzhFixedTables {
  internal static readonly CanonicalHuffman LitLen;
  internal static readonly CanonicalHuffman Distance;

  /// <summary>Per-symbol fixed-table code lengths for the literal/length alphabet.</summary>
  internal static readonly int[] LitLenLengths;
  /// <summary>Per-symbol fixed-table code lengths for the distance alphabet.</summary>
  internal static readonly int[] DistanceLengths;

  static MsLzhFixedTables() {
    LitLenLengths = new int[MsLzhConstants.LitLenAlphabetSize];
    for (var i = 0; i <= 143; i++) LitLenLengths[i] = 8;
    for (var i = 144; i <= 255; i++) LitLenLengths[i] = 9;
    for (var i = 256; i <= 279; i++) LitLenLengths[i] = 7;
    for (var i = 280; i <= 285; i++) LitLenLengths[i] = 8;
    LitLen = new CanonicalHuffman(LitLenLengths);

    DistanceLengths = new int[MsLzhConstants.DistanceAlphabetSize];
    for (var i = 0; i < MsLzhConstants.DistanceAlphabetSize; i++) DistanceLengths[i] = 5;
    Distance = new CanonicalHuffman(DistanceLengths);
  }
}
