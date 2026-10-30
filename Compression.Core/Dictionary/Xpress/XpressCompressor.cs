using System.Buffers.Binary;
using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Xpress;

/// <summary>
/// Compresses data with the plain LZ77 variant of XPRESS ([MS-XCA] section 2.3), the format
/// RtlDecompressBuffer reads as COMPRESSION_FORMAT_XPRESS.
/// </summary>
/// <remarks>
/// Items are grouped under 32-bit flag words filled highest bit first (1 = match); the last word is
/// padded with 1 bits. A match is a 16-bit word, (distance - 1) &lt;&lt; 3 | min(length - 3, 7), and a
/// longer length continues in a shared half byte, then a byte, then a 16- or 32-bit value
/// (see <see cref="XpressDecompressor"/>). Distances reach 8192 bytes.
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
  /// Initializes a new <see cref="XpressCompressor"/> with the specified compression level.
  /// </summary>
  /// <param name="level">The compression level.</param>
  public XpressCompressor(XpressCompressionLevel level) : this(level switch {
    XpressCompressionLevel.Fast => 16,
    XpressCompressionLevel.Best => 512,
    _ => 128
  }) { }

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
    // Worst case: every byte a literal, plus a flag word per 32 items.
    var buffer = new byte[input.Length + input.Length / 8 + 16];
    var outPos = 4;
    var flagPos = 0;
    var flags = 0u;
    var flagCount = 0;
    var halfByte = -1;
    var pos = 0;

    while (pos < input.Length) {
      var (distance, length) = matchFinder.FindMatch(input, pos, XpressConstants.WindowSize, XpressConstants.MaxMatch, XpressConstants.MinMatch);
      if (length >= XpressConstants.MinMatch) {
        flags = flags << 1 | 1;
        var extra = length - XpressConstants.MinMatch;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(outPos), (ushort)((distance - 1) << 3 | Math.Min(extra, 7)));
        outPos += 2;
        if (extra >= 7) {
          var nibble = Math.Min(extra - 7, 15);
          if (halfByte < 0) {
            halfByte = outPos;
            buffer[outPos++] = (byte)nibble;
          } else {
            buffer[halfByte] |= (byte)(nibble << 4);
            halfByte = -1;
          }

          if (extra - 7 >= 15) {
            if (extra - 7 - 15 < 255)
              buffer[outPos++] = (byte)(extra - 7 - 15);
            else {
              buffer[outPos++] = 255;
              if (extra < 1 << 16) {
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(outPos), (ushort)extra);
                outPos += 2;
              } else {
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(outPos), 0);
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(outPos + 2), (uint)extra);
                outPos += 6;
              }
            }
          }
        }

        for (var i = 1; i < length; ++i)
          matchFinder.InsertPosition(input, pos + i);

        pos += length;
      } else {
        flags <<= 1;
        buffer[outPos++] = input[pos++];
      }

      if (++flagCount == 32) {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(flagPos), flags);
        flagPos = outPos;
        outPos += 4;
        flags = 0;
        flagCount = 0;
      }

      if (outPos + 16 > buffer.Length)
        Array.Resize(ref buffer, buffer.Length * 2);
    }

    // Pad the last flag word with match bits: a decoder that runs out of input there stops.
    var padding = 32 - flagCount;
    flags = padding == 32 ? uint.MaxValue : flags << padding | (1u << padding) - 1;
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(flagPos), flags);
    output.Write(buffer, 0, outPos);
  }
}
