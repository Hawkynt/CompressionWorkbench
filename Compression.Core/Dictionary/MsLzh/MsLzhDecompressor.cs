using System.Buffers.Binary;
using Compression.Core.BitIO;

namespace Compression.Core.Dictionary.MsLzh;

/// <summary>
/// MS LZH decompressor — reads back the bit stream produced by
/// <see cref="MsLzhCompressor"/>. Reads a 4-byte little-endian original-size
/// header followed by the MSB-first canonical-Huffman literal/length and
/// distance code stream, with the end-of-block marker (symbol 256) closing
/// the stream.
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
    var safety = originalSize * 8 + 1024;

    while (pos < originalSize && safety-- > 0) {
      var symbol = MsLzhFixedTables.LitLen.DecodeSymbol(reader);
      if (symbol < 256) {
        output[pos++] = (byte)symbol;
        continue;
      }
      if (symbol == MsLzhConstants.EndOfBlockSymbol) {
        break;
      }
      if (symbol > 285)
        throw new InvalidDataException($"MS LZH: invalid literal/length symbol {symbol}.");

      // Match: decode length then distance.
      var (_, lenExtraBits) = MsLzhConstants.LengthCodes[symbol - MsLzhConstants.FirstLengthSymbol];
      var lenExtraVal = lenExtraBits > 0 ? (int)reader.ReadBits(lenExtraBits) : 0;
      var length = MsLzhConstants.DecodeLength(symbol, lenExtraVal);

      var distSym = MsLzhFixedTables.Distance.DecodeSymbol(reader);
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

    if (pos != originalSize)
      throw new InvalidDataException($"MS LZH: output underrun (pos={pos}, expected={originalSize}).");

    return output;
  }
}
