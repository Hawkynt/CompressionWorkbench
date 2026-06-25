#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.AdvFs;

/// <summary>
/// Genuine in-place R/W mutation for AdvFS-WB images. The file table lives
/// inside RBMT page 0 (a single 8 KB page at <see cref="AdvFsReader.RbmtPageOffset"/>);
/// payloads sit in the flat append-only data area starting at
/// <see cref="AdvFsWriter.DataAreaOffset"/>. Every mutation rewrites only the
/// touched file-table rows + the appended/overwritten payload region, leaving
/// every other payload and table row byte-identical at its original offset.
/// </summary>
/// <remarks>
/// <para>On-disk anatomy (mirrors <see cref="AdvFsWriter"/> — do not drift):</para>
/// <list type="bullet">
///   <item>RBMT page 0 at byte 131072. 16-byte cookie, then DMN/VD/MATTR fields,
///     then a 64-byte volume tag, then the AdvFS-WB file table at page offset
///     <see cref="AdvFsWriter.FileTableOffsetInPage"/> (=132).</item>
///   <item>File table = 16-byte eyecatcher + u32 count + N rows of
///     {i64 offset, i64 length, u16 nameLen, name bytes}.</item>
///   <item>Payloads stored back-to-back from <see cref="AdvFsWriter.DataAreaOffset"/>
///     (=139264) to image end. Append-only / contiguous.</item>
///   <item><c>vdBlkCnt</c> (volume size in 512-byte sectors) sits at body offset
///     40 → absolute <see cref="VdBlkCntOffset"/>.</item>
/// </list>
/// <para><b>Add</b> appends the new payload at the current image end and appends a
/// new row inside the RBMT page (spare room in the 8 KB page), bumping the count.
/// <b>Replace</b> overwrites in place when the new payload fits the old slot
/// (zeroing trailing slack), else appends a fresh payload at image end (old bytes
/// become dead). <b>Remove</b> drops the row (shifting later rows up within the
/// page) and decrements the count. When a change can't be expressed in-place
/// (file table would overflow the page, or the image header is unparseable), the
/// caller's <c>rebuild</c> fallback takes over.</para>
/// </remarks>
internal static class AdvFsInPlaceModifier {

  // ── Layout constants (cross-checked with AdvFsWriter / AdvFsReader) ──
  private const long RbmtPageOffset = AdvFsReader.RbmtPageOffset;       // 131072
  private const int PageSize = AdvFsReader.PageSize;                    // 8192
  private const long DataAreaOffset = AdvFsWriter.DataAreaOffset;       // 139264
  private const int FileTableOffsetInPage = AdvFsWriter.FileTableOffsetInPage; // 132

  /// <summary>Absolute offset of the file-table eyecatcher inside the image.</summary>
  private static readonly long EyecatcherOffset = RbmtPageOffset + FileTableOffsetInPage;
  /// <summary>Absolute offset of the u32 file count (after the 16-byte eyecatcher).</summary>
  private static readonly long CountOffset = EyecatcherOffset + 16;
  /// <summary>Absolute offset where the first file-table row begins.</summary>
  private static readonly long RowsOffset = CountOffset + 4;
  /// <summary>Absolute offset of vdBlkCnt: cookie(16) + UUID(16)+mountId(8)+version(4)+vdIndex(4)+vdCount(4)+state(4) = 56 into the page.</summary>
  private static readonly long VdBlkCntOffset = RbmtPageOffset + 56;

  private static readonly byte[] Eyecatcher = AdvFsWriter.FileTableEyecatcher;

  // ── Public entry points ─────────────────────────────────────────────

  /// <summary>
  /// Adds or replaces files inside the AdvFS image in place. Falls back to
  /// <paramref name="rebuild"/> when the change can't be committed without
  /// re-packing (file table page overflow, header parse failure).
  /// </summary>
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

    if (!TryAddInPlace(ref image, payloads)) {
      rebuild(archive, inputs);
      return;
    }

    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }

  /// <summary>
  /// Removes the named entries from the file table in place. Falls back to
  /// <paramref name="rebuild"/> when the header can't be parsed.
  /// </summary>
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

    if (!TryRemoveInPlace(image, entryNames)) {
      rebuild(archive, entryNames);
      return;
    }

    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
  }

  // ── File-table model ─────────────────────────────────────────────────

  private sealed class Row {
    public required string Name;
    public required long Offset;
    public required long Length;
  }

  /// <summary>
  /// Parses the AdvFS-WB file table at its fixed offset. Returns false (forcing
  /// the rebuild fallback) on any sanity-check failure.
  /// </summary>
  private static bool TryReadTable(byte[] image, out List<Row> rows) {
    rows = [];
    if (image.LongLength < RowsOffset) return false;

    // Cookie + eyecatcher gate — only mutate writer-produced images in place.
    if (image.Length < RbmtPageOffset + AdvFsReader.DetectionCookie.Length) return false;
    if (!image.AsSpan((int)RbmtPageOffset, AdvFsReader.DetectionCookie.Length)
          .SequenceEqual(AdvFsReader.DetectionCookie)) return false;
    if (!image.AsSpan((int)EyecatcherOffset, Eyecatcher.Length).SequenceEqual(Eyecatcher)) return false;

    var count = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)CountOffset, 4));
    if (count > 4096) return false;

    var cursor = (int)RowsOffset;
    for (var i = 0; i < count; i++) {
      if (cursor + 18 > image.Length) return false;
      var offset = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(cursor, 8)); cursor += 8;
      var length = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(cursor, 8)); cursor += 8;
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(cursor, 2)); cursor += 2;
      if (cursor + nameLen > image.Length) return false;
      var name = Encoding.UTF8.GetString(image, cursor, nameLen);
      cursor += nameLen;
      if (offset < 0 || length < 0 || offset + length > image.LongLength) return false;
      rows.Add(new Row { Name = name, Offset = offset, Length = length });
    }
    return true;
  }

  /// <summary>Encoded byte length a row occupies in the file table.</summary>
  private static int RowSize(Row r) => 8 + 8 + 2 + Encoding.UTF8.GetByteCount(r.Name);

  /// <summary>Total bytes from page start to the end of the encoded table.</summary>
  private static bool TableFitsPage(List<Row> rows) {
    var used = (int)(RowsOffset - RbmtPageOffset);
    foreach (var r in rows) used += RowSize(r);
    return used <= PageSize;
  }

  /// <summary>Rewrites count + every row, zeroing the remaining table area of the page.</summary>
  private static void WriteTable(byte[] image, List<Row> rows) {
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)CountOffset, 4), (uint)rows.Count);
    var cursor = (int)RowsOffset;
    foreach (var r in rows) {
      var nameBytes = Encoding.UTF8.GetBytes(r.Name);
      BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(cursor, 8), r.Offset); cursor += 8;
      BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(cursor, 8), r.Length); cursor += 8;
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(cursor, 2), (ushort)nameBytes.Length); cursor += 2;
      nameBytes.CopyTo(image.AsSpan(cursor)); cursor += nameBytes.Length;
    }
    // Zero the rest of the page's table region so stale row bytes never re-parse.
    var pageEnd = (int)(RbmtPageOffset + PageSize);
    if (cursor < pageEnd) image.AsSpan(cursor, pageEnd - cursor).Clear();
  }

  /// <summary>Refreshes vdBlkCnt (volume size in 512-byte sectors) for the current image length.</summary>
  private static void UpdateVdBlkCnt(byte[] image) {
    if (VdBlkCntOffset + 8 > image.LongLength) return;
    var blkCnt = (ulong)((image.LongLength + 511) / 512);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan((int)VdBlkCntOffset, 8), blkCnt);
  }

  // ── Mutators ─────────────────────────────────────────────────────────

  private static bool TryAddInPlace(ref byte[] image, List<(string Name, byte[] Data)> payloads) {
    if (!TryReadTable(image, out var rows)) return false;

    foreach (var (name, data) in payloads) {
      var idx = rows.FindIndex(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
      if (idx >= 0) {
        if (!TryReplaceBytes(ref image, rows[idx], data)) return false;
      } else {
        // Append payload at current image end; append a fresh row.
        var newRow = new Row { Name = name, Offset = image.LongLength, Length = data.LongLength };
        var candidate = new List<Row>(rows) { newRow };
        if (!TableFitsPage(candidate)) return false;   // page overflow → rebuild
        AppendPayload(ref image, data);
        rows.Add(newRow);
      }
    }

    WriteTable(image, rows);
    UpdateVdBlkCnt(image);
    return true;
  }

  private static bool TryRemoveInPlace(byte[] image, string[] entryNames) {
    if (!TryReadTable(image, out var rows)) return false;

    var toRemove = new HashSet<string>(
      entryNames.Select(n => n.Replace('\\', '/').TrimStart('/')), StringComparer.OrdinalIgnoreCase);

    var kept = rows.Where(r =>
      !toRemove.Contains(r.Name.Replace('\\', '/').TrimStart('/'))).ToList();

    if (kept.Count == rows.Count) {
      // Nothing matched. If a requested name isn't a known file at all, let the
      // rebuild path try (it might be a synthetic/header name).
      return false;
    }

    // Optionally wipe the freed payload bytes (forensic cleanliness); only safe
    // when the slot isn't shared. Payloads are contiguous and unique per row.
    foreach (var r in rows) {
      if (kept.Contains(r)) continue;
      if (r.Length > 0 && r.Offset >= DataAreaOffset && r.Offset + r.Length <= image.LongLength)
        image.AsSpan((int)r.Offset, (int)r.Length).Clear();
    }

    WriteTable(image, kept);
    return true;
  }

  /// <summary>
  /// Replaces a row's payload. Overwrites in place when the new bytes fit the
  /// old slot length (zeroing trailing slack); otherwise appends a fresh payload
  /// at image end and re-points the row (old bytes become dead space).
  /// </summary>
  private static bool TryReplaceBytes(ref byte[] image, Row row, byte[] data) {
    if (data.LongLength <= row.Length) {
      if (row.Length > 0 && row.Offset >= DataAreaOffset && row.Offset + row.Length <= image.LongLength) {
        var span = image.AsSpan((int)row.Offset, (int)row.Length);
        span.Clear();
        if (data.Length > 0) data.CopyTo(span);
      } else if (data.Length > 0) {
        // No old slot to reuse — append fresh.
        row.Offset = image.LongLength;
        AppendPayload(ref image, data);
      }
      row.Length = data.LongLength;
      return true;
    }

    // Larger — append fresh run at image end; old payload becomes dead space.
    row.Offset = image.LongLength;
    row.Length = data.LongLength;
    AppendPayload(ref image, data);
    return true;
  }

  /// <summary>Grows the image and copies <paramref name="data"/> at the old end.</summary>
  private static void AppendPayload(ref byte[] image, byte[] data) {
    if (data.Length == 0) return;
    var oldLen = image.Length;
    Array.Resize(ref image, oldLen + data.Length);
    data.CopyTo(image, oldLen);
  }
}
