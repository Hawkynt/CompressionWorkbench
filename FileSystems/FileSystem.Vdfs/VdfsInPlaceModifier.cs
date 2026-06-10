#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Vdfs;

/// <summary>
/// In-place VDFS modifier — true random-access editing without rebuilding the
/// whole archive. Targets images produced by <see cref="VdfsWriter"/>: 16-byte
/// magic, 20-byte header fields, 80-byte entries packed at <c>rootOffset</c>,
/// contiguous stored-mode file data referenced by each entry's jump field.
///
/// <para>What it touches per <see cref="AddFile"/>:
/// <list type="bullet">
///   <item>Appends the new file's data bytes at end-of-stream.</item>
///   <item>Relocates the entry table to a fresh region past all live data —
///         existing data byte ranges keep their original absolute offsets so
///         every surviving entry's jump field remains valid without rewriting.</item>
///   <item>Patches the header's entry count, file count, total data size, and
///         root/entries offset in-place.</item>
/// </list>
/// The previously-occupied entry-table bytes become orphaned slack and are
/// reclaimable through a defrag pass.</para>
///
/// <para><see cref="RemoveFile"/> zeroes the entry's name (first byte = 0)
/// which the reader already skips as "empty name", and optionally wipes the
/// file's data bytes. The entry record itself is left in the table so existing
/// neighbour entries keep their byte offsets unchanged.</para>
///
/// <para><see cref="ReplaceFile"/> rewrites the file's data at its current
/// offset when the new payload fits and updates the entry's size in-place;
/// otherwise it falls back to remove-plus-add so the new payload lives at a
/// fresh tail offset and no other entries are disturbed.</para>
///
/// <para>Honest scope: this modifier handles VDFS <b>stored mode only</b>.
/// The VDFS format spec leaves entry-type flags reserved for compressed
/// variants but no production Gothic-engine image observed in REGoth/VdfsSharp
/// corpora uses them — compression support is deferred until a real example
/// surfaces.</para>
/// </summary>
public static class VdfsInPlaceModifier {
  private const int HeaderSize = 16;
  private const int FieldsSize = 20;
  private const int EntrySize = 80;
  private const int DefaultEntriesStart = HeaderSize + FieldsSize; // 36
  private const uint TypeFile = 0x02;

  private const int OffsetEntryCount = 16;
  private const int OffsetFileCount = 20;
  private const int OffsetTotalDataSize = 28;
  private const int OffsetRootOffset = 32;

  private const int EntryNameSize = 64;
  private const int EntryOffsetJump = 64;
  private const int EntryOffsetSize = 68;
  private const int EntryOffsetType = 72;
  private const int EntryOffsetAttr = 76;

  // ── Public API ────────────────────────────────────────────────────────────

  /// <summary>
  /// Adds (or replaces) a file in a VDFS archive. The new data bytes are
  /// appended at end-of-stream and a new entry-table region is written past
  /// them; existing live file data keeps its original absolute byte offset so
  /// every surviving entry's jump pointer remains valid.
  /// </summary>
  public static void AddFile(Stream archive, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (!archive.CanSeek || !archive.CanRead || !archive.CanWrite)
      throw new ArgumentException("Archive stream must be readable, writable, and seekable.", nameof(archive));

    // Replace semantics: drop any prior entry of the same name (entry stays
    // in the table marked-empty so neighbour offsets don't shift).
    RemoveFile(archive, name, wipeData: true);

    var ctx = ReadContext(archive);

    // Live extents = jump windows of surviving entries (those whose first
    // name byte is non-zero and whose type marks them as a file).
    var liveEntries = ctx.Entries.Where(e => e.IsLive).ToList();
    var liveDataEnd = ComputeLiveDataEnd(ctx, liveEntries);

    // Append the new file's data at the live-data tail.
    var newDataOffset = liveDataEnd;
    EnsureLength(archive, newDataOffset + data.Length);
    archive.Position = newDataOffset;
    archive.Write(data);
    var afterNewData = newDataOffset + data.Length;

    // Rebuild the entry-table body in memory: surviving entries (verbatim
    // 80-byte records, jump pointers untouched) plus one fresh entry for the
    // new file. The fresh entry's name is space-padded then null-terminated
    // exactly the way VdfsWriter would have written it for a Build-time add.
    var survivingRecords = liveEntries.Select(e => e.Record).ToList();
    var newRecord = BuildEntryRecord(name, (uint)newDataOffset, (uint)data.Length);
    survivingRecords.Add(newRecord);

    // Place the new entry table at the very end of the file.
    var newRootOffset = afterNewData;
    var newTableSize = survivingRecords.Count * EntrySize;
    EnsureLength(archive, newRootOffset + newTableSize);
    archive.Position = newRootOffset;
    foreach (var rec in survivingRecords)
      archive.Write(rec);

    // Patch the header to point at the relocated entry table.
    PatchHeader(archive,
      entryCount: (uint)survivingRecords.Count,
      fileCount: (uint)survivingRecords.Count,
      totalDataSize: SumFileSizes(survivingRecords),
      rootOffset: (uint)newRootOffset);
  }

  /// <summary>
  /// Removes a named file from a VDFS archive. The entry's first name byte is
  /// zeroed (the reader's existing "empty name → skip" rule turns it into a
  /// tombstone), all 80 entry bytes are cleared, and the file's data extent is
  /// optionally zero-wiped. Returns <c>true</c> when an entry was removed.
  /// </summary>
  public static bool RemoveFile(Stream archive, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    if (!archive.CanSeek || !archive.CanRead || !archive.CanWrite)
      throw new ArgumentException("Archive stream must be readable, writable, and seekable.", nameof(archive));

    Context ctx;
    try {
      ctx = ReadContext(archive);
    } catch (InvalidDataException) {
      return false;
    }

    var hit = ctx.Entries.FirstOrDefault(e => e.IsLive
      && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    if (hit == null) return false;

    // Zero the 80-byte entry record on disk. The reader skips entries with
    // an empty name (first byte == 0 makes the decoded name empty) and the
    // surrounding entries keep their original byte positions in the table.
    var zeros = new byte[EntrySize];
    archive.Position = hit.RecordOffset;
    archive.Write(zeros);

    if (wipeData && hit.DataSize > 0) {
      WipeRange(archive, hit.DataOffset, hit.DataSize);
    }

    // Update header file count (entry-table length stays the same — the dead
    // record stays in place to preserve neighbour offsets).
    var liveCount = ctx.Entries.Count(e => e.IsLive
      && !string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    var liveTotal = ctx.Entries
      .Where(e => e.IsLive && !string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
      .Sum(e => (long)e.DataSize);
    PatchFileCount(archive, (uint)liveCount, (uint)liveTotal);

    return true;
  }

  /// <summary>
  /// Replaces an existing file's payload in-place when the new bytes fit
  /// inside the original extent (data written at the same offset, entry size
  /// trimmed). When the new payload is larger, falls back to remove-plus-add
  /// so the bytes land at a fresh tail offset.
  /// </summary>
  public static bool ReplaceFile(Stream archive, string name, byte[] newData) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(newData);
    if (!archive.CanSeek || !archive.CanRead || !archive.CanWrite)
      throw new ArgumentException("Archive stream must be readable, writable, and seekable.", nameof(archive));

    var ctx = ReadContext(archive);
    var hit = ctx.Entries.FirstOrDefault(e => e.IsLive
      && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    if (hit == null) {
      AddFile(archive, name, newData);
      return true;
    }

    if (newData.Length <= hit.DataSize) {
      // Fits — rewrite payload at the existing offset, update entry size.
      archive.Position = hit.DataOffset;
      archive.Write(newData);
      var tail = hit.DataSize - newData.Length;
      if (tail > 0) {
        // Zero the unused tail of the original extent so leftover bytes
        // from the prior payload don't linger.
        WipeRange(archive, hit.DataOffset + newData.Length, tail);
      }
      // Patch the entry's size field on disk.
      archive.Position = hit.RecordOffset + EntryOffsetSize;
      Span<byte> sizeBuf = stackalloc byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(sizeBuf, (uint)newData.Length);
      archive.Write(sizeBuf);

      // Recompute the header's total-data-size from all live entries.
      var newTotal = (long)0;
      foreach (var e in ctx.Entries)
        if (e.IsLive)
          newTotal += string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)
            ? newData.Length
            : e.DataSize;
      PatchFileCount(archive, (uint)ctx.Entries.Count(e => e.IsLive), (uint)newTotal);
      return true;
    }

    // Doesn't fit — remove + add at the tail.
    RemoveFile(archive, name, wipeData: true);
    AddFile(archive, name, newData);
    return true;
  }

  // ── Context parsing ──────────────────────────────────────────────────────

  private sealed record EntryView(
    int Index,
    long RecordOffset,
    string Name,
    long DataOffset,
    long DataSize,
    uint Type,
    byte[] Record) {
    /// <summary>
    /// Live = non-empty name AND not flagged as a pure directory (per VDFS
    /// type-bit convention: bit 0 alone = directory). A directory record
    /// has no data extent we'd want to wipe/relocate.
    /// </summary>
    public bool IsLive => !string.IsNullOrEmpty(this.Name) && (this.Type & 0x01) == 0;
  }

  private sealed record Context(
    int EntryCount,
    int RootOffset,
    long ImageLength,
    IReadOnlyList<EntryView> Entries);

  private static Context ReadContext(Stream archive) {
    archive.Position = 0;
    if (archive.Length < HeaderSize + FieldsSize)
      throw new InvalidDataException("VDFS: archive too small for header.");

    var header = new byte[HeaderSize + FieldsSize];
    archive.ReadExactly(header);

    var magic = "PSVDSC_V2.00\n\r\n\r"u8;
    if (!header.AsSpan(0, magic.Length).SequenceEqual(magic))
      throw new InvalidDataException("VDFS: invalid magic.");

    var entryCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(OffsetEntryCount));
    var rootOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(OffsetRootOffset));
    var entriesStart = rootOffset > 0 ? rootOffset : DefaultEntriesStart;

    var entries = new List<EntryView>(entryCount);
    for (var i = 0; i < entryCount; i++) {
      var recordOffset = entriesStart + i * EntrySize;
      if (recordOffset + EntrySize > archive.Length) break;
      var rec = new byte[EntrySize];
      archive.Position = recordOffset;
      archive.ReadExactly(rec);

      var name = DecodeName(rec);
      var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(EntryOffsetJump));
      var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(EntryOffsetSize));
      var type = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(EntryOffsetType));

      entries.Add(new EntryView(
        Index: i,
        RecordOffset: recordOffset,
        Name: name,
        DataOffset: dataOffset,
        DataSize: dataSize,
        Type: type,
        Record: rec));
    }

    return new Context(
      EntryCount: entryCount,
      RootOffset: entriesStart,
      ImageLength: archive.Length,
      Entries: entries);
  }

  /// <summary>
  /// Mirrors the writer's name decoding: the entry name is the ASCII bytes up
  /// to the first NUL or trailing space terminator. An entry whose first byte
  /// is NUL decodes to an empty string, which signals a tombstone to the reader.
  /// </summary>
  private static string DecodeName(byte[] rec) {
    var nameLen = EntryNameSize;
    for (var j = 0; j < EntryNameSize; j++) {
      if (rec[j] == 0 || (rec[j] == 0x20 && (j + 1 >= EntryNameSize || rec[j + 1] == 0))) {
        nameLen = j;
        break;
      }
    }
    return Encoding.ASCII.GetString(rec, 0, nameLen).TrimEnd();
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Computes the offset one byte past the last live file's data extent.
  /// New payloads are appended here so existing live extents keep their
  /// original byte ranges.
  /// </summary>
  private static long ComputeLiveDataEnd(Context ctx, IReadOnlyList<EntryView> liveEntries) {
    long maxEnd = 0;
    foreach (var e in liveEntries) {
      var end = e.DataOffset + e.DataSize;
      if (end > maxEnd) maxEnd = end;
    }

    // Floor: the live-data region starts at least at the end of the writer's
    // default table layout. When no files survive we still want to avoid
    // overlapping the original 36 + N*80 metadata window.
    var defaultDataStart = (long)(DefaultEntriesStart + ctx.EntryCount * EntrySize);
    if (maxEnd < defaultDataStart) maxEnd = defaultDataStart;
    return maxEnd;
  }

  private static byte[] BuildEntryRecord(string name, uint dataOffset, uint dataSize) {
    var rec = new byte[EntrySize];
    var nameBytes = Encoding.ASCII.GetBytes(name);
    Array.Fill(rec, (byte)0x20, 0, EntryNameSize);
    Array.Copy(nameBytes, 0, rec, 0, Math.Min(nameBytes.Length, EntryNameSize));
    rec[Math.Min(nameBytes.Length, EntryNameSize - 1)] = 0; // NUL terminator
    BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(EntryOffsetJump), dataOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(EntryOffsetSize), dataSize);
    BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(EntryOffsetType), TypeFile);
    BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(EntryOffsetAttr), 0);
    return rec;
  }

  private static uint SumFileSizes(IReadOnlyList<byte[]> records) {
    var total = 0u;
    foreach (var rec in records) {
      var type = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(EntryOffsetType));
      if ((type & 0x01) != 0) continue; // skip directory records
      total += BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(EntryOffsetSize));
    }
    return total;
  }

  private static void PatchHeader(Stream archive, uint entryCount, uint fileCount, uint totalDataSize, uint rootOffset) {
    Span<byte> buf = stackalloc byte[4];

    archive.Position = OffsetEntryCount;
    BinaryPrimitives.WriteUInt32LittleEndian(buf, entryCount);
    archive.Write(buf);

    archive.Position = OffsetFileCount;
    BinaryPrimitives.WriteUInt32LittleEndian(buf, fileCount);
    archive.Write(buf);

    archive.Position = OffsetTotalDataSize;
    BinaryPrimitives.WriteUInt32LittleEndian(buf, totalDataSize);
    archive.Write(buf);

    archive.Position = OffsetRootOffset;
    BinaryPrimitives.WriteUInt32LittleEndian(buf, rootOffset);
    archive.Write(buf);
  }

  private static void PatchFileCount(Stream archive, uint fileCount, uint totalDataSize) {
    Span<byte> buf = stackalloc byte[4];
    archive.Position = OffsetFileCount;
    BinaryPrimitives.WriteUInt32LittleEndian(buf, fileCount);
    archive.Write(buf);

    archive.Position = OffsetTotalDataSize;
    BinaryPrimitives.WriteUInt32LittleEndian(buf, totalDataSize);
    archive.Write(buf);
  }

  private static void WipeRange(Stream archive, long start, long count) {
    if (start >= archive.Length) return;
    var capped = Math.Min(count, archive.Length - start);
    archive.Position = start;
    var chunk = new byte[Math.Min(4096, capped)];
    var remaining = capped;
    while (remaining > 0) {
      var write = (int)Math.Min(chunk.Length, remaining);
      archive.Write(chunk, 0, write);
      remaining -= write;
    }
  }

  private static void EnsureLength(Stream archive, long required) {
    if (archive.Length < required) archive.SetLength(required);
  }
}
