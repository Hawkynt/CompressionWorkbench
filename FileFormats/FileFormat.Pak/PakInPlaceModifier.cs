#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Pak;

/// <summary>
/// Changed-byte editor for canonical Quake PACK archives whose directory is the
/// exact physical trailer. New/replacement payload bytes reuse the old directory
/// position and a regenerated directory is appended after them. Removal rewrites
/// only that directory and optionally wipes payload ranges no surviving entry
/// references. Untouched payloads never move.
/// </summary>
public static class PakInPlaceModifier {
  private sealed record DirectoryEntry(byte[] NameBytes, string Name, int Offset, int Length);
  private sealed record State(int DirectoryOffset, int DirectoryLength, List<DirectoryEntry> Entries);
  private readonly record struct Range(int Offset, int Length);

  /// <summary>Adds or same-name replaces one entry.</summary>
  public static void AddFile(Stream pak, string name, byte[] data)
    => AddFiles(pak, [(name, data)]);

  /// <summary>
  /// Adds or same-name replaces entries in one trailer rewrite. All structural
  /// validation and directory serialization complete before the first archive write.
  /// </summary>
  public static void AddFiles(
      Stream pak,
      IReadOnlyList<(string Name, byte[] Data)> files,
      bool wipeReplacedData = true) {
    ValidateWritable(pak);
    ArgumentNullException.ThrowIfNull(files);
    if (files.Count == 0)
      return;

    var requests = new List<(string Name, byte[] NameBytes, byte[] Data)>(files.Count);
    var requestNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (name, data) in files) {
      ArgumentNullException.ThrowIfNull(data);
      var nameBytes = PakWriter.EncodeName(name);
      var normalized = DecodeName(nameBytes);
      if (!requestNames.Add(normalized))
        throw new ArgumentException($"Duplicate PAK mutation name '{normalized}'.", nameof(files));
      requests.Add((normalized, nameBytes, data));
    }

    var state = ReadCanonicalState(pak);
    var planned = new List<DirectoryEntry>(state.Entries);
    var byName = BuildUniqueNameIndex(state.Entries, requests.Select(request => request.Name));
    var newNames = requests.Count(request => !byName.ContainsKey(request.Name));
    if (planned.Count + newNames > PakReader.MaxEntries)
      throw new NotSupportedException($"Quake PAK supports at most {PakReader.MaxEntries} directory entries.");

    var wipeCandidates = new List<Range>();
    var appendOffset = state.DirectoryOffset;

    foreach (var request in requests) {
      if (request.Data.LongLength > int.MaxValue || (long)appendOffset + request.Data.Length > int.MaxValue)
        throw new NotSupportedException("Quake PAK uses signed 32-bit file offsets and lengths.");

      if (byName.TryGetValue(request.Name, out var existingIndex)) {
        var old = planned[existingIndex];
        if (wipeReplacedData && old.Length > 0)
          wipeCandidates.Add(new Range(old.Offset, old.Length));
        planned[existingIndex] = new DirectoryEntry(request.NameBytes, request.Name, appendOffset, request.Data.Length);
      } else {
        byName.Add(request.Name, planned.Count);
        planned.Add(new DirectoryEntry(request.NameBytes, request.Name, appendOffset, request.Data.Length));
      }
      appendOffset = checked(appendOffset + request.Data.Length);
    }

    var newDirectoryOffset = appendOffset;
    var directoryBytes = SerializeDirectory(planned);
    var newLength = checked((long)newDirectoryOffset + directoryBytes.Length);
    if (newLength > int.MaxValue)
      throw new NotSupportedException("Quake PAK exceeds its signed 32-bit layout limit.");
    var safeWipes = PlanSafeWipes(wipeCandidates, planned);
    var headerPatch = BuildHeaderPatch(newDirectoryOffset, directoryBytes.Length);

    // Commit. The old directory is dead space by definition, so new payload bytes
    // can start exactly there without touching any existing payload.
    pak.Position = state.DirectoryOffset;
    foreach (var request in requests)
      if (request.Data.Length > 0)
        pak.Write(request.Data);
    pak.Write(directoryBytes);
    pak.Position = 4;
    pak.Write(headerPatch);
    pak.SetLength(newLength);
    ZeroRanges(pak, safeWipes);
    pak.Flush();
  }

  /// <summary>Removes one named entry. Returns false without writing if absent.</summary>
  public static bool RemoveFile(Stream pak, string name, bool wipeData = true)
    => RemoveFiles(pak, [name], wipeData) > 0;

  /// <summary>
  /// Removes all requested full-path or leaf-name matches in one directory rewrite.
  /// Payloads are left in place; unreferenced removed ranges are zeroed when requested.
  /// </summary>
  public static int RemoveFiles(Stream pak, IReadOnlyCollection<string> names, bool wipeData = true) {
    ValidateWritable(pak);
    ArgumentNullException.ThrowIfNull(names);
    if (names.Count == 0)
      return 0;

    var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var name in names) {
      if (string.IsNullOrWhiteSpace(name))
        continue;
      requested.Add(name.Replace('\\', '/').TrimStart('/'));
    }
    if (requested.Count == 0)
      return 0;

    var state = ReadCanonicalState(pak);
    var kept = new List<DirectoryEntry>(state.Entries.Count);
    var wipeCandidates = new List<Range>();
    var removed = 0;
    foreach (var entry in state.Entries) {
      if (!Matches(entry.Name, requested)) {
        kept.Add(entry);
        continue;
      }
      ++removed;
      if (wipeData && entry.Length > 0)
        wipeCandidates.Add(new Range(entry.Offset, entry.Length));
    }
    if (removed == 0)
      return 0;

    var directoryBytes = SerializeDirectory(kept);
    var newLength = checked((long)state.DirectoryOffset + directoryBytes.Length);
    var safeWipes = PlanSafeWipes(wipeCandidates, kept);
    var headerPatch = BuildHeaderPatch(state.DirectoryOffset, directoryBytes.Length);

    pak.Position = state.DirectoryOffset;
    pak.Write(directoryBytes);
    pak.Position = 4;
    pak.Write(headerPatch);
    pak.SetLength(newLength);
    ZeroRanges(pak, safeWipes);
    pak.Flush();
    return removed;
  }

  private static State ReadCanonicalState(Stream pak) {
    if (pak.Length < PakReader.HeaderSize || pak.Length > int.MaxValue)
      throw new NotSupportedException("Quake PAK tail editing requires a <=2 GiB canonical archive.");

    Span<byte> header = stackalloc byte[PakReader.HeaderSize];
    pak.Position = 0;
    pak.ReadExactly(header);
    if (!header[..4].SequenceEqual("PACK"u8))
      throw new InvalidDataException("Not a Quake PAK archive: missing PACK magic.");
    var directoryOffset = BinaryPrimitives.ReadInt32LittleEndian(header[4..8]);
    var directoryLength = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
    if (directoryOffset < PakReader.HeaderSize || directoryLength < 0 || directoryLength % PakReader.DirectoryEntrySize != 0)
      throw new InvalidDataException("Quake PAK has an invalid directory offset or length.");
    if ((long)directoryOffset + directoryLength != pak.Length)
      throw new NotSupportedException("Changed-byte PAK editing requires the directory to be the exact physical trailer.");

    var entryCount = directoryLength / PakReader.DirectoryEntrySize;
    if (entryCount > PakReader.MaxEntries)
      throw new NotSupportedException($"Quake PAK contains {entryCount} entries; the original engine limit is {PakReader.MaxEntries}.");

    var entries = new List<DirectoryEntry>(entryCount);
    var record = new byte[PakReader.DirectoryEntrySize];
    pak.Position = directoryOffset;
    for (var i = 0; i < entryCount; ++i) {
      pak.ReadExactly(record);
      var rawName = record.AsSpan(0, PakReader.NameFieldSize).ToArray();
      var name = DecodeName(rawName);
      var offset = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(56, 4));
      var length = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(60, 4));
      if (offset < PakReader.HeaderSize || length < 0 || (long)offset + length > directoryOffset)
        throw new NotSupportedException(
          $"Changed-byte PAK editing requires payload '{name}' to lie wholly before the trailing directory.");
      entries.Add(new DirectoryEntry(rawName, name, offset, length));
    }
    return new State(directoryOffset, directoryLength, entries);
  }

  private static Dictionary<string, int> BuildUniqueNameIndex(
      IReadOnlyList<DirectoryEntry> entries,
      IEnumerable<string> namesBeingChanged) {
    var targets = new HashSet<string>(namesBeingChanged, StringComparer.OrdinalIgnoreCase);
    var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < entries.Count; ++i) {
      if (!targets.Contains(entries[i].Name))
        continue;
      if (!result.TryAdd(entries[i].Name, i))
        throw new NotSupportedException(
          $"PAK contains duplicate directory entries named '{entries[i].Name}'; replacement semantics are ambiguous.");
    }
    return result;
  }

  private static byte[] SerializeDirectory(IReadOnlyList<DirectoryEntry> entries) {
    var result = new byte[checked(entries.Count * PakReader.DirectoryEntrySize)];
    for (var i = 0; i < entries.Count; ++i) {
      var offset = i * PakReader.DirectoryEntrySize;
      entries[i].NameBytes.CopyTo(result, offset);
      BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset + 56, 4), entries[i].Offset);
      BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset + 60, 4), entries[i].Length);
    }
    return result;
  }

  private static byte[] BuildHeaderPatch(int directoryOffset, int directoryLength) {
    var patch = new byte[8];
    BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(0, 4), directoryOffset);
    BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(4, 4), directoryLength);
    return patch;
  }

  private static List<Range> PlanSafeWipes(IEnumerable<Range> candidates, IReadOnlyList<DirectoryEntry> survivors) {
    var safe = new List<Range>();
    foreach (var candidate in candidates) {
      var overlapsLive = survivors.Any(entry => entry.Length > 0 && Overlaps(candidate, new Range(entry.Offset, entry.Length)));
      if (!overlapsLive)
        safe.Add(candidate);
    }
    if (safe.Count < 2)
      return safe;

    safe.Sort((a, b) => a.Offset.CompareTo(b.Offset));
    var merged = new List<Range>();
    var current = safe[0];
    for (var i = 1; i < safe.Count; ++i) {
      var next = safe[i];
      var currentEnd = (long)current.Offset + current.Length;
      if (next.Offset <= currentEnd) {
        var end = Math.Max(currentEnd, (long)next.Offset + next.Length);
        current = new Range(current.Offset, checked((int)(end - current.Offset)));
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
       left.Offset < (long)right.Offset + right.Length &&
       right.Offset < (long)left.Offset + left.Length;

  private static void ZeroRanges(Stream pak, IReadOnlyList<Range> ranges) {
    if (ranges.Count == 0)
      return;
    var zeroes = new byte[64 * 1024];
    foreach (var range in ranges) {
      pak.Position = range.Offset;
      var remaining = range.Length;
      while (remaining > 0) {
        var count = Math.Min(remaining, zeroes.Length);
        pak.Write(zeroes, 0, count);
        remaining -= count;
      }
    }
  }

  private static bool Matches(string path, HashSet<string> requested) {
    if (requested.Contains(path))
      return true;
    var slash = path.LastIndexOf('/');
    return requested.Contains(slash >= 0 ? path[(slash + 1)..] : path);
  }

  private static string DecodeName(byte[] nameBytes) {
    var terminator = Array.IndexOf(nameBytes, (byte)0);
    var count = terminator >= 0 ? terminator : nameBytes.Length;
    return Encoding.ASCII.GetString(nameBytes, 0, count);
  }

  private static void ValidateWritable(Stream pak) {
    ArgumentNullException.ThrowIfNull(pak);
    if (!pak.CanRead || !pak.CanWrite || !pak.CanSeek)
      throw new NotSupportedException("Changed-byte Quake PAK editing requires a seekable, readable, writable stream.");
  }
}
