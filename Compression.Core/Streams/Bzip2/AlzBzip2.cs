using Compression.Core.BitIO;

namespace FileFormat.Bzip2;

/// <summary>
/// ALZip's bzip2 variant (ALZ compression method 1): no "BZh" stream header, each block introduced by
/// the 32-bit marker "DLZ\x01" instead of the 48-bit block magic, no block CRC and no randomised bit
/// (the block starts at its BWT origin pointer), and the stream ended by "DLZ\x02" without a combined
/// CRC. The block payload is standard bzip2 (900k blocks). Layout as documented by alkegi/unalz
/// (MIT) and confirmed against its CC0 test archives; integrity rests on the ALZ entry's CRC-32.
/// </summary>
public static class AlzBzip2 {
  private const uint BlockMarker = 0x444C5A01; // "DLZ\x01" read MSB first
  private const uint EndMarker = 0x444C5A02;   // "DLZ\x02"

  /// <summary>Decodes an ALZ bzip2 stream; stops at the end marker or once <paramref name="expectedSize"/> bytes are out.</summary>
  public static byte[] Decompress(ReadOnlySpan<byte> data, long expectedSize) {
    if (expectedSize > int.MaxValue)
      throw new NotSupportedException("ALZ members larger than 2 GiB are not supported.");
    using var input = new MemoryStream(data.ToArray(), writable: false);
    var bits = new BitBuffer<MsbBitOrder>(input);
    using var output = new MemoryStream((int)expectedSize);
    try {
      while (output.Length < expectedSize) {
        var marker = (bits.ReadBits(16) << 16) | bits.ReadBits(16);
        if (marker == EndMarker)
          break;
        if (marker != BlockMarker)
          throw new InvalidDataException($"ALZ bzip2: expected a DLZ block marker, found 0x{marker:X8}.");
        output.Write(Bzip2Decompressor.DecodeBlockBody(bits));
      }
    } catch (EndOfStreamException e) {
      throw new InvalidDataException("Truncated ALZ bzip2 stream.", e);
    }
    if (output.Length != expectedSize)
      throw new InvalidDataException($"ALZ bzip2 produced {output.Length} bytes, the entry declares {expectedSize}.");
    return output.ToArray();
  }
}
