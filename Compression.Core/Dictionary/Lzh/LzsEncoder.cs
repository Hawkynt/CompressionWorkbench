using Compression.Core.BitIO;

namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Encodes LArc <c>-lzs-</c> (see <see cref="LzsDecoder"/> for the stream layout).
/// </summary>
/// <remarks>
/// Matches only reach back into bytes already emitted (never into the space-filled initial ring),
/// and are written as the absolute ring position of their source, as the format requires. Verified by
/// lhasa extracting what this writes.
/// </remarks>
public static class LzsEncoder {
  private const int MaxLength = 15 + LzsDecoder.Threshold;

  /// <summary>Encodes <paramref name="data"/>.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data) => Encode(data.ToArray());

  /// <summary>Encodes <paramref name="data"/>.</summary>
  public static byte[] Encode(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    using var ms = new MemoryStream();
    var bits = new BitWriter<MsbBitOrder>(ms);
    var finder = new LhaMatchFinder(data, LzsDecoder.RingSize - 1, 3, MaxLength);
    var pos = 0;
    while (pos < data.Length) {
      var (length, distance) = finder.Find(pos);
      if (length == 0) {
        bits.WriteBit(1);
        bits.WriteBits(data[pos], 8);
        ++pos;
        continue;
      }
      var ringPos = (LzsDecoder.RingSize - LzsDecoder.StartOffset + pos - distance) % LzsDecoder.RingSize;
      bits.WriteBit(0);
      bits.WriteBits((uint)ringPos, 11);
      bits.WriteBits((uint)(length - LzsDecoder.Threshold), 4);
      pos += length;
    }
    bits.FlushBits();
    return ms.ToArray();
  }
}
