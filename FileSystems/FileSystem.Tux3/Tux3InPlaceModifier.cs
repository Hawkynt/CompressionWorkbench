#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Tux3;

/// <summary>
/// Genuine in-place R/W mutation for TUX3 single-version WORM images. The
/// on-disk surface is block 0 boot (zeroed 4096), block 1 superblock
/// (<c>"TUX3SUPR"</c> magic at offset 4096), block 2 the WORM file table
/// (<c>"TUX3WORM"</c> magic at offset 8192, <c>u32 count</c> at 8200, then
/// back-to-back records: <c>u16 nameLen</c>, name, <c>u32 dataLen</c>, data).
/// </summary>
/// <remarks>
/// <para>Same inline-record strategy as TUX2, with two extra invariants kept
/// intact: the image stays padded to a whole 4096-byte block, and the
/// superblock's <c>vol_blocks</c> field (offset 4096 + 0x38) is refreshed to
/// <c>imageLen / 4096</c> whenever the image length changes.</para>
/// <para>Byte-preservation guarantees:</para>
/// <list type="bullet">
///   <item><b>Add (new name)</b> — appends a record after the last existing one
///     and bumps the table count; the boot block, superblock block, table
///     header, and every prior record stay byte-identical. (When the image was
///     block-padded, the new record overwrites trailing zero padding, then we
///     re-pad and refresh <c>vol_blocks</c>.)</item>
///   <item><b>Replace, same size</b> — overwrites the matched record's data in
///     place; every other byte stays identical.</item>
///   <item><b>Replace, different size</b> and <b>Remove</b> — tail-rewrite from
///     the changed record's offset onward; boot+superblock+table-header+all
///     preceding records stay byte-identical.</item>
/// </list>
/// </remarks>
internal static class Tux3InPlaceModifier {

  private const int BlockSize = 4096;
  private const int SuperblockOffset = 4096;
  private const int VolBlocksOffset = SuperblockOffset + 0x38;
  private const int FreeBlocksOffset = SuperblockOffset + 0x40;
  private const int TableOffset = 8192;
  private const int CountOffset = TableOffset + 8;
  private const int FirstRecordOffset = TableOffset + 12;

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

  private readonly record struct Record(string Name, int Offset, int Length, int DataOffset, int DataLength);

  private static bool TryParse(byte[] image, out List<Record> records, out int tableEnd) {
    records = [];
    tableEnd = FirstRecordOffset;
    if (image.Length < FirstRecordOffset) return false;
    if (!image.AsSpan(SuperblockOffset, 8).SequenceEqual(Tux3Reader.Magic)) return false;
    if (!image.AsSpan(TableOffset, 8).SequenceEqual(Tux3Reader.WormTableMagic)) return false;

    var declared = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(CountOffset));
    var pos = FirstRecordOffset;
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
    tableEnd = pos;
    return count == declared;
  }

  private static bool TryAddInPlace(byte[] image, List<(string Name, byte[] Data)> payloads, out byte[] result) {
    result = image;
    if (!TryParse(image, out var records, out var tableEnd)) return false;

    foreach (var (name, _) in payloads)
      if (name.Contains('/') || name.Contains('\\'))
        return false;

    // Build the working buffer trimmed to the live table end (drops block
    // padding so appends land contiguously); re-pad + refresh vol_blocks at the
    // end. Boot/superblock/table-header and preceding records are preserved.
    var head = image.AsSpan(0, tableEnd).ToArray();
    using var ms = new MemoryStream();
    ms.Write(head, 0, head.Length);

    var count = (uint)records.Count;

    foreach (var (name, data) in payloads) {
      var idx = records.FindIndex(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
      if (idx < 0) {
        AppendRecordBytes(ms, name, data);
        count++;
        continue;
      }

      var rec = records[idx];
      if (data.Length == rec.DataLength) {
        // Same size — patch data in place inside the already-written head copy.
        var buf = ms.GetBuffer();
        data.CopyTo(buf.AsSpan(rec.DataOffset));
        continue;
      }

      // Different size — rebuild the whole table region from scratch with the
      // current records, applying the replacement. Simpler and still O(tail).
      var rebuilt = RebuildTable(image, records, replaceIdx: idx, replaceName: name, replaceData: data,
        removeIdx: -1);
      ms.SetLength(0);
      ms.Write(rebuilt, 0, rebuilt.Length);
      // Re-parse so subsequent payloads see updated offsets/count.
      var snap = ms.ToArray();
      if (!TryParse(snap, out records, out _)) return false;
      count = (uint)records.Count;
    }

    PatchCount(ms, count);
    result = Finalize(ms.ToArray());
    return true;
  }

  private static bool TryRemoveInPlace(byte[] image, string[] entryNames, out byte[] result) {
    result = image;
    if (!TryParse(image, out var records, out _)) return false;

    var toRemove = new HashSet<string>(
      entryNames.Select(n => n.Replace('\\', '/').TrimStart('/')),
      StringComparer.OrdinalIgnoreCase);

    var anyHit = records.Any(r => toRemove.Contains(r.Name.Replace('\\', '/').TrimStart('/')));
    if (!anyHit) { result = image; return true; }

    var surviving = new List<(string Name, byte[] Data)>();
    foreach (var r in records) {
      if (toRemove.Contains(r.Name.Replace('\\', '/').TrimStart('/'))) continue;
      surviving.Add((r.Name, image.AsSpan(r.DataOffset, r.DataLength).ToArray()));
    }

    using var ms = new MemoryStream();
    ms.Write(image, 0, FirstRecordOffset); // boot + superblock + table header, byte-identical
    foreach (var (name, data) in surviving)
      AppendRecordBytes(ms, name, data);
    PatchCount(ms, (uint)surviving.Count);
    result = Finalize(ms.ToArray());
    return true;
  }

  // ── Helpers ────────────────────────────────────────────────────────

  /// <summary>Rebuilds the whole table region preserving boot+superblock+header,
  /// applying an optional replace (by index) and/or skipping a removed index.</summary>
  private static byte[] RebuildTable(byte[] image, List<Record> records,
      int replaceIdx, string? replaceName, byte[]? replaceData, int removeIdx) {
    using var ms = new MemoryStream();
    ms.Write(image, 0, FirstRecordOffset);
    var count = 0;
    for (var i = 0; i < records.Count; i++) {
      if (i == removeIdx) continue;
      if (i == replaceIdx) {
        AppendRecordBytes(ms, replaceName!, replaceData!);
      } else {
        var r = records[i];
        AppendRecordBytes(ms, r.Name, image.AsSpan(r.DataOffset, r.DataLength).ToArray());
      }
      count++;
    }
    PatchCount(ms, (uint)count);
    return ms.ToArray();
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

  private static void PatchCount(MemoryStream ms, uint count) {
    var save = ms.Position;
    ms.Position = CountOffset;
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(u32, count);
    ms.Write(u32);
    ms.Position = save;
  }

  /// <summary>Pads the image up to a whole 4096-byte block and refreshes the
  /// superblock's <c>vol_blocks</c> field. <c>free_blocks</c> is left at 0
  /// (matching the writer's WORM accounting).</summary>
  private static byte[] Finalize(byte[] image) {
    var len = image.Length;
    var pad = (int)(((long)BlockSize - (len % BlockSize)) % BlockSize);
    if (pad > 0) {
      var grown = new byte[len + pad];
      Array.Copy(image, grown, len);
      image = grown;
    }
    var volBlocks = (ulong)(image.Length / BlockSize);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(VolBlocksOffset), volBlocks);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(FreeBlocksOffset), 0UL);
    return image;
  }

  /// <summary>Lists the real file records — used for the rebuild fallback.</summary>
  public static IEnumerable<(string Name, byte[] Data)> ReadRealEntries(byte[] image) {
    if (!TryParse(image, out var records, out _))
      yield break;
    foreach (var r in records)
      yield return (r.Name, image.AsSpan(r.DataOffset, r.DataLength).ToArray());
  }
}
