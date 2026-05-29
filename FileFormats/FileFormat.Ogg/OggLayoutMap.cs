#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Ogg;

/// <summary>
/// Walks an OGG bitstream at the page level and emits <see cref="DefragBlockInfo"/>
/// tiles for block-chart visualization. Codec header pages (first page of each
/// logical stream) are classified as MetadataReserved; subsequent audio data pages
/// are classified as Used.
/// </summary>
public static class OggLayoutMap {

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream file) {
    ArgumentNullException.ThrowIfNull(file);
    if (file.Length < 27)
      yield break;

    file.Position = 0;
    var header = new byte[27];
    var seenBos = new HashSet<uint>();

    while (file.Position + 27 <= file.Length) {
      var pageStart = file.Position;

      if (file.Read(header, 0, 27) < 27)
        break;

      // Validate OggS magic
      if (header[0] != 'O' || header[1] != 'g' || header[2] != 'g' || header[3] != 'S')
        break;

      var flags = header[5];
      var serial = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14));
      var segTableLen = header[26];

      if (file.Position + segTableLen > file.Length)
        break;

      var segTable = new byte[segTableLen];
      if (file.Read(segTable, 0, segTableLen) < segTableLen)
        break;

      var totalPayload = 0;
      foreach (var s in segTable) totalPayload += s;

      var pageSize = 27L + segTableLen + totalPayload;
      if (pageStart + pageSize > file.Length)
        break;

      // BOS flag (bit 1) indicates a beginning-of-stream page = codec header
      var isBos = (flags & 0x02) != 0;
      if (isBos)
        seenBos.Add(serial);

      // First few pages of each stream are codec headers (BOS page and typically
      // the next 1-2 pages carry header/comment packets). We classify BOS pages
      // as metadata. For simplicity, non-BOS pages are audio data.
      if (isBos) {
        yield return new DefragBlockInfo(pageStart, pageSize,
          DefragBlockKind.MetadataReserved,
          $"OGG header page (stream 0x{serial:X8})",
          DefragBlockClass.Hot);
      } else {
        yield return new DefragBlockInfo(pageStart, pageSize,
          DefragBlockKind.Used,
          $"OGG data page (stream 0x{serial:X8})",
          DefragBlockClass.Normal);
      }

      file.Position = pageStart + pageSize;
    }
  }
}
