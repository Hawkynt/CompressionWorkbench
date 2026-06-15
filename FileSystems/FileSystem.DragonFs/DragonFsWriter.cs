#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.DragonFs;

/// <summary>
/// Builds a fresh, read-only DragonFS image (Libdragon / Nintendo 64) from a
/// flat set of input files. The produced image round-trips through
/// <see cref="DragonFsReader"/>.
///
/// Layout produced (big-endian throughout):
///   0x000..0x007  "DragonFS" ASCII tag (enables self-detection)
///   0x008..0x107  zero padding
///   0x108         start of the root directory's child chain (DFS_ROOT_OFFSET = 8 + 256 = 264)
///
/// Each child is a 32-byte directory record immediately followed by that
/// file's raw bytes:
///   0x00 u32  next_entry_offset (absolute byte offset of the next record; 0 = last)
///   0x04 u32  flags (0 = regular file)
///   0x08 char[20] name (NUL-terminated ASCII; DragonDOS-style 8.3 leaf names)
///   0x1C u32  file_size
///
/// File data follows the record at record_offset + 32; the next record begins
/// at record_offset + 32 + file_size (no inter-file padding is required by the
/// reader, but each record's start is what the previous record's
/// next_entry_offset points at).
/// </summary>
public sealed class DragonFsWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Byte offset of the first directory record (the root chain head).</summary>
  public const int RootChainOffset = 8 + DragonFsReader.DefaultRootOffset; // 264

  /// <summary>Size of one directory record in bytes.</summary>
  public const int EntryRecordSize = 32;

  /// <summary>Maximum name length stored in a directory record (NUL-terminated within 20 bytes).</summary>
  public const int MaxNameLength = 19;

  /// <summary>
  /// Adds a file to the image. Names are flattened to an 8.3-style leaf and
  /// truncated to fit the record's 20-byte (NUL-terminated) name field.
  /// </summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var leaf = NormalizeName(name);
    if (leaf.Length == 0)
      throw new ArgumentException("DragonFs: file name resolves to an empty leaf name.", nameof(name));

    _files.Add((leaf, data));
  }

  /// <summary>Emits the complete DragonFS image into <paramref name="output"/>.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    var image = Build();
    output.Write(image, 0, image.Length);
  }

  /// <summary>Builds the complete DragonFS image as a byte array.</summary>
  public byte[] Build() {
    // Total size = header (264) + sum over files of (32-byte record + data).
    var total = RootChainOffset;
    foreach (var (_, data) in _files)
      total += EntryRecordSize + data.Length;

    // Guarantee the reader's minimum image size even when there are no files.
    var minSize = RootChainOffset + EntryRecordSize;
    if (total < minSize) total = minSize;

    var image = new byte[total];
    DragonFsReader.OptionalTag.CopyTo(image.AsSpan(0));

    if (_files.Count == 0) {
      // Single end-of-directory record so the reader sees a valid empty root.
      var span = image.AsSpan(RootChainOffset, EntryRecordSize);
      BinaryPrimitives.WriteUInt32BigEndian(span[0..4], 0);      // next = 0 (last)
      BinaryPrimitives.WriteUInt32BigEndian(span[4..8], 0x0002); // end-of-directory marker
      return image;
    }

    var offset = RootChainOffset;
    for (var i = 0; i < _files.Count; ++i) {
      var (name, data) = _files[i];
      var recordSpan = image.AsSpan(offset, EntryRecordSize);

      var dataOffset = offset + EntryRecordSize;
      var isLast = i == _files.Count - 1;
      var nextOffset = isLast ? 0 : dataOffset + data.Length;

      BinaryPrimitives.WriteUInt32BigEndian(recordSpan[0..4], (uint)nextOffset); // next_entry_offset
      BinaryPrimitives.WriteUInt32BigEndian(recordSpan[4..8], 0);                // flags = regular file
      WriteName(recordSpan.Slice(8, 20), name);
      BinaryPrimitives.WriteUInt32BigEndian(recordSpan[28..32], (uint)data.Length); // file_size

      data.CopyTo(image.AsSpan(dataOffset));
      offset = dataOffset + data.Length;
    }

    return image;
  }

  // Flatten to a leaf name and clamp to the on-disk name capacity.
  private static string NormalizeName(string name) {
    var leaf = Path.GetFileName(name.Replace('\\', '/'));
    if (leaf.Length > MaxNameLength)
      leaf = leaf[..MaxNameLength];
    return leaf;
  }

  private static void WriteName(Span<byte> field, string name) {
    field.Clear();
    var bytes = Encoding.ASCII.GetBytes(name);
    var count = Math.Min(bytes.Length, MaxNameLength); // leave at least one NUL terminator
    bytes.AsSpan(0, count).CopyTo(field);
  }
}
