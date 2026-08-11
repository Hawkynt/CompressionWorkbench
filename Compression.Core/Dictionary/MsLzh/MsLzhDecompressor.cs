using System.Buffers.Binary;
using Compression.Core.BitIO;
using Compression.Core.Entropy.Huffman;

namespace Compression.Core.Dictionary.MsLzh;

/// <summary>
/// MS LZH decompressor — reads back the bit stream produced by
/// <see cref="MsLzhCompressor"/>. Reads a 4-byte little-endian original-size
/// header followed by a sequence of blocks. Each block is prefixed by a
/// single block-type bit: <c>0</c> selects the RFC 1951 §3.2.6-shape
/// fixed Huffman tables (see <see cref="MsLzhFixedTables"/>); <c>1</c>
/// selects per-block dynamic Huffman tables whose header layout follows
/// RFC 1951 §3.2.7 (HLIT / HDIST / HCLEN + code-length-code table +
/// lit/len + distance code-length lists — see
/// <see cref="MsLzhDynamicHuffman"/>). End-of-block (symbol 256) closes a
/// block; the decoder stops once the original-size byte count has been
/// emitted.
/// </summary>
public sealed class MsLzhDecompressor {

  /// <summary>Decompresses an MS LZH bit stream.</summary>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 4)
      throw new InvalidDataException("MS LZH: input too small for header.");

    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize < 0)
      throw new InvalidDataException("MS LZH: negative original size.");
    if (originalSize == 0)
      return [];

    using var ms = new MemoryStream(data[4..].ToArray());
    var reader = new BitReader<MsbBitOrder>(ms);

    var output = new byte[originalSize];
    var pos = 0;
    // Decode budget, in 64-bit: at 32-bit width this wraps negative for sizes of
    // 2^28 bytes and up, which stops the symbol loop before it emits anything.
    var safety = (long)originalSize * 8 + 1024;

    while (pos < originalSize) {
      // Block-type bit: 0 = fixed Huffman tables, 1 = dynamic per-block tables.
      var blockType = reader.ReadBit();
      CanonicalHuffman litLenHuf, distHuf;
      switch (blockType) {
        case MsLzhDynamicHuffman.BlockTypeFixed:
          litLenHuf = MsLzhFixedTables.LitLen;
          distHuf = MsLzhFixedTables.Distance;
          break;
        case MsLzhDynamicHuffman.BlockTypeDynamic:
          (litLenHuf, distHuf) = MsLzhDynamicHuffman.ReadHeader(reader);
          break;
        default:
          throw new InvalidDataException($"MS LZH: invalid block-type bit {blockType}.");
      }

      while (pos < originalSize && safety-- > 0) {
        var symbol = litLenHuf.DecodeSymbol(reader);
        if (symbol < 256) {
          output[pos++] = (byte)symbol;
          continue;
        }
        if (symbol == MsLzhConstants.EndOfBlockSymbol)
          break;
        if (symbol > 285)
          throw new InvalidDataException($"MS LZH: invalid literal/length symbol {symbol}.");

        // Match: decode length then distance.
        var (_, lenExtraBits) = MsLzhConstants.LengthCodes[symbol - MsLzhConstants.FirstLengthSymbol];
        var lenExtraVal = lenExtraBits > 0 ? (int)reader.ReadBits(lenExtraBits) : 0;
        var length = MsLzhConstants.DecodeLength(symbol, lenExtraVal);

        var distSym = distHuf.DecodeSymbol(reader);
        if (distSym is < 0 or >= MsLzhConstants.DistanceAlphabetSize)
          throw new InvalidDataException($"MS LZH: invalid distance symbol {distSym}.");
        var (_, distExtraBits) = MsLzhConstants.DistanceCodes[distSym];
        var distExtraVal = distExtraBits > 0 ? (int)reader.ReadBits(distExtraBits) : 0;
        var distance = MsLzhConstants.DecodeDistance(distSym, distExtraVal);

        if (distance < 1 || distance > pos)
          throw new InvalidDataException($"MS LZH: invalid distance {distance} at pos {pos}.");
        if (pos + length > originalSize)
          throw new InvalidDataException("MS LZH: match would overrun output.");

        var srcPos = pos - distance;
        for (var j = 0; j < length; j++)
          output[pos + j] = output[srcPos + j];
        pos += length;
      }

      if (safety <= 0)
        throw new InvalidDataException("MS LZH: decoder safety counter exhausted.");
    }

    if (pos != originalSize)
      throw new InvalidDataException($"MS LZH: output underrun (pos={pos}, expected={originalSize}).");

    return output;
  }
}
