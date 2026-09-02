using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.Wad;

/// <summary>
/// Random-access editor for canonical Doom WAD images whose directory is the
/// final region of the file. Existing lump payloads stay at their original
/// offsets; changed payloads replace the old directory, then a fresh directory
/// is appended and the 12-byte header is patched.
/// </summary>
internal static class WadInPlaceModifier {
  private const int HeaderSize = 12;
  private const int DirectoryEntrySize = 16;
  private const int NameLength = 8;
  private const int IoBufferSize = 64 * 1024;

  private sealed record Entry(string Name, int DataOffset, int Size) {
    public long End => checked((long)this.DataOffset + this.Size);
  }

  private sealed record Layout(bool IsIwad, int DirectoryOffset, IReadOnlyList<Entry> Entries);

  private sealed record PendingEntry(string Name, byte[] Data, int DataOffset);

  /// <summary>
  /// Adds or replaces lumps with O(directory bytes + changed payload bytes) I/O.
  /// Untouched payloads are not read, copied, or recompressed.
  /// </summary>
  public static void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ValidateStream(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    var layout = ReadLayout(archive);
    var additions = new List<(string Name, byte[] Data)>();
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var input in inputs) {
      if (input.IsDirectory)
        continue;
      var name = NormalizeName(Path.GetFileName(input.ArchiveName));
      if (!names.Add(name))
        throw new ArgumentException($"WAD edit contains duplicate normalized lump name '{name}'.", nameof(inputs));
      additions.Add((name, input.ReadContent()));
    }
    if (additions.Count == 0)
      return;

    var replacedNames = new HashSet<string>(additions.Select(item => item.Name), StringComparer.OrdinalIgnoreCase);
    var replaced = layout.Entries.Where(entry => replacedNames.Contains(entry.Name)).ToArray();
    var survivors = layout.Entries.Where(entry => !replacedNames.Contains(entry.Name)).ToArray();
    EnsureWipeSafe(replaced, survivors);

    var pending = new List<PendingEntry>(additions.Count);
    var cursor = (long)layout.DirectoryOffset;
    foreach (var (name, data) in additions) {
      if (cursor > int.MaxValue)
        throw new NotSupportedException("WAD edit would exceed the 32-bit lump offset limit.");
      pending.Add(new PendingEntry(name, data, checked((int)cursor)));
      cursor = checked(cursor + data.LongLength);
    }
    if (cursor > int.MaxValue)
      throw new NotSupportedException("WAD edit would exceed the 32-bit directory offset limit.");

    var newDirectoryOffset = checked((int)cursor);
    var directory = BuildDirectory(survivors, pending);
    var finalLength = checked((long)newDirectoryOffset + directory.Length);

    // Everything that can fail structurally is complete before the first write.
    archive.Position = layout.DirectoryOffset;
    foreach (var entry in pending)
      archive.Write(entry.Data);
    archive.Write(directory);
    archive.SetLength(finalLength);
    PatchHeader(archive, survivors.Length + pending.Count, newDirectoryOffset);

    foreach (var entry in replaced)
      ZeroRange(archive, entry.DataOffset, entry.Size);

    archive.Flush();
  }

  /// <summary>
  /// Removes named lumps by rewriting only the trailing directory and wiping the
  /// removed payload ranges. Surviving payload offsets remain unchanged.
  /// </summary>
  public static void Remove(Stream archive, IReadOnlyCollection<string> entryNames) {
    ValidateStream(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    if (entryNames.Count == 0)
      return;

    var layout = ReadLayout(archive);
    var requested = new HashSet<string>(
      entryNames.Where(name => name != null).Select(name => NormalizeName(Path.GetFileName(name))),
      StringComparer.OrdinalIgnoreCase);
    if (requested.Count == 0)
      return;

    var removed = layout.Entries.Where(entry => requested.Contains(entry.Name)).ToArray();
    if (removed.Length == 0)
      return;
    var survivors = layout.Entries.Where(entry => !requested.Contains(entry.Name)).ToArray();
    EnsureWipeSafe(removed, survivors);

    var directory = BuildDirectory(survivors, []);
    var finalLength = checked((long)layout.DirectoryOffset + directory.Length);

    archive.Position = layout.DirectoryOffset;
    archive.Write(directory);
    archive.SetLength(finalLength);
    PatchHeader(archive, survivors.Length, layout.DirectoryOffset);

    foreach (var entry in removed)
      ZeroRange(archive, entry.DataOffset, entry.Size);

    archive.Flush();
  }

  private static Layout ReadLayout(Stream archive) {
    Span<byte> header = stackalloc byte[HeaderSize];
    archive.Position = 0;
    archive.ReadExactly(header);

    var magic = Encoding.ASCII.GetString(header[..4]);
    var isIwad = string.Equals(magic, WadConstants.MagicIwadString, StringComparison.Ordinal);
    var isPwad = string.Equals(magic, WadConstants.MagicPwadString, StringComparison.Ordinal);
    if (!isIwad && !isPwad)
      throw new InvalidDataException($"Invalid WAD magic: {magic}");

    var count = BinaryPrimitives.ReadInt32LittleEndian(header[4..8]);
    var directoryOffset = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
    if (count < 0 || directoryOffset < HeaderSize)
      throw new InvalidDataException("WAD header contains a negative count or invalid directory offset.");

    var directoryLength = checked((long)count * DirectoryEntrySize);
    if ((long)directoryOffset + directoryLength != archive.Length)
      throw new NotSupportedException(
        "Changed-byte WAD editing requires the directory to be the exact trailing region of the archive.");

    var entries = new List<Entry>(count);
    archive.Position = directoryOffset;
    Span<byte> raw = stackalloc byte[DirectoryEntrySize];
    for (var i = 0; i < count; ++i) {
      archive.ReadExactly(raw);
      var dataOffset = BinaryPrimitives.ReadInt32LittleEndian(raw[..4]);
      var size = BinaryPrimitives.ReadInt32LittleEndian(raw[4..8]);
      if (dataOffset < 0 || size < 0)
        throw new InvalidDataException("WAD directory contains a negative lump offset or size.");
      if (size > 0 && (dataOffset < HeaderSize || checked((long)dataOffset + size) > directoryOffset))
        throw new NotSupportedException("WAD lump data overlaps the trailing directory or lies outside the payload region.");
      entries.Add(new Entry(ReadName(raw[8..16]), dataOffset, size));
    }

    return new Layout(isIwad, directoryOffset, entries);
  }

  private static byte[] BuildDirectory(
      IReadOnlyList<Entry> survivors,
      IReadOnlyList<PendingEntry> additions) {
    var total = checked((survivors.Count + additions.Count) * DirectoryEntrySize);
    var result = new byte[total];
    var offset = 0;

    foreach (var entry in survivors) {
      WriteDirectoryEntry(result.AsSpan(offset, DirectoryEntrySize), entry.DataOffset, entry.Size, entry.Name);
      offset += DirectoryEntrySize;
    }
    foreach (var entry in additions) {
      WriteDirectoryEntry(result.AsSpan(offset, DirectoryEntrySize), entry.DataOffset, entry.Data.Length, entry.Name);
      offset += DirectoryEntrySize;
    }

    return result;
  }

  private static void EnsureWipeSafe(IReadOnlyList<Entry> removed, IReadOnlyList<Entry> survivors) {
    foreach (var target in removed) {
      if (target.Size == 0)
        continue;
      foreach (var survivor in survivors) {
        if (survivor.Size == 0)
          continue;
        if (target.DataOffset < survivor.End && survivor.DataOffset < target.End)
          throw new NotSupportedException(
            $"WAD lump '{target.Name}' shares payload bytes with surviving lump '{survivor.Name}'; safe in-place wiping is impossible.");
      }
    }
  }

  private static void PatchHeader(Stream archive, int count, int directoryOffset) {
    Span<byte> patch = stackalloc byte[8];
    BinaryPrimitives.WriteInt32LittleEndian(patch[..4], count);
    BinaryPrimitives.WriteInt32LittleEndian(patch[4..], directoryOffset);
    archive.Position = 4;
    archive.Write(patch);
  }

  private static void WriteDirectoryEntry(Span<byte> destination, int dataOffset, int size, string name) {
    destination.Clear();
    BinaryPrimitives.WriteInt32LittleEndian(destination[..4], dataOffset);
    BinaryPrimitives.WriteInt32LittleEndian(destination[4..8], size);
    var bytes = Encoding.ASCII.GetBytes(name);
    bytes.AsSpan(0, Math.Min(bytes.Length, NameLength)).CopyTo(destination[8..16]);
  }

  private static string NormalizeName(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var normalized = name.Length > NameLength ? name[..NameLength] : name;
    return normalized.ToUpperInvariant();
  }

  private static string ReadName(ReadOnlySpan<byte> source) {
    var length = source.IndexOf((byte)0);
    if (length < 0)
      length = source.Length;
    return Encoding.ASCII.GetString(source[..length]);
  }

  private static void ZeroRange(Stream archive, long offset, long length) {
    if (length <= 0)
      return;
    var zeroes = new byte[IoBufferSize];
    var remaining = length;
    archive.Position = offset;
    while (remaining > 0) {
      var count = (int)Math.Min(zeroes.Length, remaining);
      archive.Write(zeroes, 0, count);
      remaining -= count;
    }
  }

  private static void ValidateStream(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new NotSupportedException("Changed-byte WAD editing requires a readable, writable, seekable stream.");
  }
}
