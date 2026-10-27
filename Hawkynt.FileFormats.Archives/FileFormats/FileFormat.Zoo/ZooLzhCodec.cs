using Compression.Core.Dictionary.Lzh;

namespace FileFormat.Zoo;

/// <summary>
/// Zoo packing method 2. Zoo 2.10 took its LZH coder from Haruhiko Okumura's "ar": the same
/// static-Huffman block layout as LHA's -lh5- with an 8 KiB window, read MSB first. Unlike LHA,
/// the stream is self-terminating: after the last block the encoder pads with zero bits and writes
/// a 16-bit block size of 0, and Zoo's decoder stops on that.
/// </summary>
internal static class ZooLzhCodec {
  /// <summary>Decodes a method-2 member to its recorded original size.</summary>
  public static byte[] Decode(byte[] compressed, uint originalSize) {
    if (originalSize > int.MaxValue)
      throw new NotSupportedException("Zoo member exceeds the managed decoder's 2 GiB output limit.");
    if (originalSize == 0)
      return [];
    using var ms = new MemoryStream(compressed, writable: false);
    return new LzhDecoder(ms, ZooConstants.LzhDictionaryBits).Decode((int)originalSize);
  }

  /// <summary>
  /// Encodes data as method 2. The block stream ends byte-aligned with zero padding, then two
  /// zero bytes follow: wherever inside that padding the decoder's next 16-bit read starts, it
  /// reads a block size of 0, which is Zoo's end-of-stream marker.
  /// </summary>
  public static byte[] Encode(byte[] data) {
    var blocks = data.Length == 0 ? [] : new LzhEncoder(ZooConstants.LzhDictionaryBits).Encode(data);
    var result = new byte[blocks.Length + 2];
    blocks.CopyTo(result, 0);
    return result;
  }
}
