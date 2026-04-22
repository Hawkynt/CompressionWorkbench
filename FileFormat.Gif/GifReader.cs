#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Gif;

/// <summary>
/// Slices a multi-frame GIF89a (or GIF87a) into per-frame standalone GIFs at the byte level
/// — no LZW decode is performed. Each emitted frame contains the original Logical Screen
/// Descriptor + Global Color Table, the frame's Graphic Control Extension (if present),
/// the Image Descriptor + Local Color Table + LZW data, and a Trailer byte.
/// </summary>
public sealed class GifReader {
  /// <summary>One sliced frame ready to write as a standalone GIF.</summary>
  public sealed record Frame(int Index, byte[] Data);

  private const byte BlockExtension = 0x21;
  private const byte BlockImageDescriptor = 0x2C;
  private const byte BlockTrailer = 0x3B;
  private const byte ExtGraphicControl = 0xF9;

  public List<Frame> Read(ReadOnlySpan<byte> data) {
    if (data.Length < 13)
      throw new InvalidDataException("GIF too short for header.");
    if (data[0] != 'G' || data[1] != 'I' || data[2] != 'F')
      throw new InvalidDataException("Missing GIF magic.");

    // Header (6) + Logical Screen Descriptor (7) + optional Global Color Table.
    var packed = data[10];
    var globalCtSize = (packed & 0x80) != 0 ? 3 * (1 << ((packed & 0x07) + 1)) : 0;
    var headerEnd = 13 + globalCtSize;
    if (headerEnd > data.Length)
      throw new InvalidDataException("Truncated GIF global color table.");
    var headerBlob = data[..headerEnd].ToArray();

    var frames = new List<Frame>();
    var pos = headerEnd;
    var pendingGce = ReadOnlySpan<byte>.Empty;
    while (pos < data.Length) {
      var marker = data[pos];
      if (marker == BlockTrailer) break;

      if (marker == BlockExtension) {
        var extStart = pos;
        if (pos + 2 > data.Length)
          throw new InvalidDataException("Truncated GIF extension introducer.");
        var label = data[pos + 1];
        pos += 2;
        var subStart = pos;
        SkipSubBlocks(data, ref pos);
        var ext = data[extStart..pos];

        if (label == ExtGraphicControl) {
          // Latch the GCE so it precedes the next Image Descriptor in the sliced output.
          pendingGce = ext;
        }
        // Other extensions (Application/Comment/PlainText) are dropped from per-frame
        // output — they apply to the whole stream, not to one frame.
        continue;
      }

      if (marker == BlockImageDescriptor) {
        var idStart = pos;
        if (pos + 10 > data.Length)
          throw new InvalidDataException("Truncated Image Descriptor.");
        var idPacked = data[pos + 9];
        var hasLocalCt = (idPacked & 0x80) != 0;
        var localCtSize = hasLocalCt ? 3 * (1 << ((idPacked & 0x07) + 1)) : 0;
        pos += 10 + localCtSize;
        // LZW minimum-code-size byte then sub-blocks.
        if (pos >= data.Length)
          throw new InvalidDataException("Truncated Image data start.");
        ++pos; // LZW min code size
        SkipSubBlocks(data, ref pos);
        var imageBlob = data[idStart..pos];

        var frameSize = headerBlob.Length + pendingGce.Length + imageBlob.Length + 1;
        var frameOut = new byte[frameSize];
        var w = 0;
        headerBlob.CopyTo(frameOut.AsSpan(w)); w += headerBlob.Length;
        pendingGce.CopyTo(frameOut.AsSpan(w)); w += pendingGce.Length;
        imageBlob.CopyTo(frameOut.AsSpan(w)); w += imageBlob.Length;
        frameOut[w] = BlockTrailer;

        frames.Add(new Frame(frames.Count, frameOut));
        pendingGce = ReadOnlySpan<byte>.Empty;
        continue;
      }

      // Unknown marker — skip one byte and try to recover. Real-world malformed GIFs
      // sometimes have stray bytes; abort cleanly rather than infinite-loop.
      throw new InvalidDataException($"Unknown GIF block marker 0x{marker:X2} at offset {pos}.");
    }

    return frames;
  }

  /// <summary>Walks a chain of GIF sub-blocks (length byte then that many bytes), terminating on a 0-length block.</summary>
  private static void SkipSubBlocks(ReadOnlySpan<byte> data, ref int pos) {
    while (pos < data.Length) {
      var len = data[pos++];
      if (len == 0) return;
      if (pos + len > data.Length)
        throw new InvalidDataException("Truncated GIF sub-block.");
      pos += len;
    }
    throw new InvalidDataException("Unterminated GIF sub-block chain.");
  }
}
