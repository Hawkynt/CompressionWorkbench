using System.Buffers.Binary;

namespace FileFormat.OneNote;

internal readonly record struct OneNoteFileNodeListFragmentInfo(uint FileNodeListId, uint Sequence, int FileNodeCount, bool HasNextFragment);

/// <summary>Checks the first file-node-list fragment and walks its bounded FileNode headers.</summary>
internal static class OneNoteFileNodeListFragment {
  private const ulong HeaderMagic = 0xA4567AB1F5F7F4C4;
  private const ulong FooterMagic = 0x8BC215C38233BA4B;
  private const int FragmentHeaderSize = 16;
  private const int NextFragmentSize = 12;
  private const int FooterSize = 8;
  private const int FragmentOverhead = FragmentHeaderSize + NextFragmentSize + FooterSize;

  internal static bool TryRead(Stream stream, ulong offset, uint length, out OneNoteFileNodeListFragmentInfo info) {
    ArgumentNullException.ThrowIfNull(stream);
    info = default;
    if (!stream.CanSeek || length < FragmentOverhead || offset > (ulong)stream.Length || length > (ulong)stream.Length - offset)
      return false;

    var origin = stream.Position;
    try {
      Span<byte> header = stackalloc byte[FragmentHeaderSize];
      stream.Position = (long)offset;
      if (!ReadExactly(stream, header) || BinaryPrimitives.ReadUInt64LittleEndian(header) != HeaderMagic)
        return false;
      var listId = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
      var sequence = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
      if (listId < 0x10 || sequence != 0)
        return false;

      Span<byte> footer = stackalloc byte[FooterSize];
      stream.Position = (long)(offset + length - FooterSize);
      if (!ReadExactly(stream, footer) || BinaryPrimitives.ReadUInt64LittleEndian(footer) != FooterMagic)
        return false;

      var nodeEnd = offset + length - NextFragmentSize - FooterSize;
      var position = offset + FragmentHeaderSize;
      var nodeCount = 0;
      var hasTerminator = false;
      Span<byte> rawHeader = stackalloc byte[sizeof(uint)];
      while (nodeEnd - position >= sizeof(uint)) {
        stream.Position = (long)position;
        if (!ReadExactly(stream, rawHeader)) return false;
        var bits = BinaryPrimitives.ReadUInt32LittleEndian(rawHeader);
        var id = bits & 0x3FF;
        var nodeSize = (bits >> 10) & 0x1FFF;
        var baseType = (bits >> 27) & 0xF;
        var reserved = bits >> 31;

        // Padding is undefined. Once at least one complete node was read, an impossible
        // next header marks its beginning; an impossible first header is malformed.
        if (nodeSize == 0 || nodeSize < sizeof(uint) || reserved != 1 || baseType > 2) {
          if (nodeCount == 0) return false;
          break;
        }
        if (nodeSize > nodeEnd - position) return false;
        ++nodeCount;
        position += nodeSize;
        if (id == 0x0FF) {
          if (nodeSize != sizeof(uint)) return false;
          hasTerminator = true;
          break;
        }
      }

      Span<byte> next = stackalloc byte[NextFragmentSize];
      stream.Position = (long)nodeEnd;
      if (!ReadExactly(stream, next)) return false;
      var nextOffset = BinaryPrimitives.ReadUInt64LittleEndian(next);
      var nextLength = BinaryPrimitives.ReadUInt32LittleEndian(next[8..]);
      var hasNext = !(nextOffset == ulong.MaxValue && nextLength == 0);
      if (hasTerminator && !hasNext) return false;
      if (hasNext && (nextLength < FragmentOverhead || nextOffset > (ulong)stream.Length || nextLength > (ulong)stream.Length - nextOffset))
        return false;

      info = new OneNoteFileNodeListFragmentInfo(listId, sequence, nodeCount, hasNext);
      return nodeCount > 0;
    } finally {
      stream.Position = origin;
    }
  }

  private static bool ReadExactly(Stream stream, Span<byte> buffer) {
    var read = 0;
    while (read < buffer.Length) {
      var count = stream.Read(buffer[read..]);
      if (count <= 0) return false;
      read += count;
    }
    return true;
  }
}
