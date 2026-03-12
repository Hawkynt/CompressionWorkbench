using System.Buffers.Binary;
using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Xpress;

/// <summary>
/// Compresses data using the XPRESS (LZ Xpress plain) algorithm.
/// </summary>
/// <remarks>
/// <para>
/// Data is organized in groups of up to 32 items. Each group begins with a 32-bit
/// little-endian flag word: bit 0 (LSB) corresponds to the first item, bit 31 to the
/// last. A 0 bit means the item is a literal byte; a 1 bit means it is an LZ match.
/// </para>
/// <para>
/// Match encoding — a 16-bit LE value plus optional extra bytes:
/// <code>
///   value      = (offset &lt;&lt; 3) | lengthHeader
///   lengthHeader = min(length - 3, 7)
///
///   if lengthHeader &lt; 7 : length = lengthHeader + 3   (lengths 3..9)
///   if lengthHeader == 7:
///     extra = length - 3          (write as one byte if 0..254)
///     if length - 3 &lt;  255 : emit byte(length-3)
///     if length - 3 >= 255 : emit byte(255) + uint16(length)   [lengths 258..65535]
///     if length     == 0   : emit byte(255) + uint16(0) + uint32(length)
/// </code>
/// </para>
/// </remarks>
public sealed class XpressCompressor {
  private readonly int _maxChainDepth;

  /// <summary>
  /// Initializes a new <see cref="XpressCompressor"/>.
  /// </summary>
  /// <param name="maxChainDepth">Hash-chain search depth. Higher values improve ratio at the cost of speed.</param>
  public XpressCompressor(int maxChainDepth = 128) {
    ArgumentOutOfRangeException.ThrowIfLessThan(maxChainDepth, 1);
    this._maxChainDepth = maxChainDepth;
  }

  /// <summary>
  /// Compresses <paramref name="input"/> and returns the compressed bytes.
  /// </summary>
  /// <param name="input">The data to compress.</param>
  /// <returns>The XPRESS-compressed data.</returns>
  public byte[] Compress(ReadOnlySpan<byte> input) {
    if (input.IsEmpty)
      return [];

    using var output = new MemoryStream(input.Length);
    this.Compress(input, output);
    return output.ToArray();
  }

  /// <summary>
  /// Compresses <paramref name="input"/> and writes the result to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The data to compress.</param>
  /// <param name="output">The stream to write compressed data to.</param>
  public void Compress(ReadOnlySpan<byte> input, Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    if (input.IsEmpty)
      return;

    var matchFinder = new HashChainMatchFinder(XpressConstants.WindowSize, this._maxChainDepth);

    // Per-group staging buffer:
    //   4 bytes  flag word placeholder
    //   up to 32 items, worst case 6 bytes each (2 header + 1 sentinel + 1 + 2 or 4 extra)
    Span<byte> groupBuf = stackalloc byte[4 + XpressConstants.FlagGroupSize * 8];
    var pos = 0;

    while (pos < input.Length) {
      var flagWord = 0u;
      var groupLen = 0; // bytes written into groupBuf after the 4-byte flag slot
      var itemCount = 0;

      while (itemCount < XpressConstants.FlagGroupSize && pos < input.Length) {
        var (offset, length) = matchFinder.FindMatch(
          input, pos,
          XpressConstants.WindowSize,
          XpressConstants.MaxMatch,
          XpressConstants.MinMatch);

        if (length >= XpressConstants.MinMatch) {
          // Set corresponding flag bit (bit 0 = first item)
          flagWord |= 1u << itemCount;

          var lengthHeader = Math.Min(length - XpressConstants.MinMatch, 7);

          // 16-bit match value: offset in upper 13 bits, lengthHeader in lower 3 bits
          var value = (ushort)((offset << 3) | lengthHeader);
          BinaryPrimitives.WriteUInt16LittleEndian(groupBuf.Slice(4 + groupLen, 2), value);
          groupLen += 2;

          if (lengthHeader == 7) {
            // Encode the adjusted length: adj = length - MinMatch = length - 3
            var adj = length - XpressConstants.MinMatch;  // 0-based extra
            if (adj < XpressConstants.LengthSentinel8)
              // Fits directly in one byte (adj 0..254 → lengths 3..257)
              groupBuf[4 + groupLen++] = (byte)adj;
            else if (length <= 65535) {
              // Sentinel byte + 16-bit length
              groupBuf[4 + groupLen++] = XpressConstants.LengthSentinel8;
              BinaryPrimitives.WriteUInt16LittleEndian(groupBuf.Slice(4 + groupLen, 2), (ushort)length);
              groupLen += 2;
            } else {
              // Sentinel byte + sentinel 16-bit + 32-bit length
              groupBuf[4 + groupLen++] = XpressConstants.LengthSentinel8;
              BinaryPrimitives.WriteUInt16LittleEndian(groupBuf.Slice(4 + groupLen, 2), XpressConstants.LengthSentinel16);
              groupLen += 2;
              BinaryPrimitives.WriteUInt32LittleEndian(groupBuf.Slice(4 + groupLen, 4), (uint)length);
              groupLen += 4;
            }
          }

          // Insert skipped positions into hash chain
          for (var i = 1; i < length; ++i)
            matchFinder.InsertPosition(input, pos + i);

          pos += length;
        } else
          // Literal: flag bit stays 0
          groupBuf[4 + groupLen++] = input[pos++];

        ++itemCount;
      }

      // Write the flag word, then the encoded items
      BinaryPrimitives.WriteUInt32LittleEndian(groupBuf, flagWord);
      output.Write(groupBuf[..(4 + groupLen)]);
    }
  }
}
