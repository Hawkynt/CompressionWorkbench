using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Refs;

/// <summary>
/// Creates an empty directory the way Windows Server 2022 (ReFS 3.7) and 2025 (ReFS 3.14) do,
/// measured by having Windows create one:
/// <list type="bullet">
/// <item>a new object (the next object number past the highest directory object) with its own
/// table: one MSB+ leaf page holding only the directory header row (type 0x10), the page placed
/// in a free run of the Medium Allocator row that holds an existing directory's page, and the
/// table's row count in the page's root descriptor at 1;</item>
/// <item>the header row: the record of an existing directory with new timestamps and the
/// embedded name record (see <see cref="LocateNameRecord"/>) carrying the parent's object
/// number and the new name; on ReFS 3.7 also the attributes of the template's name row
/// (0x10000000, plus 0x8000 for an integrity directory) at value+0x48 and its last file
/// number (value+0x80) at 1;</item>
/// <item>an object-table row (root 0): the existing directory's row with the page reference
/// (four virtual LCNs and its CRC64) pointing at the new page and the file-number counter
/// (+0xC8 on 3.7, +0x50 on 3.14) at 1;</item>
/// <item>a name row (type 0x30, kind 2) in the parent: [u64 0, object number, four
/// timestamps, zero bytes, the attributes u32 at +0x40 (0x10000000, | 0x8000 with
/// integrity), ...] — 72 bytes on 3.7, 84 on 3.14.</item>
/// </list>
/// Every page-reference checksum from the changed pages up to the checkpoint is then refreshed.
/// </summary>
/// <remarks>
/// Refused before the first write: a name that exists, a parent deeper than a root over leaves
/// or whose leaf has no room (a page split is not made), a
/// volume without a directory whose page and records match these layouts (it is the template),
/// and no room or no free run.
/// </remarks>
internal static class RefsInPlaceMkdir {
  private const int HeaderInnerNode = 0xA8;       // the embedded node inside a directory header value
  private const uint Hidden = 0x2;
  private const uint SystemFile = 0x4;
  /// <summary>Read-only, hidden, system and archive: attribute bits a new directory does not take from its template.</summary>
  private const uint PlainDirectoryMask = 0x1 | Hidden | SystemFile | 0x20;

  public static void Create(Stream image, string path) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanSeek || !image.CanWrite) throw new IOException("ReFS: the image must be seekable and writable.");
    var normalized = path.Replace('\\', '/').Trim('/');
    var parent = normalized.Contains('/') ? normalized[..normalized.LastIndexOf('/')] : "";
    var name = normalized[(normalized.LastIndexOf('/') + 1)..];
    if (name.Length is 0 or > 255 || name.IndexOfAny(['\0', '/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name is "." or "..")
      throw new ArgumentException($"ReFS: '{name}' is not a valid directory name.", nameof(path));

    var metadata = RefsMetadataReader.Open(image);
    var legacy = metadata.Header.MinorVersion < 14;
    var (parentOid, parentRoot) = RefsInPlaceRename.ResolveDirectory(metadata, parent);
    var tree = RefsDirectoryTree.Open(metadata, parentRoot);
    var rows = tree.Rows;
    var collation = RefsDirectoryCollation.For(metadata, rows, name);
    var nameKey = new byte[4 + name.Length * 2];
    BinaryPrimitives.WriteUInt16LittleEndian(nameKey, 0x30);
    BinaryPrimitives.WriteUInt16LittleEndian(nameKey.AsSpan(2), 2);
    Encoding.Unicode.GetBytes(name).CopyTo(nameKey, 4);
    if (rows.Any(r => BinaryPrimitives.ReadUInt16LittleEndian(r.Key) == 0x30 && collation.Compare(r.Key, nameKey) == 0))
      throw new IOException($"ReFS: '{normalized}' already exists.");

    // Template: an existing directory (a child of the parent preferred), its name row, object row and table page.
    var template = FindTemplate(metadata, parentOid)
      ?? throw new NotSupportedException("ReFS: the volume holds no directory whose records match the layout this editor writes.");
    var counterOffset = RefsDirectoryRows.CounterOffset(template.ObjectValue)!.Value;
    var objectRows = metadata.WalkRoot(0).ToList();
    var objectPage = objectRows[0].PhysicalPageLcn;
    if (objectRows.Any(r => r.PhysicalPageLcn != objectPage))
      throw new NotSupportedException("ReFS: the object table spans more than one page.");
    var oid = objectRows.Select(static r => BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(8))).Where(static o => o >= 0x700).DefaultIfEmpty(0x700UL).Max() + 1;

    // The page: clusters, allocation, bytes.
    var graph = new RefsMetadataGraph(image, metadata);
    var allocation = RefsMetadataPageAllocation.Plan(metadata, graph, template.PagePhysical);
    var virtualLcns = allocation.Virtual;

    var now = (ulong)DateTime.UtcNow.ToFileTimeUtc();
    var header = RewriteNameRecord(template.HeaderValue, template.NameRecord, name, parentOid);
    for (var o = 0x28; o < 0x48; o += 8) BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(o), now);
    // A new directory is a plain one: the template's read-only, hidden, system and archive bits
    // are not passed on (System Volume Information is the only template a fresh volume has), and
    // a hidden or system template's security descriptor gives way to the parent's (header +0x50).
    var templateAttributes = BinaryPrimitives.ReadUInt32LittleEndian(template.NameValue.AsSpan(0x40));
    var attributes = templateAttributes & ~PlainDirectoryMask;
    if ((templateAttributes & (Hidden | SystemFile)) != 0
        && rows.FirstOrDefault(static r => r.Key.Length == 4 && BinaryPrimitives.ReadUInt32LittleEndian(r.Key) == 0x10) is { } parentHeader
        && parentHeader.Value.Length >= 0x58)
      parentHeader.Value.AsSpan(0x50, 8).CopyTo(header.AsSpan(0x50));
    if (legacy) {
      BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x48), attributes);
      BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x80), 1);
    } else {
      BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x48), BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x48)) & ~PlainDirectoryMask);
    }
    var page = BuildPage(template.Page, template.HeaderRowOffset, header, virtualLcns, oid);

    var objectValue = template.ObjectValue.ToArray();
    for (var i = 0; i < 4; ++i)
      BinaryPrimitives.WriteUInt64LittleEndian(objectValue.AsSpan(0x20 + i * 8), i < virtualLcns.Count ? virtualLcns[i] : 0);
    RefsChecksum.RefreshPageReference(objectValue.AsSpan(0x20, checked((int)metadata.PageReferenceSize)), page);
    BinaryPrimitives.WriteUInt64LittleEndian(objectValue.AsSpan(counterOffset), 1);
    var objectKey = template.ObjectKey.ToArray();
    BinaryPrimitives.WriteUInt64LittleEndian(objectKey.AsSpan(8), oid);

    var nameValue = template.NameValue.ToArray();
    BinaryPrimitives.WriteUInt64LittleEndian(nameValue.AsSpan(0x08), oid);
    BinaryPrimitives.WriteUInt32LittleEndian(nameValue.AsSpan(0x40), attributes);
    for (var o = 0x10; o < 0x30; o += 8) BinaryPrimitives.WriteUInt64LittleEndian(nameValue.AsSpan(o), now);

    var parentPage = tree.LeafFor(nameKey, collation);
    var namePosition = tree.PositionIn(parentPage, nameKey, collation);
    var objectPosition = objectRows.Count(r => BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(8)) < oid);
    if (!RefsPageEditor.CanInsertRow(graph, parentPage, namePosition, nameKey.Length, nameValue.Length)
        || !RefsPageEditor.CanInsertRow(graph, objectPage, objectPosition, objectKey.Length, objectValue.Length))
      throw new NotSupportedException("ReFS: the parent directory or the object table has no room for the new rows.");

    allocation.Claim(image, metadata, page);
    RefsPageEditor.InsertRow(graph, objectPage, objectPosition, objectKey, objectValue, template.ObjectFlags);
    RefsPageEditor.InsertRow(graph, parentPage, namePosition, nameKey, nameValue, template.NameFlags);
    tree.AdjustRowCount(graph, +1);
    // The object-table page was repacked, so the offsets of the page references it holds moved:
    // the checksums are refreshed through a graph built from the volume as it now is.
    new RefsMetadataGraph(image, RefsMetadataReader.Open(image)).RefreshChecksumPaths([parentPage, tree.RootPage, objectPage]);
  }

  private sealed record Template(
    byte[] NameValue, ushort NameFlags,
    byte[] ObjectKey, byte[] ObjectValue, ushort ObjectFlags,
    byte[] Page, IReadOnlyList<ulong> PagePhysical, int HeaderRowOffset, byte[] HeaderValue, int NameRecord);

  private static Template? FindTemplate(RefsMetadataReader metadata, ulong parentOid) {
    var objects = metadata.WalkRoot(0).ToDictionary(static r => BinaryPrimitives.ReadUInt64LittleEndian(r.Key.AsSpan(8)));
    var referenceEnd = 0x20 + (int)metadata.PageReferenceSize;
    foreach (var (dirOid, dirObject) in objects.OrderBy(o => o.Key == parentOid ? 0 : 1).ThenBy(static o => o.Key)) {
      if (dirOid < 0x600 || dirObject.Value.Length < referenceEnd) continue;
      var root = RefsPageReference.Parse(dirObject.Value.AsSpan(0x20));
      List<RefsBTreeRow> rows;
      try { rows = metadata.WalkTree(root, virtualAddresses: true).ToList(); } catch (InvalidDataException) { continue; }
      foreach (var row in rows) {
        if (row.Key.Length <= 4 || BinaryPrimitives.ReadUInt16LittleEndian(row.Key) != 0x30 || BinaryPrimitives.ReadUInt16LittleEndian(row.Key.AsSpan(2)) != 2) continue;
        if (row.Value.Length is not (72 or 84) || (BinaryPrimitives.ReadUInt32LittleEndian(row.Value.AsSpan(0x40)) & 0x10000000) == 0) continue;
        var childOid = BinaryPrimitives.ReadUInt64LittleEndian(row.Value.AsSpan(0x08));
        if (!objects.TryGetValue(childOid, out var childObject) || RefsDirectoryRows.CounterOffset(childObject.Value) is not { } trailer || trailer < referenceEnd) continue;
        var childRoot = RefsPageReference.Parse(childObject.Value.AsSpan(0x20));
        List<RefsBTreeRow> childRows;
        try { childRows = metadata.WalkTree(childRoot, virtualAddresses: true).ToList(); } catch (InvalidDataException) { continue; }
        var headerRow = childRows.FirstOrDefault(static r => r.Key.Length == 4 && BinaryPrimitives.ReadUInt32LittleEndian(r.Key) == 0x10);
        if (headerRow is null) continue;
        var record = LocateNameRecord(headerRow.Value, Encoding.Unicode.GetString(row.Key, 4, row.Key.Length - 4), dirOid);
        if (record is null) continue;
        var physical = childRoot.Lcns.Select(metadata.TranslateVirtualLcn).ToList();
        var page = metadata.TryReadReferencedPage(childRoot, virtualAddresses: true);
        if (page is null) continue;
        var headerOffset = (int)(headerRow.AbsoluteRowOffset - (long)physical[0] * metadata.ClusterSize);
        if (headerOffset < 0x80 || headerOffset >= page.Length) continue;
        return new Template(row.Value, row.Flags, childObject.Key, childObject.Value, childObject.Flags, page, physical, headerOffset, headerRow.Value, record.Value);
      }
    }
    return null;
  }

  /// <summary>
  /// Finds the name record embedded in a directory header value. The header holds a node at
  /// value+0xA8 ([data start, data end, free, flags, index start, count, …, index end at
  /// +0x20], the index running to the end of the value, 0xFFFF-tagged offsets) with two rows:
  /// a 0xB8-byte one and the name record [record size u32 = align8(0x30 + n), 0x10 u16,
  /// 0x20 + n u16, u16 0, 0x18 u16, 0x18 + n u32, 0x18 + n u32, u32 0, 0x80000002,
  /// 0x000D0039, the parent's object number u64, u64 0, the UTF-16 name of n bytes]. ReFS 3.7
  /// stores the name record after the other row with free space behind it (value+0x188, 640
  /// bytes in all); ReFS 3.14 stores it first, at value+0xD0, with no free space — measured on
  /// directories Windows created.
  /// </summary>
  private static int? LocateNameRecord(byte[] header, string name, ulong parentOid) {
    const int inner = HeaderInnerNode;
    if (header.Length < inner + 0x28) return null;
    var dataEnd = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(inner + 4));
    var free = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(inner + 8));
    var indexStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(inner + 0x10));
    var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(inner + 0x14));
    var indexEnd = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(inner + 0x20));
    if (count != 2 || inner + indexEnd != header.Length || indexStart + count * 4 != indexEnd || dataEnd + free != indexStart) return null;
    var n = Encoding.Unicode.GetByteCount(name);
    var nameBytes = Encoding.Unicode.GetBytes(name);
    for (var p = inner + indexStart; p < inner + indexEnd; p += 4) {
      var entry = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(p));
      var r = inner + (int)(entry & 0xFFFF);
      if ((entry & 0xFFFF0000u) != 0xFFFF0000u || r + 0x30 + n > inner + dataEnd) continue;
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(r));
      if (size == ((0x30 + n + 7) & ~7)
          && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(r + 6)) == 0x20 + n
          && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(r + 0x0C)) == (uint)(0x18 + n)
          && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(r + 0x10)) == (uint)(0x18 + n)
          && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(r + 0x18)) == 0x80000002
          && BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(r + 0x20)) == parentOid
          && BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(r + 0x28)) == 0
          && header.AsSpan(r + 0x30, n).SequenceEqual(nameBytes)
          && r + size <= inner + dataEnd)
        return r;
    }
    return null;
  }

  /// <summary>
  /// Rewrites the name record at <paramref name="record"/> for a new name and parent. Rows behind
  /// it move by the size change and their index entries with them. Where the node keeps free
  /// space (ReFS 3.7) the change comes out of it and the value keeps its length; where it keeps
  /// none (ReFS 3.14) the value grows or shrinks by the change, as Windows does.
  /// </summary>
  private static byte[] RewriteNameRecord(byte[] header, int record, string name, ulong parentOid) {
    const int inner = HeaderInnerNode;
    var oldSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(record));
    var nameBytes = Encoding.Unicode.GetBytes(name);
    var n = nameBytes.Length;
    var newSize = (0x30 + n + 7) & ~7;
    var delta = newSize - oldSize;
    var dataEnd = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(inner + 4));
    var free = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(inner + 8));
    var indexStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(inner + 0x10));
    var indexEnd = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(inner + 0x20));
    var grows = free == 0;
    if (!grows && delta > free) throw new NotSupportedException("ReFS: the directory name does not fit the header record.");

    var recordBytes = new byte[newSize];
    header.AsSpan(record, 0x30).CopyTo(recordBytes);
    BinaryPrimitives.WriteUInt32LittleEndian(recordBytes, (uint)newSize);
    BinaryPrimitives.WriteUInt16LittleEndian(recordBytes.AsSpan(6), (ushort)(0x20 + n));
    BinaryPrimitives.WriteUInt32LittleEndian(recordBytes.AsSpan(0x0C), (uint)(0x18 + n));
    BinaryPrimitives.WriteUInt32LittleEndian(recordBytes.AsSpan(0x10), (uint)(0x18 + n));
    BinaryPrimitives.WriteUInt64LittleEndian(recordBytes.AsSpan(0x20), parentOid);
    BinaryPrimitives.WriteUInt64LittleEndian(recordBytes.AsSpan(0x28), 0);
    nameBytes.CopyTo(recordBytes, 0x30);

    byte[] result;
    if (grows) {
      result = new byte[header.Length + delta];
      header.AsSpan(0, record).CopyTo(result);
      recordBytes.CopyTo(result, record);
      header.AsSpan(record + oldSize).CopyTo(result.AsSpan(record + newSize));
      indexStart += delta;
      indexEnd += delta;
    } else {
      result = header.ToArray();
      var tail = header.AsSpan(record + oldSize, inner + dataEnd - (record + oldSize)).ToArray();
      result.AsSpan(record, inner + indexStart - record).Clear();
      recordBytes.CopyTo(result, record);
      tail.CopyTo(result, record + newSize);
      free -= delta;
    }
    dataEnd += delta;
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(inner + 4), (uint)dataEnd);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(inner + 8), (uint)free);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(inner + 0x10), (uint)indexStart);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(inner + 0x20), (uint)indexEnd);
    for (var p = inner + indexStart; p < inner + indexEnd; p += 4) {
      var entry = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(p));
      if (inner + (int)(entry & 0xFFFF) > record)
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(p), entry + (uint)delta);
    }
    return result;
  }

  /// <summary>
  /// The template page with its self LCNs and table number replaced, its leaf holding only the
  /// header row and the table's row count (root descriptor, page+0x70) at 1.
  /// </summary>
  private static byte[] BuildPage(byte[] template, int headerRowOffset, byte[] headerValue, IReadOnlyList<ulong> virtualLcns, ulong oid) {
    var page = template.ToArray();
    for (var i = 0; i < 4; ++i) BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(0x20 + i * 8), i < virtualLcns.Count ? virtualLcns[i] : 0);
    BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(0x48), oid);
    var node = 0x50 + (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(0x50));
    var dataStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(node));
    var indexEnd = (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(node + 0x20));
    var templateCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(node + 0x14));
    var keyOffset = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(headerRowOffset + 4));
    var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(headerRowOffset + 6));
    var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(headerRowOffset + 10));
    if (valueOffset < keyOffset + keyLength) throw new InvalidDataException("ReFS: the template header row is malformed.");
    var rowSize = (valueOffset + headerValue.Length + 7) & ~7;
    var row = new byte[rowSize];
    page.AsSpan(headerRowOffset, valueOffset).CopyTo(row);
    BinaryPrimitives.WriteUInt32LittleEndian(row, (uint)rowSize);
    BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(12), checked((ushort)headerValue.Length));
    headerValue.CopyTo(row, valueOffset);
    // Index tag of the template's header entry, kept as found.
    var indexStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(node + 0x10));
    uint tag = 0xFFFF0000u;
    for (var p = node + indexStart; p < node + indexEnd; p += 4) {
      var entry = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(p));
      if (node + (int)(entry & 0xFFFF) == headerRowOffset) tag = entry & 0xFFFF0000u;
    }
    page.AsSpan(node + dataStart, indexEnd - dataStart).Clear();
    row.CopyTo(page, node + dataStart);
    var dataEnd = dataStart + rowSize;
    var newIndexStart = indexEnd - 4;
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(node + 4), (uint)dataEnd);
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(node + 8), (uint)(newIndexStart - dataEnd));
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(node + 0x10), (uint)newIndexStart);
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(node + 0x14), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(node + newIndexStart), (uint)dataStart | tag);
    RefsPageEditor.UpdateTableRowCount(page, templateCount, 1);
    return page;
  }
}
