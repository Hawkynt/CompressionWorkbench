#pragma warning disable CA5350 // Unreal Pak v3 mandates SHA-1 in entry and index records.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;

namespace FileFormat.UnrealPak;

/// <summary>
/// Random-access editor for the deterministic legacy Pak v3 layout emitted by
/// <see cref="UnrealPakWriter"/>. Existing payload records stay at their original
/// offsets. New/replacement records are appended where the old index started,
/// then the index and fixed footer are regenerated. Removal rewrites only the
/// index/footer and wipes the removed records in place.
/// </summary>
internal static class UnrealPakModifier {
  private const uint SupportedVersion = 3;
  private const int Sha1Length = 20;
  private const int StoredRecordSize = 53;
  private const int IoBufferSize = 64 * 1024;

  private sealed record PendingEntry(string Path, byte[] Data, long Offset, byte[] Hash);

  /// <summary>
  /// Adds or replaces entries without relaying existing payload bytes. Cost is
  /// O(index bytes + new payload bytes + replaced payload bytes), independent of
  /// the size of untouched payloads.
  /// </summary>
  public static void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ValidateStream(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    archive.Position = 0;
    var reader = new UnrealPakReader(archive);
    EnsureSupported(reader);

    var additions = new List<(string Path, byte[] Data)>();
    var addedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName))
        continue;
      var path = ToIndexPath(reader.MountPoint, input.ArchiveName);
      if (!addedNames.Add(path))
        throw new ArgumentException($"Unreal Pak edit contains duplicate entry '{path}'.", nameof(inputs));
      additions.Add((path, input.ReadContent()));
    }
    if (additions.Count == 0)
      return;

    var replaced = new HashSet<string>(additions.Select(entry => entry.Path), StringComparer.OrdinalIgnoreCase);
    var removedEntries = reader.Entries
      .Where(entry => !entry.IsDeleted && replaced.Contains(entry.Path))
      .ToArray();

    // Before the first write, validate every range that will later be wiped. This
    // reads only replaced payloads, never unrelated files.
    foreach (var entry in removedEntries)
      reader.VerifyEntry(entry);

    var survivors = reader.Entries
      .Where(entry => !entry.IsDeleted && !replaced.Contains(entry.Path))
      .ToArray();

    using var localRecords = new MemoryStream();
    var pending = new List<PendingEntry>(additions.Count);
    var nextOffset = reader.IndexOffset;
    foreach (var (path, data) in additions) {
      var hash = SHA1.HashData(data);
      pending.Add(new PendingEntry(path, data, nextOffset, hash));
      WriteStoredRecord(localRecords, serializedOffset: 0, data, hash);
      nextOffset = checked(reader.IndexOffset + localRecords.Length);
    }

    var newIndexOffset = checked(reader.IndexOffset + localRecords.Length);
    var indexBytes = BuildIndex(reader.MountPoint, survivors, pending);
    var footerBytes = BuildFooter(newIndexOffset, indexBytes);

    // Commit only after parsing, validation, hashing, and all metadata
    // serialization succeeded. The old index is the append point by design.
    archive.Position = reader.IndexOffset;
    localRecords.Position = 0;
    localRecords.CopyTo(archive);
    archive.Write(indexBytes);
    archive.Write(footerBytes);
    archive.SetLength(archive.Position);

    foreach (var entry in removedEntries)
      WipeEntryRecord(archive, entry);

    archive.Flush();
  }

  /// <summary>
  /// Removes entries by regenerating the trailing index/footer and wiping only
  /// the removed local records. No surviving payload is copied or recompressed.
  /// </summary>
  public static void Remove(Stream archive, IReadOnlyCollection<string> entryNames) {
    ValidateStream(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    if (entryNames.Count == 0)
      return;

    archive.Position = 0;
    var reader = new UnrealPakReader(archive);
    EnsureSupported(reader);

    var requested = new HashSet<string>(entryNames.Select(NormalizeVisiblePath), StringComparer.OrdinalIgnoreCase);
    var removed = reader.Entries
      .Where(entry => !entry.IsDeleted && MatchesAny(reader.MountPoint, entry.Path, requested))
      .ToArray();
    if (removed.Length == 0)
      return;

    foreach (var entry in removed)
      reader.VerifyEntry(entry);

    var removedSet = new HashSet<UnrealPakReader.UnrealPakEntry>(removed);
    var survivors = reader.Entries
      .Where(entry => !entry.IsDeleted && !removedSet.Contains(entry))
      .ToArray();

    var indexBytes = BuildIndex(reader.MountPoint, survivors, []);
    var footerBytes = BuildFooter(reader.IndexOffset, indexBytes);

    // The monolithic index is the only metadata that must move. Keeping its
    // original offset deliberately leaves holes where removed payloads lived;
    // those holes are wiped below and a later defrag may reclaim them.
    archive.Position = reader.IndexOffset;
    archive.Write(indexBytes);
    archive.Write(footerBytes);
    archive.SetLength(archive.Position);

    foreach (var entry in removed)
      WipeEntryRecord(archive, entry);

    archive.Flush();
  }

  private static byte[] BuildIndex(
      string mountPoint,
      IReadOnlyList<UnrealPakReader.UnrealPakEntry> survivors,
      IReadOnlyList<PendingEntry> additions) {
    using var index = new MemoryStream();
    WriteFString(index, mountPoint);
    WriteInt32(index, checked(survivors.Count + additions.Count));

    foreach (var entry in survivors) {
      WriteFString(index, entry.Path);
      WriteExistingIndexRecord(index, entry);
    }

    foreach (var entry in additions) {
      WriteFString(index, entry.Path);
      WriteStoredRecord(index, entry.Offset, entry.Data, entry.Hash, includePayload: false);
    }

    return index.ToArray();
  }

  private static byte[] BuildFooter(long indexOffset, byte[] indexBytes) {
    using var footer = new MemoryStream(44);
    WriteUInt32(footer, UnrealPakReader.Magic);
    WriteUInt32(footer, SupportedVersion);
    WriteInt64(footer, indexOffset);
    WriteInt64(footer, indexBytes.LongLength);
    footer.Write(SHA1.HashData(indexBytes));
    return footer.ToArray();
  }

  private static void WriteExistingIndexRecord(Stream output, UnrealPakReader.UnrealPakEntry entry) {
    WriteInt64(output, entry.Offset);
    WriteInt64(output, entry.Size);
    WriteInt64(output, entry.UncompressedSize);
    WriteUInt32(output, entry.CompressionMethod);
    if (entry.Hash.Length != Sha1Length)
      throw new InvalidDataException($"Pak entry '{entry.Path}' has an invalid SHA-1 length.");
    output.Write(entry.Hash);

    if (entry.CompressionMethod != UnrealPakReader.CompressionNone) {
      WriteInt32(output, entry.CompressionBlocks.Count);
      foreach (var block in entry.CompressionBlocks) {
        WriteInt64(output, block.CompressedStart);
        WriteInt64(output, block.CompressedEnd);
      }
    }

    output.WriteByte(entry.Flags);
    WriteUInt32(output, entry.CompressionBlockSize);
  }

  private static void WriteStoredRecord(
      Stream output,
      long serializedOffset,
      byte[] data,
      byte[] hash,
      bool includePayload = true) {
    WriteInt64(output, serializedOffset);
    WriteInt64(output, data.LongLength);
    WriteInt64(output, data.LongLength);
    WriteUInt32(output, UnrealPakReader.CompressionNone);
    if (hash.Length != Sha1Length)
      throw new InvalidDataException("Pak entry SHA-1 must be exactly 20 bytes.");
    output.Write(hash);
    output.WriteByte(0);
    WriteUInt32(output, 0);
    if (includePayload)
      output.Write(data);
  }

  private static void WipeEntryRecord(Stream archive, UnrealPakReader.UnrealPakEntry entry) {
    var headerSize = checked(StoredRecordSize +
      (entry.CompressionMethod == UnrealPakReader.CompressionNone ? 0 : 4 + entry.CompressionBlocks.Count * 16));
    ZeroRange(archive, entry.Offset, headerSize);

    if (entry.CompressionMethod == UnrealPakReader.CompressionNone) {
      ZeroRange(archive, checked(entry.Offset + headerSize), entry.Size);
      return;
    }

    foreach (var block in entry.CompressionBlocks)
      ZeroRange(archive, block.CompressedStart, block.CompressedSize);
  }

  private static void ZeroRange(Stream stream, long offset, long count) {
    if (count <= 0)
      return;
    var zeroes = new byte[IoBufferSize];
    var remaining = count;
    var position = offset;
    while (remaining > 0) {
      var chunk = (int)Math.Min(zeroes.Length, remaining);
      stream.Position = position;
      stream.Write(zeroes, 0, chunk);
      position += chunk;
      remaining -= chunk;
    }
  }

  private static bool MatchesAny(string mountPoint, string entryPath, HashSet<string> requested) {
    var raw = NormalizeVisiblePath(entryPath);
    var visible = NormalizeVisiblePath(CombinePath(mountPoint, entryPath));
    var leaf = raw[(raw.LastIndexOf('/') + 1)..];
    return requested.Contains(raw) || requested.Contains(visible) || requested.Contains(leaf);
  }

  private static string ToIndexPath(string mountPoint, string inputName) {
    var normalized = NormalizeVisiblePath(inputName);
    var visibleMount = NormalizeVisibleMount(mountPoint);
    if (visibleMount.Length > 0) {
      var prefix = visibleMount + "/";
      if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        normalized = normalized[prefix.Length..];
    }

    if (normalized.Length == 0 || normalized.EndsWith('/'))
      throw new ArgumentException("Pak path must name a file.", nameof(inputName));
    foreach (var part in normalized.Split('/'))
      if (part.Length == 0 || part is "." or ".." || part.IndexOf('\0') >= 0)
        throw new ArgumentException("Unsafe Pak path.", nameof(inputName));
    return normalized;
  }

  private static string CombinePath(string mountPoint, string path) {
    var mount = NormalizeVisibleMount(mountPoint);
    var normalizedPath = NormalizeVisiblePath(path);
    return mount.Length == 0 ? normalizedPath : mount + "/" + normalizedPath;
  }

  private static string NormalizeVisibleMount(string mountPoint) {
    var normalized = mountPoint.Replace('\\', '/');
    while (normalized.StartsWith("../", StringComparison.Ordinal))
      normalized = normalized[3..];
    return normalized.Trim('/');
  }

  private static string NormalizeVisiblePath(string path)
    => path.Replace('\\', '/').TrimStart('/');

  private static void EnsureSupported(UnrealPakReader reader) {
    if (reader.PakVersion != SupportedVersion)
      throw new NotSupportedException(
        $"Trailer-only Pak editing currently supports v{SupportedVersion}; got v{reader.PakVersion}.");
    if (reader.IsIndexEncrypted)
      throw new NotSupportedException("Trailer-only Pak editing does not support encrypted indexes.");
  }

  private static void ValidateStream(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new NotSupportedException("Trailer-only Pak editing requires a readable, writable, seekable stream.");
  }

  private static void WriteFString(Stream output, string value) {
    ArgumentNullException.ThrowIfNull(value);
    if (value.IndexOf('\0') >= 0)
      throw new ArgumentException("Pak FString values may not contain embedded NUL characters.", nameof(value));

    if (value.All(ch => ch <= 0x7F)) {
      var bytes = Encoding.UTF8.GetBytes(value);
      WriteInt32(output, checked(bytes.Length + 1));
      output.Write(bytes);
      output.WriteByte(0);
      return;
    }

    var utf16 = Encoding.Unicode.GetBytes(value);
    WriteInt32(output, checked(-(value.Length + 1)));
    output.Write(utf16);
    output.WriteByte(0);
    output.WriteByte(0);
  }

  private static void WriteUInt32(Stream output, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    output.Write(bytes);
  }

  private static void WriteInt32(Stream output, int value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
    output.Write(bytes);
  }

  private static void WriteInt64(Stream output, long value) {
    Span<byte> bytes = stackalloc byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
    output.Write(bytes);
  }
}
