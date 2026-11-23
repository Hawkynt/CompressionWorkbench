using Compression.Core.Deflate;

namespace FileFormat.AlZip;

/// <summary>
/// ALZip's obfuscated deflate (method 3): RFC 1951 Deflate whose dynamic blocks list their 19
/// code-length code lengths in a permuted order instead of RFC 1951's, the permutation keyed on
/// the member's uncompressed size modulo 16. Everything else in the bit stream is plain Deflate.
/// </summary>
/// <remarks>
/// Converted from XADMaster's <c>XADALZipParser.m</c> (LGPL-2.1-or-later); see
/// <c>THIRD-PARTY-NOTICE.XADMaster.txt</c>.
/// </remarks>
public static class AlZipObfuscatedDeflate {
  /// <summary>
  /// The code-length order for a member of <paramref name="uncompressedSize"/> bytes: start from
  /// the identity order 0..18, then for each position i swap it with (i mod 6)·3 + (size mod 16),
  /// that index reduced modulo 18 when it exceeds 18.
  /// </summary>
  /// <remarks>
  /// XADMaster takes the size as a 32-bit signed integer before reducing it, which leaves the key
  /// undefined for members of 2 GiB and more; this uses the size's low four bits throughout, which
  /// is the same key for every size below that.
  /// </remarks>
  public static int[] CodeLengthOrder(long uncompressedSize) {
    ArgumentOutOfRangeException.ThrowIfNegative(uncompressedSize);
    var key = (int)(uncompressedSize & 15);
    var order = new int[DeflateConstants.CodeLengthAlphabetSize];
    for (var i = 0; i < order.Length; ++i)
      order[i] = i;
    for (var i = 0; i < order.Length; ++i) {
      var swap = i % 6 * 3 + key;
      if (swap > 18)
        swap %= 18;
      (order[i], order[swap]) = (order[swap], order[i]);
    }

    return order;
  }

  /// <summary>Decodes a method-3 member whose uncompressed size is <paramref name="uncompressedSize"/>.</summary>
  public static byte[] Decompress(byte[] packed, long uncompressedSize) {
    ArgumentNullException.ThrowIfNull(packed);
    using var input = new MemoryStream(packed, writable: false);
    return new DeflateDecompressor(input) { CodeLengthOrder = CodeLengthOrder(uncompressedSize) }.DecompressAll();
  }

  /// <summary>Encodes <paramref name="data"/> as a method-3 member.</summary>
  public static byte[] Compress(ReadOnlySpan<byte> data, DeflateCompressionLevel level = DeflateCompressionLevel.Default) {
    using var output = new MemoryStream();
    var compressor = new DeflateCompressor(output, level) { CodeLengthOrder = CodeLengthOrder(data.Length) };
    compressor.Write(data);
    compressor.Finish();
    return output.ToArray();
  }
}
