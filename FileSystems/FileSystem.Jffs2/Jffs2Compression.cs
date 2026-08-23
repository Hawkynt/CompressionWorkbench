#pragma warning disable CS1591
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lzo;

namespace FileSystem.Jffs2;

/// <summary>
/// The compressors JFFS2 stores a data node with, and how to get the bytes back.
/// </summary>
/// <remarks>
/// <para>Each data node says what compressed it, and a reader that only handles
/// one of them is a reader that only handles the files nobody bothered to
/// compress. This project's own writer stores everything uncompressed, so a
/// round trip through it never touched any of this — while mkfs.jffs2 compresses
/// almost everything it can, and its images came back with their files empty.
/// </para>
///
/// <para>A node that cannot be decompressed raises. It used to be dropped
/// quietly, which turned "this reader does not know that compressor" into "the
/// file is empty".</para>
/// </remarks>
internal static class Jffs2Compression {

  internal const byte None = 0x00;

  /// <summary>The data is all zeros and no payload was stored for it.</summary>
  private const byte Zero = 0x01;

  private const byte Rtime = 0x02;
  private const byte Copy = 0x04;
  private const byte Zlib = 0x06;
  private const byte Lzo = 0x07;

  internal static byte[] Decompress(byte[] data, byte compression, uint decompressedSize) {
    switch (compression) {
      case None:
      case Copy:
        return data;

      case Zero:
        return new byte[decompressedSize];

      case Zlib: {
        // A JFFS2 zlib node holds an ordinary zlib stream: two bytes of header,
        // the deflate data, then an adler32 nobody here needs.
        if (data.Length >= 2 && (data[0] & 0x0F) == 8 && (data[0] * 256 + data[1]) % 31 == 0) {
          var end = data.Length >= 6 ? data.Length - 4 : data.Length;
          return DeflateDecompressor.Decompress(data.AsSpan(2, end - 2));
        }

        return DeflateDecompressor.Decompress(data);
      }

      case Lzo:
        return Lzo1xDecompressor.Decompress(data, (int)decompressedSize);

      case Rtime:
        return DecompressRtime(data, (int)decompressedSize);

      default:
        throw new NotSupportedException(
          $"JFFS2: a data node compressed with method {compression} cannot be read here. "
          + "Returning nothing for it would report the file as empty rather than as unread.");
    }
  }

  /// <summary>
  /// JFFS2's own "rtime" scheme: each byte is followed by a repeat count saying
  /// how much of what came before to copy again.
  /// </summary>
  /// <remarks>
  /// The last position of every byte value is remembered, so a repeat says "and
  /// then the run that followed the previous one of these". The copy is done a
  /// byte at a time because the run may reach into what it is writing, which is
  /// how the scheme expresses a repeat longer than the distance behind it.
  /// </remarks>
  private static byte[] DecompressRtime(byte[] data, int decompressedSize) {
    var output = new byte[decompressedSize];
    var positions = new int[256];
    var outPos = 0;
    var inPos = 0;

    while (outPos < decompressedSize) {
      if (inPos + 2 > data.Length)
        throw new InvalidDataException("JFFS2: an rtime node ends in the middle of a pair.");

      var value = data[inPos++];
      var repeat = data[inPos++];
      output[outPos++] = value;

      var from = positions[value];
      positions[value] = outPos;

      for (var i = 0; i < repeat && outPos < decompressedSize; ++i)
        output[outPos++] = output[from++];
    }

    return output;
  }
}
