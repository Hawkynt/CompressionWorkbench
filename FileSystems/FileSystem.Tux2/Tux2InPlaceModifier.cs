#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Tux2;

/// <summary>
/// Genuine in-place R/W mutation for TUX2 synthetic images. The image is a
/// 16-byte header (<c>"TUX2FS\0\0"</c> magic, <c>u32 version</c>, <c>u32
/// file_count</c>) followed by back-to-back per-file records
/// (<c>u16 nameLen</c>, name, <c>u32 dataLen</c>, data).
/// </summary>
/// <remarks>
/// <para>Byte-preservation guarantees per operation:</para>
/// <list type="bullet">
///   <item><b>Add (new name)</b> — appends a fresh record at the end of the
///     image and bumps <c>file_count</c>. The header's first 12 bytes and
///     every prior record stay byte-identical at their original offsets.
///     Genuine append-only in-place.</item>
///   <item><b>Replace, same encoded size</b> — overwrites the matched record's
///     data bytes in place. Every other byte in the image (header, preceding
///     and following records) stays byte-identical. Genuine in-place.</item>
///   <item><b>Replace, different size</b> and <b>Remove</b> — the inline
///     variable-length layout means resizing/dropping one record shifts every
///     following record. We rewrite the tail starting at the changed record's
///     offset; the header and all <em>preceding</em> records stay byte-identical
///     at their original offsets. This is a localized O(tail) relayout, not a
///     full re-encode.</item>
/// </list>
/// </remarks>
internal static class Tux2InPlaceModifier {

  private const int HeaderSize = 16;
  private const int CountOffset = 12;

  // Reader emits these synthetic non-file entries; they must never be treated
  // as real records when planning a mutation.
  private static readonly HashSet<string> Synthetic =
    new(StringComparer.OrdinalIgnoreCase) { "FULL.tux2", "metadata.ini" };

  // ── Public entry points ────────────────────────────────────────────

  public static void Add(
    Stream archive,
    IReadOnlyList<ArchiveInputInfo> inputs,
    Action<Stream, IReadOnlyList<ArchiveInputInfo>> rebuild) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(rebuild);

    var payloads = new List<(string Name, byte[] Data)>();
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      payloads.Add((name, data));
    if (payloads.Count == 0) return;

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();

    if (!TryAddInPlace(image, payloads, out var result)) {
      rebuild(archive, inputs);
      return;
    }

    archive.Position = 0;
    archive.Write(result);
    archive.SetLength(result.Length);
  }

  public static void Remove(
    Stream archive,
    string[] entryNames,
    Action<Stream, string[]> rebuild) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    ArgumentNullException.ThrowIfNull(rebuild);
    if (entryNames.Length == 0) return;

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();

    if (!TryRemoveInPlace(image, entryNames, out var result)) {
      rebuild(archive, entryNames);
      return;
    }

    archive.Position = 0;
    archive.Write(result);
    archive.SetLength(result.Length);
  }

  // ── Core ───────────────────────────────────────────────────────────

  /// <summary>
  /// Parsed view of every real record: name, encoded byte offset, total
  /// encoded length, and the data span.
  /// </summary>
  private readonly record struct Record(string Name, int Offset, int Length, int DataOffset, int DataLength);

  private static bool TryParse(byte[] image, out uint version, out List<Record> records) {
    version = 0;
    records = [];
    if (image.Length < HeaderSize) return false;
    if (!image.AsSpan(0, 8).SequenceEqual(Tux2Reader.Magic)) return false;

    version = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(8));
    var declared = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(CountOffset));

    var pos = HeaderSize;
    var count = 0u;
    while (count < declared && pos + 2 <= image.Length) {
      var start = pos;
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pos));
      pos += 2;
      if (pos + nameLen + 4 > image.Length) return false;
      var name = Encoding.UTF8.GetString(image, pos, nameLen);
      pos += nameLen;
      var dataLen = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(pos));
      pos += 4;
      if (dataLen > int.MaxValue || pos + (long)dataLen > image.Length) return false;
      var dataOff = pos;
      pos += (int)dataLen;
      records.Add(new Record(name, start, pos - start, dataOff, (int)dataLen));
      count++;
    }
    // file_count must be accurate for a clean in-place edit.
    return count == declared;
  }

  private static bool TryAddInPlace(byte[] image, List<(string Name, byte[] Data)> payloads, out byte[] result) {
    result = image;
    if (!TryParse(image, out var version, out var records)) return false;

    // No nested directories in TUX2.
    foreach (var (name, _) in payloads)
      if (name.Contains('/') || name.Contains('\\') || Synthetic.Contains(name))
        return false;

    var working = new MemoryStream();
    working.Write(image, 0, image.Length);

    var liveCount = records.Count;

    foreach (var (name, data) in payloads) {
      var idx = records.FindIndex(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
      if (idx < 0) {
        // New record — append at end, bump count. Genuine in-place.
        AppendRecord(working, name, data);
        liveCount++;
        // Refresh the record table view (append doesn't shift existing records).
        var arr0 = working.ToArray();
        if (!TryParse(arr0, out version, out records)) return false;
        continue;
      }

      var rec = records[idx];
      var newDataLen = data.Length;
      if (newDataLen == rec.DataLength) {
        // Same size — overwrite data bytes in place. Fully byte-preserving.
        var buf = working.GetBuffer();
        data.CopyTo(buf.AsSpan(rec.DataOffset));
        // record table unchanged
        continue;
      }

      // Different size — tail-rewrite from this record's offset onward.
      var arr = working.ToArray();
      var rebuilt = RewriteTail(arr, version, records, idx, name, data, liveCount);
      working.Dispose();
      working = new MemoryStream();
      working.Write(rebuilt, 0, rebuilt.Length);
      if (!TryParse(rebuilt, out version, out records)) return false;
    }

    result = working.ToArray();
    working.Dispose();
    return true;
  }

  private static bool TryRemoveInPlace(byte[] image, string[] entryNames, out byte[] result) {
    result = image;
    if (!TryParse(image, out var version, out var records)) return false;

    var toRemove = new HashSet<string>(
      entryNames.Select(n => n.Replace('\\', '/').TrimStart('/')),
      StringComparer.OrdinalIgnoreCase);

    var firstHit = records.FindIndex(r =>
      toRemove.Contains(r.Name.Replace('\\', '/').TrimStart('/')));
    if (firstHit < 0) {
      // Nothing matched among real records — nothing to do (clean success).
      result = image;
      return true;
    }

    // Tail-rewrite from the first removed record onward, dropping every match.
    var surviving = new List<(string Name, byte[] Data)>();
    for (var i = firstHit; i < records.Count; i++) {
      var r = records[i];
      if (toRemove.Contains(r.Name.Replace('\\', '/').TrimStart('/'))) continue;
      surviving.Add((r.Name, image.AsSpan(r.DataOffset, r.DataLength).ToArray()));
    }

    var newCount = firstHit + surviving.Count;

    using var ms = new MemoryStream();
    ms.Write(image, 0, records[firstHit].Offset); // header + preceding records, byte-identical
    foreach (var (name, data) in surviving)
      AppendRecordBytes(ms, name, data);
    PatchCount(ms, (uint)newCount);
    result = ms.ToArray();
    return true;
  }

  // ── Helpers ────────────────────────────────────────────────────────

  /// <summary>Rewrites the tail of the image from record <paramref name="idx"/>
  /// onward, replacing that record's data with <paramref name="newData"/>.</summary>
  private static byte[] RewriteTail(byte[] image, uint version, List<Record> records,
      int idx, string name, byte[] newData, int liveCount) {
    using var ms = new MemoryStream();
    ms.Write(image, 0, records[idx].Offset); // header + preceding records
    AppendRecordBytes(ms, name, newData);     // replacement record
    for (var i = idx + 1; i < records.Count; i++) {
      var r = records[i];
      ms.Write(image, r.Offset, r.Length);    // trailing records, verbatim
    }
    PatchCount(ms, (uint)liveCount);
    return ms.ToArray();
  }

  private static void AppendRecord(MemoryStream working, string name, byte[] data) {
    AppendRecordBytes(working, name, data);
    PatchCount(working, ReadCount(working) + 1);
  }

  private static void AppendRecordBytes(MemoryStream ms, string name, byte[] data) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    Span<byte> u16 = stackalloc byte[2];
    Span<byte> u32 = stackalloc byte[4];
    var save = ms.Position;
    ms.Position = ms.Length;
    BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)nameBytes.Length);
    ms.Write(u16);
    ms.Write(nameBytes);
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)data.Length);
    ms.Write(u32);
    if (data.Length > 0) ms.Write(data);
    ms.Position = save;
  }

  private static uint ReadCount(MemoryStream ms) {
    var buf = ms.GetBuffer();
    return BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(CountOffset));
  }

  private static void PatchCount(MemoryStream ms, uint count) {
    var save = ms.Position;
    ms.Position = CountOffset;
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(u32, count);
    ms.Write(u32);
    ms.Position = save;
  }

  /// <summary>Lists the real (non-synthetic) file records — used for the rebuild fallback.</summary>
  public static IEnumerable<(string Name, byte[] Data)> ReadRealEntries(byte[] image) {
    if (!TryParse(image, out _, out var records))
      yield break;
    foreach (var r in records)
      if (!Synthetic.Contains(r.Name))
        yield return (r.Name, image.AsSpan(r.DataOffset, r.DataLength).ToArray());
  }
}
