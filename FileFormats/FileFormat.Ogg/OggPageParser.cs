#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Ogg;

/// <summary>
/// Walks an Ogg bitstream at the page level (RFC 3533). Each page begins with the
/// magic <c>OggS</c>, carries a header of at least 27 bytes, a segment table, and
/// the concatenated packet segments. This parser does not reassemble continuing
/// packets across pages — consumers that need whole packets call
/// <see cref="StreamPackets(ReadOnlySpan{byte}, uint)"/>.
/// </summary>
public sealed class OggPageParser {
  public readonly record struct Page(uint Serial, byte Flags, byte[][] Segments);

  public List<Page> Pages(ReadOnlySpan<byte> data) {
    var pages = new List<Page>();
    var pos = 0;
    while (pos + 27 <= data.Length) {
      if (data[pos] != 'O' || data[pos + 1] != 'g' || data[pos + 2] != 'g' || data[pos + 3] != 'S')
        throw new InvalidDataException($"Ogg: missing page magic at offset {pos}.");
      var flags = data[pos + 5];
      var serial = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 14)..]);
      var segTableLen = data[pos + 26];
      if (pos + 27 + segTableLen > data.Length)
        throw new InvalidDataException("Ogg: page segment table out of range.");
      var lengths = data.Slice(pos + 27, segTableLen);
      var payloadStart = pos + 27 + segTableLen;
      var totalPayload = 0;
      foreach (var l in lengths) totalPayload += l;
      if (payloadStart + totalPayload > data.Length)
        throw new InvalidDataException("Ogg: page payload out of range.");

      // Each segment maps 1:1 to a byte[]; consumers that need full packets glue
      // 255-length runs until they hit a shorter segment (packet-end marker).
      var segments = new byte[segTableLen][];
      var offset = payloadStart;
      for (var i = 0; i < segTableLen; ++i) {
        segments[i] = data.Slice(offset, lengths[i]).ToArray();
        offset += lengths[i];
      }
      pages.Add(new Page(serial, flags, segments));
      pos = payloadStart + totalPayload;
    }
    return pages;
  }

  /// <summary>
  /// Yields reassembled packet blobs for a single logical bitstream
  /// (filtered by <paramref name="serial"/>).
  /// </summary>
  public IEnumerable<byte[]> StreamPackets(ReadOnlySpan<byte> data, uint serial) {
    var packets = new List<byte[]>();
    using var current = new MemoryStream();
    foreach (var page in Pages(data)) {
      if (page.Serial != serial) continue;
      foreach (var seg in page.Segments) {
        current.Write(seg);
        if (seg.Length < 255) {
          packets.Add(current.ToArray());
          current.SetLength(0);
        }
      }
    }
    if (current.Length > 0) packets.Add(current.ToArray());
    return packets;
  }
}
