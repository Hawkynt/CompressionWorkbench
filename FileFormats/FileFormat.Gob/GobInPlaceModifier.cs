using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Gob;

/// <summary>
/// Changed-byte editor for canonical GOB v2 archives whose directory is the
/// exact physical trailer. Added/replacement payloads reuse the old directory
/// position; removal rewrites only the directory and leaves survivor payloads
/// at their original offsets. Removed bytes are wiped only when no surviving
/// directory record overlaps them.
/// </summary>
public static class GobInPlaceModifier {
  private sealed record Entry(byte[] NameField, string Name, uint Offset, uint Size);
  private sealed record State(uint Version, uint DirectoryOffset, List<Entry> Entries);
  private readonly record struct Range(long Offset, long Length);

  /// <summary>Adds or same-name replaces one stored entry.</summary>
  public static void AddFile(Stream archive, string name, byte[] data, bool wipeReplacedData = true)
    => AddFiles(archive, [(name, data)], wipeReplacedData);

  /// <summary>
  /// Adds or same-name replaces multiple entries with one directory rewrite.
  /// Structural rejection and replacement-directory serialization happen before
  /// the first archive write.
  /// </summary>
  public static void AddFiles(
      Stream archive,
      IReadOnlyList<(string Name, byte[] Data)> files,
      bool wipeReplacedData = true) {
    ValidateWritable(archive);
    ArgumentNullException.ThrowIfNull(files);
    if (files.Count == 0)
      return;

    var requests = new List<(string Name, byte[] NameField, byte[] Data)>(files.Count);
    var requestNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (name, data) in files) {
      ArgumentNullException.ThrowIfNull(data);
      var nameField = EncodeName(name, out var normalized);
      if (!requestNames.Add(NormalizeForMatch(normalized)))
        throw new ArgumentException($"Duplicate GOB mutation name '{normalized}'.", nameof(files));
      requests.Add((normalized, nameField, data));
    }

    var state = ReadCanonicalState(archive);
    var planned = new List<Entry>(state.Entries);
    var targetIndex = BuildUniqueTargetIndex(planned, requests.Select(request => request.Name));
    var appendOffset = (long)state.DirectoryOffset;
    var wipes = new List<Range>();

    foreach (var request in requests) {
      if (appendOffset > uint.MaxValue || request.Data.LongLength > uint.MaxValue || appendOffset + request.Data.LongLength > uint.MaxValue)
        throw new NotSupportedException("GOB v2 uses 32-bit payload offsets and sizes.");

      var key = NormalizeForMatch(request.Name);
      var replacement = new Entry(request.NameField, request.Name, checked((uint)appendOffset), checked((uint)request.Data.Length));
      if (targetIndex.TryGetValue(key, out var index)) {
        var old = planned[index];
        if (wipeReplacedData && old.Size > 0)
          wipes.Add(new Range(old.Offset, old.Size));
        planned[index] = replacement;
      } else {
        targetIndex.Add(key, planned.Count);
        planned.Add(replacement);
      }
      appendOffset = checked(appendOffset + request.Data.LongLength);
    }

    if (appendOffset > uint.MaxValue)
      throw new NotSupportedException("GOB v2 directory offset exceeds UInt32.");
    var directoryBytes = SerializeDirectory(planned);
    var newLength = checked(appendOffset + directoryBytes.LongLength);
    var safeWipes = PlanSafeWipes(wipes, planned);
    var directoryOffsetPatch = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(directoryOffsetPatch, checked((uint)appendOffset));

    archive.Position = state.DirectoryOffset;
    foreach (var request in requests)
      if (request.Data.Length > 0)
        archive.Write(request.Data);
    archive.Write(directoryBytes);
    archive.Position = 8;
    archive.Write(directoryOffsetPatch);
    archive.SetLength(newLength);
    ZeroRanges(archive, safeWipes);
    archive.Flush();
  }

  /// <summary>Removes one entry. Returns false without writing when it is absent.</summary>
  public static bool RemoveFile(Stream archive, string name, bool wipeData = true)
    => RemoveFiles(archive, [name], wipeData) > 0;

  /// <summary>
  /// Removes matching full paths or leaf names. Survivor payloads are not moved;
  /// only the directory and unreferenced removed payload ranges are written.
  /// </summary>
  public static int RemoveFiles(Stream archive, IReadOnlyCollection<string> names, bool wipeData = true) {
    ValidateWritable(archive);
    ArgumentNullException.ThrowIfNull(names);
    if (names.Count == 0)
      return 0;

    var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var name in names)
      if (!string.IsNullOrWhiteSpace(name))
        requested.Add(NormalizeForMatch(name));
    if (requested.Count == 0)
      return 0;

    var state = ReadCanonicalState(archive);
    var kept = new List<Entry>(state.Entries.Count);
    var wipes = new List<Range>();
    var removed = 0;
    foreach (var entry in state.Entries) {
      if (!Matches(entry.Name, requested)) {
        kept.Add(entry);
        continue;
      }
      ++removed;
      if (wipeData && entry.Size > 0)
        wipes.Add(new Range(entry.Offset, entry.Size));
    }
    if (removed == 0)
      return 0;

    var directoryBytes = SerializeDirectory(kept);
    var newLength = checked((long)state.DirectoryOffset + directoryBytes.LongLength);
    var safeWipes = PlanSafeWipes(wipes, kept);

    archive.Position = state.DirectoryOffset;
    archive.Write(directoryBytes);
    archive.SetLength(newLength);
    ZeroRanges(archive, safeWipes);
    archive.Flush();
    return removed;
  }

  private static State ReadCanonicalState(Stream archive) {
    if (archive.Length < GobConstants.HeaderSize)
      throw new InvalidDataException("GOB stream is shorter than its fixed header.");

    Span<byte> header = stackalloc byte[GobConstants.HeaderSize];
    archive.Position = 0;
    archive.ReadExactly(header);
    if (!header[..4].SequenceEqual(GobConstants.Magic))
      throw new InvalidDataException("Invalid GOB v2 magic.");

    var version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
    var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
    if (directoryOffset < GobConstants.HeaderSize || directoryOffset > archive.Length - 4)
      throw new InvalidDataException("GOB directory offset is out of range.");

    Span<byte> countBytes = stackalloc byte[4];
    archive.Position = directoryOffset;
    archive.ReadExactly(countBytes);
    var count = BinaryPrimitives.ReadUInt32LittleEndian(countBytes);
    if (count > int.MaxValue)
      throw new NotSupportedException("GOB directory entry count exceeds the managed limit.");
    var directoryLength = checked(4L + (long)count * GobConstants.DirectoryEntrySize);
    if ((long)directoryOffset + directoryLength != archive.Length)
      throw new NotSupportedException("Changed-byte GOB editing requires the directory to be the exact physical trailer.");

    var entries = new List<Entry>((int)count);
    var record = new byte[GobConstants.DirectoryEntrySize];
    for (var i = 0; i < (int)count; ++i) {
      archive.ReadExactly(record);
      var offset = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0, 4));
      var size = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4, 4));
      var rawName = record.AsSpan(8, GobConstants.NameFieldSize).ToArray();
      var name = DecodeName(rawName);
      if (offset < GobConstants.HeaderSize || (long)offset + size > directoryOffset)
        throw new NotSupportedException(
          $"Changed-byte GOB editing requires payload '{name}' to lie wholly before the trailing directory.");
      entries.Add(new Entry(rawName, name, offset, size));
    }

    return new State(version, directoryOffset, entries);
  }

  private static Dictionary<string, int> BuildUniqueTargetIndex(
      IReadOnlyList<Entry> entries,
      IEnumerable<string> targetNames) {
    var targets = new HashSet<string>(targetNames.Select(NormalizeForMatch), StringComparer.OrdinalIgnoreCase);
    var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < entries.Count; ++i) {
      var key = NormalizeForMatch(entries[i].Name);
      if (!targets.Contains(key))
        continue;
      if (!result.TryAdd(key, i))
        throw new NotSupportedException(
          $"GOB contains duplicate entries named '{entries[i].Name}'; replacement semantics are ambiguous.");
    }
    return result;
  }

  private static byte[] SerializeDirectory(IReadOnlyList<Entry> entries) {
    var result = new byte[checked(4 + entries.Count * GobConstants.DirectoryEntrySize)];
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), checked((uint)entries.Count));
    for (var i = 0; i < entries.Count; ++i) {
      var position = 4 + i * GobConstants.DirectoryEntrySize;
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(position, 4), entries[i].Offset);
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(position + 4, 4), entries[i].Size);
      entries[i].NameField.CopyTo(result, position + 8);
    }
    return result;
  }

  private static byte[] EncodeName(string name, out string normalized) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    normalized = name.Replace('/', '\\').TrimStart('\\');
    if (normalized.Length == 0 || normalized.EndsWith('\\'))
      throw new ArgumentException("GOB entry name must identify a file.", nameof(name));
    foreach (var part in normalized.Split('\\'))
      if (part.Length == 0 || part is "." or "..")
        throw new ArgumentException("Unsafe GOB entry path.", nameof(name));
    if (normalized.Any(c => c == '\0' || c > '\x7F'))
      throw new ArgumentException("GOB v2 entry names are 7-bit archive paths.", nameof(name));

    var bytes = Encoding.ASCII.GetBytes(normalized);
    if (bytes.Length > GobConstants.MaxNameLength)
      throw new ArgumentException($"GOB entry names are limited to {GobConstants.MaxNameLength} bytes.", nameof(name));
    var field = new byte[GobConstants.NameFieldSize];
    bytes.CopyTo(field, 0);
    return field;
  }

  private static string DecodeName(byte[] field) {
    var terminator = Array.IndexOf(field, (byte)0);
    var length = terminator >= 0 ? terminator : field.Length;
    return Encoding.ASCII.GetString(field, 0, length);
  }

  private static string NormalizeForMatch(string name)
    => name.Replace('/', '\\').TrimStart('\\');

  private static bool Matches(string path, HashSet<string> requested) {
    var normalized = NormalizeForMatch(path);
    if (requested.Contains(normalized))
      return true;
    var separator = normalized.LastIndexOf('\\');
    return requested.Contains(separator >= 0 ? normalized[(separator + 1)..] : normalized);
  }

  private static List<Range> PlanSafeWipes(IEnumerable<Range> candidates, IReadOnlyList<Entry> survivors) {
    var safe = new List<Range>();
    foreach (var candidate in candidates) {
      var overlapsLive = survivors.Any(entry => entry.Size > 0 && Overlaps(candidate, new Range(entry.Offset, entry.Size)));
      if (!overlapsLive)
        safe.Add(candidate);
    }
    if (safe.Count < 2)
      return safe;

    safe.Sort((left, right) => left.Offset.CompareTo(right.Offset));
    var merged = new List<Range>();
    var current = safe[0];
    for (var i = 1; i < safe.Count; ++i) {
      var next = safe[i];
      var currentEnd = checked(current.Offset + current.Length);
      if (next.Offset <= currentEnd) {
        var end = Math.Max(currentEnd, checked(next.Offset + next.Length));
        current = new Range(current.Offset, checked(end - current.Offset));
      } else {
        merged.Add(current);
        current = next;
      }
    }
    merged.Add(current);
    return merged;
  }

  private static bool Overlaps(Range left, Range right)
    => left.Length > 0 && right.Length > 0 &&
       left.Offset < right.Offset + right.Length && right.Offset < left.Offset + left.Length;

  private static void ZeroRanges(Stream archive, IReadOnlyList<Range> ranges) {
    if (ranges.Count == 0)
      return;
    var zeroes = new byte[64 * 1024];
    foreach (var range in ranges) {
      archive.Position = range.Offset;
      var remaining = range.Length;
      while (remaining > 0) {
        var count = (int)Math.Min(remaining, zeroes.Length);
        archive.Write(zeroes, 0, count);
        remaining -= count;
      }
    }
  }

  private static void ValidateWritable(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new NotSupportedException("Changed-byte GOB editing requires a seekable, readable, writable stream.");
  }
}
