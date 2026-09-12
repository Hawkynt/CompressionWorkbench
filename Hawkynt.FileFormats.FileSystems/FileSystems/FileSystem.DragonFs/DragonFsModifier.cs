#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.DragonFs;

/// <summary>
/// In-place modifier for DragonFS (Libdragon / Nintendo 64) images. The
/// filesystem is a singly-linked list of 32-byte directory records, each
/// immediately followed by that file's inline data, with absolute
/// <c>next_entry_offset</c> pointers. This lets us mutate without re-packing:
///
/// <list type="bullet">
///   <item><b>Add</b>: append the new record + its data at the end of the image
///         and patch the previous tail record's <c>next_entry_offset</c> to point
///         at it. Existing files' data bytes stay byte-identical at their original
///         offsets; only the predecessor's link word (4 bytes) and the appended
///         tail bytes change. I/O = chain walk (one 32-byte record read per entry)
///         + one tail record rewrite + the appended record/data.</item>
///   <item><b>Remove</b>: blank the record's name field (the reader skips blank
///         records but still follows the link), optionally wiping the inline data.
///         No other byte moves.</item>
/// </list>
///
/// <para>Record layout (big-endian): next_offset u32 @0, flags u32 @4
/// (0x0001 = dir, 0x0002 = end-of-directory), name[20] @8 (NUL-terminated),
/// size u32 @28. File data starts at record_offset + 32.</para>
/// </summary>
public static class DragonFsModifier {
  private const int EntryRecordSize = 32;
  private const int MaxNameLength = 19;
  private const uint FlagEndOfDir = 0x0002;
  private const uint FlagDirectory = 0x0001;

  /// <summary>True if the stream is large enough to hold a DragonFS root entry.</summary>
  public static bool IsDragonFs(Stream image) =>
    image.Length >= DragonFsReader.DefaultRootOffset + EntryRecordSize;

  private static int RootOffset(Stream image) {
    if (image.Length >= 8) {
      var tag = new byte[8];
      image.Position = 0;
      image.ReadExactly(tag);
      if (tag.AsSpan().SequenceEqual(DragonFsReader.OptionalTag))
        return 8 + DragonFsReader.DefaultRootOffset;
    }
    return DragonFsReader.DefaultRootOffset;
  }

  /// <summary>
  /// Adds a file by appending a record + data at the image tail and relinking
  /// the chain. The image grows by (32 + data.Length) bytes at the end; nothing
  /// before the old end moves.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var leaf = NormalizeName(name);
    if (leaf.Length == 0) throw new ArgumentException("DragonFs: empty leaf name.", nameof(name));

    var root = RootOffset(image);
    var (tailOffset, tailIsEndMarker) = FindTail(image, root);

    if (tailIsEndMarker) {
      // Overwrite the end-marker record in place as the new (and only) record,
      // appending the data right after it.
      var rec = BuildRecord(nextOffset: 0, flags: 0, leaf, data.Length);
      image.Position = tailOffset;
      image.Write(rec, 0, rec.Length);
      if (data.Length > 0) image.Write(data, 0, data.Length);
      image.SetLength(tailOffset + EntryRecordSize + data.Length);
      return;
    }

    // Append a fresh record + data at the current end of the image.
    var newOffset = (int)image.Length;
    var newRec = BuildRecord(nextOffset: 0, flags: 0, leaf, data.Length);
    image.Position = newOffset;
    image.Write(newRec, 0, newRec.Length);
    if (data.Length > 0) image.Write(data, 0, data.Length);

    // Relink the old tail record's next pointer (4 bytes) to the new record.
    var link = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(link, (uint)newOffset);
    image.Position = tailOffset;
    image.Write(link, 0, 4);
  }

  /// <summary>
  /// Removes the named file in place by blanking its directory record's name
  /// field (the reader skips blank-name records while still following the
  /// chain). Optionally wipes the inline data. Returns true if found.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var leaf = NormalizeName(name);
    var root = RootOffset(image);
    var visited = new HashSet<int>();
    var off = root;
    while (off > 0 && off + EntryRecordSize <= image.Length) {
      if (!visited.Add(off)) break;
      var rec = ReadRecord(image, off);
      var next = (int)BinaryPrimitives.ReadUInt32BigEndian(rec.AsSpan(0, 4));
      var flags = BinaryPrimitives.ReadUInt32BigEndian(rec.AsSpan(4, 4));
      if ((flags & FlagEndOfDir) != 0) break;
      var entryName = ReadName(rec.AsSpan(8, 20));
      var size = (int)BinaryPrimitives.ReadUInt32BigEndian(rec.AsSpan(28, 4));
      var isDir = (flags & FlagDirectory) != 0;

      if (!isDir && !string.IsNullOrEmpty(entryName) &&
          string.Equals(entryName, leaf, StringComparison.OrdinalIgnoreCase)) {
        // Blank the name field + zero the size so the reader skips this record.
        image.Position = off + 8;
        image.Write(new byte[20], 0, 20);
        image.Position = off + 28;
        image.Write(new byte[4], 0, 4);
        if (wipeData && size > 0) {
          var dataOff = off + EntryRecordSize;
          if (dataOff + size <= image.Length) {
            image.Position = dataOff;
            image.Write(new byte[size], 0, size);
          }
        }
        return true;
      }

      if (next == 0) break;
      off = next;
    }
    return false;
  }

  // ── Chain walking ───────────────────────────────────────────────────

  /// <summary>Returns the offset of the last record in the chain plus whether
  /// it is the end-of-directory marker (i.e. an empty directory).</summary>
  private static (int TailOffset, bool IsEndMarker) FindTail(Stream image, int root) {
    var visited = new HashSet<int>();
    var off = root;
    while (off > 0 && off + EntryRecordSize <= image.Length) {
      if (!visited.Add(off)) break;
      var rec = ReadRecord(image, off);
      var next = (int)BinaryPrimitives.ReadUInt32BigEndian(rec.AsSpan(0, 4));
      var flags = BinaryPrimitives.ReadUInt32BigEndian(rec.AsSpan(4, 4));
      if ((flags & FlagEndOfDir) != 0) return (off, true);
      if (next == 0) return (off, false);
      off = next;
    }
    return (root, true);
  }

  // ── Record helpers ──────────────────────────────────────────────────

  private static byte[] ReadRecord(Stream image, int offset) {
    var rec = new byte[EntryRecordSize];
    image.Position = offset;
    image.ReadExactly(rec);
    return rec;
  }

  private static byte[] BuildRecord(uint nextOffset, uint flags, string name, int size) {
    var rec = new byte[EntryRecordSize];
    BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(0, 4), nextOffset);
    BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(4, 4), flags);
    var bytes = Encoding.ASCII.GetBytes(name);
    var count = Math.Min(bytes.Length, MaxNameLength);
    bytes.AsSpan(0, count).CopyTo(rec.AsSpan(8, 20));
    BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(28, 4), (uint)size);
    return rec;
  }

  private static string ReadName(ReadOnlySpan<byte> span) {
    var end = 0;
    while (end < span.Length && span[end] != 0) end++;
    return Encoding.ASCII.GetString(span[..end]);
  }

  private static string NormalizeName(string name) {
    var leaf = Path.GetFileName((name ?? "").Replace('\\', '/'));
    if (leaf.Length > MaxNameLength) leaf = leaf[..MaxNameLength];
    return leaf;
  }
}
