#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.NetAssembly;

/// <summary>
/// Reads a .NET assembly PE file and surfaces its CLI metadata content as an archive:
/// <list type="bullet">
///   <item>Each CLI metadata stream (<c>#~</c>, <c>#Strings</c>, <c>#Blob</c>, <c>#GUID</c>, <c>#US</c>) → <c>streams/{name}.bin</c>.</item>
///   <item>Each embedded resource (from the <c>ManifestResource</c> metadata table, best-effort parse of the <c>#~</c> stream) → <c>resources/{name}.bin</c>.</item>
///   <item>A synthetic <c>references.txt</c> listing <c>AssemblyRef</c> entries (name + version).</item>
/// </list>
/// <para>
/// This reader handles the outer PE → CLI header → metadata-root → stream-directory walk
/// cleanly and extracts the raw stream bytes. The <c>#~</c> tables are parsed only deeply
/// enough to resolve <c>ManifestResource</c> and <c>AssemblyRef</c> rows; full CLI metadata
/// decoding (signatures, typerefs, etc.) is intentionally out of scope.
/// </para>
/// </summary>
public sealed class NetAssemblyReader {

  /// <summary>One entry surfaced from a .NET assembly.</summary>
  public sealed record Entry(string Name, byte[] Data);

  public List<Entry> ReadAll(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

    stream.Position = 0;
    var all = new byte[stream.Length];
    stream.ReadExactly(all, 0, all.Length);
    if (all.Length < 0x40 || all[0] != 'M' || all[1] != 'Z')
      throw new InvalidDataException("Not a PE file (missing 'MZ').");

    var peOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(0x3C));
    if (peOff + 24 > all.Length || all[peOff] != 'P' || all[peOff + 1] != 'E')
      throw new InvalidDataException("Not a PE file (missing 'PE').");

    // COFF header: Machine(2) NumSections(2) Timestamp(4) SymTabPtr(4) NumSyms(4) OptHdrSize(2) Characteristics(2)
    var numSections   = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(peOff + 4 + 2));
    var optHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(peOff + 4 + 16));
    if (peOff + 4 + 20 + optHeaderSize > all.Length)
      throw new InvalidDataException("Truncated PE optional header.");

    var optHdrOff = peOff + 4 + 20;
    var pe32Plus  = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(optHdrOff)) == 0x020B;
    // Data directories: 96 bytes in (PE32) or 112 bytes in (PE32+); CLR header is index 14 (zero-based).
    var dataDirStart = optHdrOff + (pe32Plus ? 112 : 96);
    var clrDirOff = dataDirStart + 14 * 8;
    if (clrDirOff + 8 > all.Length)
      return []; // no CLR directory entry present.
    var clrRva  = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(clrDirOff));
    var clrSize = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(clrDirOff + 4));
    if (clrRva == 0 || clrSize == 0)
      return []; // plain native PE.

    // Sections table follows the optional header; used to map RVA → file offset.
    var secTableOff = optHdrOff + optHeaderSize;
    var sections = new Section[numSections];
    for (var i = 0; i < numSections; i++) {
      var s = secTableOff + i * 40;
      if (s + 40 > all.Length) throw new InvalidDataException("Truncated section header.");
      sections[i] = new Section(
        Va: BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(s + 12)),
        VSize: BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(s + 8)),
        Raw: BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(s + 20)),
        RawSize: BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(s + 16)));
    }

    long clrOff = RvaToFileOffset(sections, clrRva);
    if (clrOff < 0 || clrOff + 72 > all.Length)
      throw new InvalidDataException("CLI header extends past EOF.");

    // IMAGE_COR20_HEADER (72 bytes): cb(4) MajorRt(2) MinorRt(2) MetadataDir(8) Flags(4) EntryPoint(4) ResourcesDir(8) ...
    var metaRva  = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan((int)clrOff + 8));
    var metaSize = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan((int)clrOff + 12));
    var rsrcRva  = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan((int)clrOff + 24));
    var rsrcSize = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan((int)clrOff + 28));

    if (metaRva == 0 || metaSize == 0) return [];
    var metaOff = RvaToFileOffset(sections, metaRva);
    if (metaOff < 0 || metaOff + metaSize > all.Length)
      throw new InvalidDataException("Metadata root extends past EOF.");

    var entries = new List<Entry>();
    ParseMetadataStreams(all, metaOff, metaSize, entries, out var streams);

    // Walk #~ table stream for ManifestResource / AssemblyRef rows (best-effort).
    if (streams.TryGetValue("#~", out var tildeOff) || streams.TryGetValue("#-", out tildeOff)) {
      try {
        ParseTildeStream(all, tildeOff.Offset, tildeOff.Size, streams, entries,
          metaOff, rsrcRva, rsrcSize, sections);
      } catch {
        // Swallow metadata-decoding errors; raw streams have already been emitted.
      }
    }

    return entries;
  }

  private readonly record struct Section(uint Va, uint VSize, uint Raw, uint RawSize) {
    public bool Contains(uint rva) => rva >= this.Va && rva < this.Va + Math.Max(this.VSize, this.RawSize);
  }

  private static long RvaToFileOffset(Section[] sections, uint rva) {
    foreach (var s in sections)
      if (s.Contains(rva))
        return s.Raw + (rva - s.Va);
    return -1;
  }

  private static void ParseMetadataStreams(
    byte[] all, long metaOff, uint metaSize,
    List<Entry> entries,
    out Dictionary<string, (long Offset, int Size)> streams) {

    streams = new Dictionary<string, (long, int)>(StringComparer.Ordinal);
    // Metadata root: Signature(4) MajorVer(2) MinorVer(2) Reserved(4) VersionStringLen(4) VersionString(padded to 4) Flags(2) StreamCount(2)
    var sig = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan((int)metaOff));
    if (sig != 0x424A5342) return; // "BSJB"
    var versionLen = BinaryPrimitives.ReadInt32LittleEndian(all.AsSpan((int)metaOff + 12));
    if (versionLen < 0 || versionLen > 255) return;
    var pad = (versionLen + 3) & ~3;
    var cursor = (int)metaOff + 16 + pad;
    if (cursor + 4 > metaOff + metaSize) return;
    // Flags (2) then StreamCount (2)
    var streamCount = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(cursor + 2));
    cursor += 4;
    for (var i = 0; i < streamCount; i++) {
      if (cursor + 8 > metaOff + metaSize) break;
      var relOff = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(cursor));
      var size   = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(cursor + 4));
      cursor += 8;
      // Stream name is null-terminated, padded to a 4-byte boundary (max 32 bytes).
      var nameStart = cursor;
      var end = nameStart;
      while (end < metaOff + metaSize && all[end] != 0) end++;
      var name = Encoding.ASCII.GetString(all, nameStart, end - nameStart);
      var nameLenWithNull = end - nameStart + 1;
      cursor += (nameLenWithNull + 3) & ~3;
      var streamOff = metaOff + relOff;
      if (streamOff + size > all.Length) continue;
      streams[name] = (streamOff, (int)size);
      var safe = SanitizeName(name.TrimStart('#'));
      if (string.IsNullOrEmpty(safe)) safe = $"stream_{i}";
      entries.Add(new Entry($"streams/{safe}.bin", all.AsSpan((int)streamOff, (int)size).ToArray()));
    }
  }

  private static void ParseTildeStream(
    byte[] all, long tildeOff, int tildeSize,
    Dictionary<string, (long Offset, int Size)> streams,
    List<Entry> entries,
    long metaRoot, uint rsrcRva, uint rsrcSize, Section[] sections) {

    // Tables-stream header: Reserved(4) MajorVer(1) MinorVer(1) HeapSizes(1) Reserved2(1) Valid(8) Sorted(8) RowCounts[] ...
    if (tildeSize < 24) return;
    var heapSizes = all[tildeOff + 6];
    var valid = BinaryPrimitives.ReadUInt64LittleEndian(all.AsSpan((int)tildeOff + 8));
    var cursor = (int)tildeOff + 24;
    // Row counts: one uint32 per set bit in Valid, in ascending table-index order.
    var tableRowCounts = new int[64];
    for (var i = 0; i < 64; i++) {
      if ((valid & (1UL << i)) == 0) continue;
      if (cursor + 4 > tildeOff + tildeSize) return;
      tableRowCounts[i] = BinaryPrimitives.ReadInt32LittleEndian(all.AsSpan(cursor));
      cursor += 4;
    }

    // Heap-index sizes: bit 0 = #Strings 4-byte, bit 1 = #GUID 4-byte, bit 2 = #Blob 4-byte.
    var stringIndexSize = (heapSizes & 0x01) != 0 ? 4 : 2;
    var guidIndexSize   = (heapSizes & 0x02) != 0 ? 4 : 2;
    var blobIndexSize   = (heapSizes & 0x04) != 0 ? 4 : 2;

    // Table row sizes we care about (just enough to skip rows before our targets).
    // We'll iterate the full table ordering, computing sizes on the fly.
    // This is the minimal set needed to walk past tables 0x00-0x28 in the right order.
    var tableSizes = new Dictionary<int, int> {
      [0x00] = 10,                                      // Module: Gen(2)+Name(strIdx)+Mvid(guidIdx)+EncId(guidIdx)+EncBaseId(guidIdx)
      [0x01] = 6,                                       // TypeRef: ResolutionScope(codedIdx)+Name(strIdx)+Namespace(strIdx)
      [0x02] = 14,                                      // TypeDef: Flags(4)+Name(strIdx)+Namespace(strIdx)+Extends(codedIdx)+FieldList(tblIdx)+MethodList(tblIdx)
      [0x04] = 6,                                       // Field: Flags(2)+Name(strIdx)+Signature(blobIdx)
      [0x06] = 14,                                      // MethodDef: RVA(4)+ImplFlags(2)+Flags(2)+Name(strIdx)+Sig(blobIdx)+ParamList(tblIdx)
      [0x08] = 6,                                       // Param: Flags(2)+Sequence(2)+Name(strIdx)
      [0x09] = 6,                                       // InterfaceImpl
      [0x0A] = 6,                                       // MemberRef
      [0x0B] = 16,                                      // Constant
      [0x0C] = 6,                                       // CustomAttribute
      [0x0D] = 6,                                       // FieldMarshal
      [0x0E] = 6,                                       // DeclSecurity
      [0x0F] = 6,                                       // ClassLayout
      [0x10] = 6,                                       // FieldLayout
      [0x11] = 2,                                       // StandAloneSig
      [0x12] = 4,                                       // EventMap
      [0x14] = 6,                                       // Event
      [0x15] = 4,                                       // PropertyMap
      [0x17] = 6,                                       // Property
      [0x18] = 6,                                       // MethodSemantics
      [0x19] = 6,                                       // MethodImpl
      [0x1A] = 2,                                       // ModuleRef: Name(strIdx)
      [0x1B] = 2,                                       // TypeSpec: Signature(blobIdx)
      [0x1C] = 8,                                       // ImplMap
      [0x1D] = 6,                                       // FieldRVA
      [0x20] = 16,                                      // Assembly: HashAlg(4)+Major(2)+Minor(2)+Build(2)+Rev(2)+Flags(4)+PubKey(blobIdx)+Name(strIdx)+Culture(strIdx)
      [0x23] = 12,                                      // AssemblyRef: Major(2)+Minor(2)+Build(2)+Rev(2)+Flags(4)+PubKeyOrToken(blobIdx)+Name(strIdx)+Culture(strIdx)+HashValue(blobIdx)
      [0x26] = 12,                                      // File: Flags(4)+Name(strIdx)+HashValue(blobIdx)
      [0x27] = 4,                                       // ExportedType
      [0x28] = 4,                                       // ManifestResource: Offset(4)+Flags(4)+Name(strIdx)+Implementation(codedIdx)
    };
    // Patch sizes that depend on index widths.
    tableSizes[0x00] = 2 + stringIndexSize + 3 * guidIndexSize;
    tableSizes[0x01] = /*ResolutionScope*/ 2 + 2 * stringIndexSize;
    tableSizes[0x02] = 4 + 2 * stringIndexSize + /*Extends*/ 2 + /*FieldList*/ 2 + /*MethodList*/ 2;
    tableSizes[0x04] = 2 + stringIndexSize + blobIndexSize;
    tableSizes[0x06] = 4 + 2 + 2 + stringIndexSize + blobIndexSize + /*ParamList*/ 2;
    tableSizes[0x08] = 4 + stringIndexSize;
    tableSizes[0x1A] = stringIndexSize;
    tableSizes[0x1B] = blobIndexSize;
    tableSizes[0x20] = 16 + blobIndexSize + 2 * stringIndexSize;
    tableSizes[0x23] = 12 + 2 * blobIndexSize + 2 * stringIndexSize;
    tableSizes[0x26] = 4 + stringIndexSize + blobIndexSize;
    tableSizes[0x28] = 8 + stringIndexSize + /*Implementation codedIdx*/ 2;

    // Walk tables in order, recording offsets for 0x23 (AssemblyRef) and 0x28 (ManifestResource).
    var tableOffsets = new Dictionary<int, long>();
    for (var t = 0; t < 64; t++) {
      if (tableRowCounts[t] == 0) continue;
      if (!tableSizes.TryGetValue(t, out var rowSize)) return; // unsupported layout — bail.
      tableOffsets[t] = cursor;
      cursor += tableRowCounts[t] * rowSize;
      if (cursor > tildeOff + tildeSize) return;
    }

    // Resolve #Strings and #Blob heaps.
    byte[] stringsHeap = [];
    byte[] blobHeap = [];
    if (streams.TryGetValue("#Strings", out var sh))
      stringsHeap = all.AsSpan((int)sh.Offset, sh.Size).ToArray();
    if (streams.TryGetValue("#Blob", out var bh))
      blobHeap = all.AsSpan((int)bh.Offset, bh.Size).ToArray();

    // AssemblyRef rows: decode name + version.
    if (tableOffsets.TryGetValue(0x23, out var asmRefOff)) {
      var sb = new StringBuilder();
      sb.Append("# AssemblyRef\n");
      var rowSize = tableSizes[0x23];
      for (var i = 0; i < tableRowCounts[0x23]; i++) {
        var row = (int)asmRefOff + i * rowSize;
        var major = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(row));
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(row + 2));
        var build = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(row + 4));
        var rev   = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(row + 6));
        var nameIdxOff = row + 12 + blobIndexSize; // skip PublicKeyOrToken blob index
        var nameIdx = stringIndexSize == 4
          ? BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(nameIdxOff))
          : BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(nameIdxOff));
        var name = ReadAsciiZ(stringsHeap, (int)nameIdx);
        sb.Append(name).Append(", Version=").Append(major).Append('.').Append(minor)
          .Append('.').Append(build).Append('.').Append(rev).Append('\n');
      }
      entries.Add(new Entry("references.txt", Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // ManifestResource rows: decode name + offset → emit resource bytes from rsrc area.
    if (tableOffsets.TryGetValue(0x28, out var mresOff) && rsrcRva != 0) {
      var resourceBaseFile = RvaToFileOffset(sections, rsrcRva);
      if (resourceBaseFile < 0) return;
      var rowSize = tableSizes[0x28];
      for (var i = 0; i < tableRowCounts[0x28]; i++) {
        var row = (int)mresOff + i * rowSize;
        var offsetField = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(row));
        var nameIdxOff = row + 8;
        var nameIdx = stringIndexSize == 4
          ? BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(nameIdxOff))
          : BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(nameIdxOff));
        var implIdxOff = nameIdxOff + stringIndexSize;
        var implIdx = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(implIdxOff));
        // Implementation coded index; low 2 bits = tag. Tag 0 (File) / Tag 2 (AssemblyRef) mean external.
        // We only emit in-module resources (implementation index == 0).
        if (implIdx != 0) continue;
        var resStart = resourceBaseFile + offsetField;
        if (resStart + 4 > all.Length) continue;
        var len = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan((int)resStart));
        var dataStart = resStart + 4;
        if (dataStart + len > all.Length || len > rsrcSize) continue;
        var name = ReadAsciiZ(stringsHeap, (int)nameIdx);
        if (string.IsNullOrEmpty(name)) name = $"resource_{i}";
        var safe = SanitizeName(name);
        var bytes = all.AsSpan((int)dataStart, (int)len).ToArray();
        entries.Add(new Entry($"resources/{safe}.bin", bytes));
      }
    }
  }

  private static string ReadAsciiZ(byte[] b, int offset) {
    if (b.Length == 0 || offset < 0 || offset >= b.Length) return "";
    var end = offset;
    while (end < b.Length && b[end] != 0) end++;
    return Encoding.UTF8.GetString(b, offset, end - offset);
  }

  private static string SanitizeName(string s) {
    var sb = new StringBuilder(s.Length);
    foreach (var c in s)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
    return sb.ToString();
  }
}
