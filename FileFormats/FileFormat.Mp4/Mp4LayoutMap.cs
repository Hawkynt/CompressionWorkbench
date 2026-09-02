#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Mp4;

/// <summary>
/// Walks the top-level atoms of an MP4/MOV file and exposes each as a
/// <see cref="DefragBlockInfo"/> for block-chart visualization.
/// </summary>
public sealed class Mp4LayoutMap : IFileInternalLayoutMap {

  /// <inheritdoc />
    /// <summary>
  /// Enumerates the chunks.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateChunks(Stream file) {
    ArgumentNullException.ThrowIfNull(file);
    file.Position = 0;
    var length = file.Length;
    if (length < 8)
      yield break;

    var header = new byte[16];
    var pos = 0L;

    while (pos + 8 <= length) {
      file.Position = pos;
      var read = file.Read(header, 0, Math.Min(16, (int)Math.Min(16, length - pos)));
      if (read < 8) break;

      var size = (long)BinaryPrimitives.ReadUInt32BigEndian(header);
      var type = Encoding.ASCII.GetString(header, 4, 4);
      var hdr = 8L;

      if (size == 1) {
        if (read < 16) break;
        size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
        hdr = 16;
      } else if (size == 0) {
        size = length - pos; // extends to EOF
      }

      if (size < hdr || pos + size > length) break;

      yield return Classify(type, pos, size);
      pos += size;
    }
  }

  /// <summary>
  /// Classifies an atom by its four-character type into the appropriate
  /// <see cref="DefragBlockKind"/> and <see cref="DefragBlockClass"/>.
  /// </summary>
  private static DefragBlockInfo Classify(string type, long offset, long size) {
    switch (type) {
      case "ftyp":
        return new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, "ftyp (File type)", DefragBlockClass.Normal);

      case "moov":
        // moov at front is good (Hot); at back means not fast-start (Frozen).
        // Caller doesn't know total layout yet, so we classify based on offset.
        // Offset 0 or very near the start → Hot; otherwise → Frozen.
        var cls = offset < 1024 * 1024 ? DefragBlockClass.Hot : DefragBlockClass.Frozen;
        return new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, "moov (Movie metadata)", cls);

      case "mdat":
        return new DefragBlockInfo(offset, size, DefragBlockKind.Used, "mdat (Media data)", DefragBlockClass.Normal);

      case "free":
      case "skip":
        return new DefragBlockInfo(offset, size, DefragBlockKind.Free, $"{type} (Padding)");

      default:
        return new DefragBlockInfo(offset, size, DefragBlockKind.MetadataReserved, $"{type}", DefragBlockClass.Normal);
    }
  }
}
