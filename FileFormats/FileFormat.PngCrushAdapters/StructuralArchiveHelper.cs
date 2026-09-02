#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Surfaces chunk/page-structured image formats (PNG / TIFF / DCX / ICNS / MPO)
/// as pseudo-archives whose entries are the file's own structural elements —
/// chunks, pages, sub-images and embedded metadata — rather than re-encoded
/// pixel views. Works purely from the raw byte layout, so it is independent of
/// any pixel decoder and never throws from listing.
/// </summary>
/// <remarks>
/// <para>Every decomposition begins with two fixed entries:</para>
/// <list type="bullet">
///   <item><c>FULL.&lt;ext&gt;</c> (<see cref="EntryKinds.Track"/>) — the verbatim
///   original file, byte-identical on extract.</item>
///   <item><c>metadata.ini</c> (<see cref="EntryKinds.Tag"/>) — parsed header
///   fields; carries <c>parse_status = partial</c> when structural walking aborts
///   on a malformed file.</item>
/// </list>
/// <para>Format-specific entries follow: PNG chunks (<c>chunks/NN_&lt;TYPE&gt;.bin</c>),
/// TIFF pages (<c>pages/page_NNN.tif</c>), DCX pages (<c>pages/page_NNN.pcx</c>),
/// ICNS sub-images (<c>icons/&lt;OSType&gt;.&lt;ext&gt;</c>) and MPO pictures
/// (<c>pictures/picture_NN.jpg</c>).</para>
/// </remarks>
public static class StructuralArchiveHelper {

  /// <summary>Canonical <see cref="ArchiveEntryInfo.Kind"/> strings used here.</summary>
  public static class EntryKinds {
    /// <summary>
    /// Defines the track constant value.
    /// </summary>
public const string Track = "Track";
    /// <summary>
    /// Defines the tag constant value.
    /// </summary>
public const string Tag = "Tag";
    /// <summary>
    /// Defines the chunk constant value.
    /// </summary>
public const string Chunk = "Chunk";
    /// <summary>
    /// Defines the frame constant value.
    /// </summary>
public const string Frame = "Frame";
    /// <summary>
    /// Defines the sample constant value.
    /// </summary>
public const string Sample = "Sample";
  }

  /// <summary>One decomposed entry: a name, its bytes and its archive kind.</summary>
  public readonly record struct Entry(string Name, byte[] Data, string Kind, string Method = "stored");

  // ── PNG ────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Decomposes a PNG into FULL + metadata.ini + one entry per chunk
  /// (<c>chunks/NN_&lt;TYPE&gt;.bin</c>, raw chunk bytes incl. length/type/CRC).
  /// Concatenated <c>IDAT</c> chunks are exposed individually. Text chunks
  /// (tEXt) are additionally collected into <c>comments.txt</c>, and the first
  /// <c>iCCP</c>/<c>eXIf</c> payloads into <c>icc.bin</c>/<c>exif.bin</c>.
  /// </summary>
  public static List<Entry> DecomposePng(byte[] file) {
    var entries = new List<Entry> { new("FULL.png", file, EntryKinds.Track) };
    var meta = new IniBuilder("png");
    var ok = false;
    var comments = new StringBuilder();
    var chunkIndex = 0;
    try {
      ReadOnlySpan<byte> sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
      if (file.Length >= 8 && file.AsSpan(0, 8).SequenceEqual(sig)) {
        var pos = 8;
        while (pos + 12 <= file.Length) {
          var dataLen = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos, 4));
          var type = Encoding.ASCII.GetString(file, pos + 4, 4);
          var total = 12 + dataLen;
          if (dataLen < 0 || pos + total > file.Length) break;

          if (type == "IHDR" && dataLen >= 13) {
            var d = file.AsSpan(pos + 8);
            meta.Add("width", BinaryPrimitives.ReadUInt32BigEndian(d));
            meta.Add("height", BinaryPrimitives.ReadUInt32BigEndian(d[4..]));
            meta.Add("bit_depth", d[8]);
            meta.Add("color_type", d[9]);
            meta.Add("interlace", d[12]);
          }

          var chunkBytes = file.AsSpan(pos, total).ToArray();
          entries.Add(new($"chunks/{chunkIndex:D2}_{SanitizeType(type)}.bin", chunkBytes, EntryKinds.Chunk));
          ++chunkIndex;

          if (type == "tEXt") AppendTextChunk(comments, file.AsSpan(pos + 8, dataLen));
          else if (type == "iCCP" && !entries.Any(e => e.Name == "icc.bin"))
            entries.Add(new("icc.bin", file.AsSpan(pos + 8, dataLen).ToArray(), EntryKinds.Tag));
          else if (type == "eXIf" && !entries.Any(e => e.Name == "exif.bin"))
            entries.Add(new("exif.bin", file.AsSpan(pos + 8, dataLen).ToArray(), EntryKinds.Tag));

          pos += total;
          if (type == "IEND") { ok = true; break; }
        }
      }
    } catch { /* fall through to partial */ }

    meta.AddStatus(ok);
    if (comments.Length > 0)
      entries.Add(new("comments.txt", Encoding.UTF8.GetBytes(comments.ToString()), EntryKinds.Tag));
    entries.Insert(1, new("metadata.ini", meta.ToBytes(), EntryKinds.Tag));
    return entries;
  }

  private static void AppendTextChunk(StringBuilder sb, ReadOnlySpan<byte> data) {
    var nul = data.IndexOf((byte)0);
    if (nul < 0) return;
    var key = Encoding.Latin1.GetString(data[..nul]);
    var val = Encoding.Latin1.GetString(data[(nul + 1)..]);
    sb.Append(key).Append(": ").Append(val).Append('\n');
  }

  // ── TIFF (and BigTIFF-by-page best effort) ──────────────────────────────────

  /// <summary>
  /// Decomposes a multi-page TIFF into FULL + metadata.ini + one self-contained
  /// single-page TIFF per IFD (<c>pages/page_NNN.tif</c>). Each emitted page is a
  /// fresh little/big-endian-preserving TIFF carrying that IFD's tags plus the
  /// strip/tile data they point at, re-based to the new file. Pages whose offsets
  /// can't be resolved are skipped; the FULL entry always round-trips.
  /// </summary>
  public static List<Entry> DecomposeTiff(byte[] file) {
    var entries = new List<Entry> { new("FULL.tif", file, EntryKinds.Track) };
    var meta = new IniBuilder("tiff");
    var pageCount = 0;
    var ok = false;
    try {
      if (file.Length >= 8) {
        var le = file[0] == 'I' && file[1] == 'I';
        var be = file[0] == 'M' && file[1] == 'M';
        if ((le || be) && ReadU16(file, 2, le) == 0x002A) {
          meta.Add("byte_order", le ? "little-endian" : "big-endian");
          var ifdOffset = (int)ReadU32(file, 4, le);
          var guard = 0;
          while (ifdOffset > 0 && ifdOffset + 2 <= file.Length && guard++ < 4096) {
            var page = BuildSinglePageTiff(file, ifdOffset, le, out var nextOffset);
            if (page != null) {
              entries.Add(new($"pages/page_{pageCount:D3}.tif", page, EntryKinds.Frame));
              ++pageCount;
            }
            if (nextOffset <= ifdOffset && nextOffset != 0) break; // anti-loop
            ifdOffset = nextOffset;
          }
          ok = pageCount > 0;
        }
      }
    } catch { /* partial */ }

    meta.Add("page_count", pageCount);
    meta.AddStatus(ok);
    entries.Insert(1, new("metadata.ini", meta.ToBytes(), EntryKinds.Tag));
    return entries;
  }

  // TIFF data tags whose values are file offsets that must be copied + re-based.
  private static readonly ushort[] OffsetTags = [0x0111 /*StripOffsets*/, 0x0144 /*TileOffsets*/];
  private static readonly ushort[] CountTags = [0x0117 /*StripByteCounts*/, 0x0145 /*TileByteCounts*/];

  /// <summary>
  /// Rebuilds the IFD at <paramref name="ifdOffset"/> into a standalone single-page
  /// TIFF, copying its strip/tile data and external (>4-byte) tag values, and
  /// rewriting all offsets relative to the new file. Returns null if the IFD is
  /// structurally invalid. <paramref name="nextOffset"/> receives the original
  /// next-IFD pointer (0 = last page).
  /// </summary>
  private static byte[]? BuildSinglePageTiff(byte[] file, int ifdOffset, bool le, out int nextOffset) {
    nextOffset = 0;
    if (ifdOffset + 2 > file.Length) return null;
    var entryCount = ReadU16(file, ifdOffset, le);
    var ifdSize = 2 + entryCount * 12 + 4;
    if (ifdOffset + ifdSize > file.Length) return null;
    nextOffset = (int)ReadU32(file, ifdOffset + 2 + entryCount * 12, le);

    // Output: 8-byte header, then IFD at offset 8, then external data blobs.
    var ifdStart = 8;
    var dataStart = ifdStart + 2 + entryCount * 12 + 4;
    var external = new List<byte[]>();           // appended after the IFD
    var externalOffsets = new List<int>();        // new offset for each external blob
    var dataCursor = dataStart;

    var newIfd = new byte[2 + entryCount * 12 + 4];
    WriteU16(newIfd, 0, entryCount, le);

    // First pass: gather strip/tile data block list to copy contiguously.
    for (var i = 0; i < entryCount; i++) {
      var src = ifdOffset + 2 + i * 12;
      var tag = ReadU16(file, src, le);
      var type = ReadU16(file, src + 2, le);
      var count = (int)ReadU32(file, src + 4, le);
      var typeSize = TypeSize(type);
      var byteLen = (long)typeSize * count;

      Array.Copy(file, src, newIfd, 2 + i * 12, 12); // copy tag/type/count + value verbatim

      var isOffsetTag = Array.IndexOf(OffsetTags, tag) >= 0;
      if (isOffsetTag) {
        // Copy each referenced data region, re-base the offsets in the value field.
        var offsets = ReadValues(file, src + 8, type, count, le);
        var counts = FindCounts(file, ifdOffset, entryCount, le, OffsetToCountTag(tag));
        var newOffsets = new uint[offsets.Length];
        for (var k = 0; k < offsets.Length; k++) {
          var off = (int)offsets[k];
          var len = k < counts.Length ? (int)counts[k] : 0;
          if (off < 0 || len <= 0 || off + len > file.Length) { newOffsets[k] = 0; continue; }
          var blob = new byte[len];
          Array.Copy(file, off, blob, 0, len);
          externalOffsets.Add(dataCursor);
          external.Add(blob);
          newOffsets[k] = (uint)dataCursor;
          dataCursor += len;
        }
        WriteOffsetValues(newIfd, 2 + i * 12 + 8, type, count, le, newOffsets, ref dataCursor, external, externalOffsets);
      } else if (byteLen > 4) {
        // External non-offset value (e.g. long ASCII / arrays): copy + re-base pointer.
        var off = (int)ReadU32(file, src + 8, le);
        if (off >= 0 && off + byteLen <= file.Length) {
          var blob = new byte[byteLen];
          Array.Copy(file, off, blob, 0, (int)byteLen);
          WriteU32(newIfd, 2 + i * 12 + 8, (uint)dataCursor, le);
          externalOffsets.Add(dataCursor);
          external.Add(blob);
          dataCursor += (int)byteLen;
        }
      }
    }

    WriteU32(newIfd, 2 + entryCount * 12, 0, le); // single page: next-IFD = 0

    var totalLen = dataCursor;
    var outBuf = new byte[totalLen];
    outBuf[0] = (byte)(le ? 'I' : 'M');
    outBuf[1] = (byte)(le ? 'I' : 'M');
    WriteU16(outBuf, 2, 0x002A, le);
    WriteU32(outBuf, 4, (uint)ifdStart, le);
    Array.Copy(newIfd, 0, outBuf, ifdStart, newIfd.Length);
    for (var i = 0; i < external.Count; i++)
      Array.Copy(external[i], 0, outBuf, externalOffsets[i], external[i].Length);
    return outBuf;
  }

  // Writes re-based offset values back into the IFD value field (or external area
  // if the array doesn't fit inline). Simplicity: arrays >4 bytes are appended.
  private static void WriteOffsetValues(byte[] ifd, int valueFieldPos, ushort type, int count, bool le,
                                        uint[] newOffsets, ref int dataCursor,
                                        List<byte[]> external, List<int> externalOffsets) {
    var typeSize = TypeSize(type);
    var byteLen = typeSize * count;
    if (byteLen <= 4) {
      for (var k = 0; k < count; k++) {
        if (type == 3) WriteU16(ifd, valueFieldPos + k * 2, (ushort)newOffsets[k], le);
        else WriteU32(ifd, valueFieldPos + k * 4, newOffsets[k], le);
      }
    } else {
      // Build an external array blob of the rewritten offsets.
      var blob = new byte[byteLen];
      for (var k = 0; k < count; k++) {
        if (type == 3) WriteU16(blob, k * 2, (ushort)newOffsets[k], le);
        else WriteU32(blob, k * 4, newOffsets[k], le);
      }
      WriteU32(ifd, valueFieldPos, (uint)dataCursor, le);
      externalOffsets.Add(dataCursor);
      external.Add(blob);
      dataCursor += byteLen;
    }
  }

  private static ushort OffsetToCountTag(ushort offsetTag) =>
    offsetTag == 0x0111 ? (ushort)0x0117 : (ushort)0x0145;

  private static uint[] FindCounts(byte[] file, int ifdOffset, int entryCount, bool le, ushort countTag) {
    for (var i = 0; i < entryCount; i++) {
      var src = ifdOffset + 2 + i * 12;
      if (ReadU16(file, src, le) != countTag) continue;
      var type = ReadU16(file, src + 2, le);
      var count = (int)ReadU32(file, src + 4, le);
      return ReadValues(file, src + 8, type, count, le);
    }
    return [];
  }

  private static uint[] ReadValues(byte[] file, int valueFieldPos, ushort type, int count, bool le) {
    var typeSize = TypeSize(type);
    var byteLen = typeSize * count;
    var basePos = byteLen <= 4 ? valueFieldPos : (int)ReadU32(file, valueFieldPos, le);
    var result = new uint[count];
    for (var i = 0; i < count; i++) {
      var pos = basePos + i * typeSize;
      if (pos + typeSize > file.Length) break;
      result[i] = type == 3 ? ReadU16(file, pos, le) : ReadU32(file, pos, le);
    }
    return result;
  }

  // ── DCX (multi-page PCX) ────────────────────────────────────────────────────

  /// <summary>
  /// Decomposes a DCX into FULL + metadata.ini + one PCX per page
  /// (<c>pages/page_NNN.pcx</c>). The DCX header is a 4-byte magic (0x3ADE68B1,
  /// little-endian) followed by up to 1023 little-endian uint32 page offsets
  /// terminated by a zero entry; each page spans from its offset to the next.
  /// </summary>
  public static List<Entry> DecomposeDcx(byte[] file) {
    var entries = new List<Entry> { new("FULL.dcx", file, EntryKinds.Track) };
    var meta = new IniBuilder("dcx");
    var pageCount = 0;
    var ok = false;
    try {
      if (file.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(file) == 0x3ADE68B1) {
        var offsets = new List<uint>();
        var p = 4;
        while (p + 4 <= file.Length) {
          var off = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(p, 4));
          p += 4;
          if (off == 0) break;
          if (off < file.Length) offsets.Add(off);
        }
        for (var i = 0; i < offsets.Count; i++) {
          var start = (int)offsets[i];
          var end = i + 1 < offsets.Count ? (int)offsets[i + 1] : file.Length;
          if (end <= start || end > file.Length) end = file.Length;
          var blob = file.AsSpan(start, end - start).ToArray();
          entries.Add(new($"pages/page_{pageCount:D3}.pcx", blob, EntryKinds.Frame));
          ++pageCount;
        }
        ok = pageCount > 0;
      }
    } catch { /* partial */ }

    meta.Add("page_count", pageCount);
    meta.AddStatus(ok);
    entries.Insert(1, new("metadata.ini", meta.ToBytes(), EntryKinds.Tag));
    return entries;
  }

  // ── ICNS (Apple icon suite) ─────────────────────────────────────────────────

  /// <summary>
  /// Decomposes an ICNS into FULL + metadata.ini + one entry per icon element
  /// (<c>icons/&lt;OSType&gt;.&lt;ext&gt;</c>). ICNS is a 'icns' magic + total
  /// length, then a sequence of [4-byte OSType][4-byte big-endian length][data].
  /// PNG/JP2-payload elements keep their real extension; legacy raw elements get
  /// <c>.bin</c>. The <c>TOC </c> and <c>icnV</c> control elements are surfaced as
  /// <see cref="EntryKinds.Tag"/>.
  /// </summary>
  public static List<Entry> DecomposeIcns(byte[] file) {
    var entries = new List<Entry> { new("FULL.icns", file, EntryKinds.Track) };
    var meta = new IniBuilder("icns");
    var elementCount = 0;
    var ok = false;
    try {
      if (file.Length >= 8 && file[0] == 'i' && file[1] == 'c' && file[2] == 'n' && file[3] == 's') {
        var declared = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(4, 4));
        meta.Add("declared_length", declared);
        var limit = declared > 0 && declared <= file.Length ? declared : file.Length;
        var pos = 8;
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        while (pos + 8 <= limit) {
          var osType = Encoding.ASCII.GetString(file, pos, 4);
          var len = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos + 4, 4));
          if (len < 8 || pos + len > limit) break;
          var payload = file.AsSpan(pos + 8, len - 8).ToArray();
          var (ext, kind) = ClassifyIcnsElement(osType, payload);
          var safe = SanitizeType(osType);
          var name = $"icons/{safe}{ext}";
          var dup = 1;
          while (!seenNames.Add(name)) name = $"icons/{safe}_{dup++}{ext}";
          entries.Add(new(name, payload, kind));
          ++elementCount;
          pos += len;
        }
        ok = elementCount > 0;
      }
    } catch { /* partial */ }

    meta.Add("element_count", elementCount);
    meta.AddStatus(ok);
    entries.Insert(1, new("metadata.ini", meta.ToBytes(), EntryKinds.Tag));
    return entries;
  }

  private static (string Ext, string Kind) ClassifyIcnsElement(string osType, byte[] payload) {
    if (osType is "TOC " or "icnV" or "name" or "info")
      return (".bin", EntryKinds.Tag);
    if (payload.Length >= 8 && payload[0] == 0x89 && payload[1] == 0x50 && payload[2] == 0x4E && payload[3] == 0x47)
      return (".png", EntryKinds.Frame);
    if (payload.Length >= 12 && payload[4] == 'j' && payload[5] == 'P' && payload[6] == ' ' && payload[7] == ' ')
      return (".jp2", EntryKinds.Frame);
    return (".bin", EntryKinds.Sample);
  }

  // ── MPO (Multi-Picture Object) ──────────────────────────────────────────────

  /// <summary>
  /// Decomposes an MPO into FULL + metadata.ini + one JPEG per embedded picture
  /// (<c>pictures/picture_NN.jpg</c>). Pictures are split by scanning for SOI
  /// (FF D8) … EOI (FF D9) marker pairs, which cleanly separates the individual
  /// JPEG streams concatenated by the MP container without needing to parse the
  /// MP index IFD.
  /// </summary>
  public static List<Entry> DecomposeMpo(byte[] file) {
    var entries = new List<Entry> { new("FULL.mpo", file, EntryKinds.Track) };
    var meta = new IniBuilder("mpo");
    var pictureCount = 0;
    var ok = false;
    try {
      var pos = 0;
      while (pos + 1 < file.Length) {
        // Find next SOI
        if (!(file[pos] == 0xFF && file[pos + 1] == 0xD8)) { ++pos; continue; }
        var start = pos;
        // Find matching EOI from here.
        var scan = pos + 2;
        var end = -1;
        while (scan + 1 < file.Length) {
          if (file[scan] == 0xFF && file[scan + 1] == 0xD9) { end = scan + 2; break; }
          ++scan;
        }
        if (end < 0) break;
        var blob = file.AsSpan(start, end - start).ToArray();
        entries.Add(new($"pictures/picture_{pictureCount:D2}.jpg", blob, EntryKinds.Frame));
        ++pictureCount;
        pos = end;
      }
      ok = pictureCount > 0;
    } catch { /* partial */ }

    meta.Add("picture_count", pictureCount);
    meta.AddStatus(ok);
    entries.Insert(1, new("metadata.ini", meta.ToBytes(), EntryKinds.Tag));
    return entries;
  }

  // ── Shared list/extract plumbing ────────────────────────────────────────────

  /// <summary>Maps a decomposition to <see cref="ArchiveEntryInfo"/> for List().</summary>
  public static List<ArchiveEntryInfo> ToArchiveEntries(IReadOnlyList<Entry> entries) {
    var result = new List<ArchiveEntryInfo>(entries.Count);
    for (var i = 0; i < entries.Count; i++) {
      var e = entries[i];
      result.Add(new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength,
        e.Method, false, false, null, e.Kind));
    }
    return result;
  }

  /// <summary>Reads the whole stream into a byte array (rewinding if seekable).</summary>
  public static byte[] ReadAllBytes(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  // ── INI builder ─────────────────────────────────────────────────────────────

  private sealed class IniBuilder(string section) {
    private readonly StringBuilder _sb = new StringBuilder().AppendLine($"[{section}]");
    public void Add(string key, long value) => _sb.Append(CultureInfo.InvariantCulture, $"{key} = {value}\n");
    public void Add(string key, string value) => _sb.Append(CultureInfo.InvariantCulture, $"{key} = {value}\n");
    public void AddStatus(bool ok) {
      if (!ok) _sb.Append("parse_status = partial\n");
    }
    public byte[] ToBytes() => Encoding.UTF8.GetBytes(_sb.ToString());
  }

  // ── Low-level byte helpers ──────────────────────────────────────────────────

  private static string SanitizeType(string type) {
    Span<char> buf = stackalloc char[type.Length];
    for (var i = 0; i < type.Length; i++) {
      var c = type[i];
      buf[i] = char.IsLetterOrDigit(c) ? c : '_';
    }
    return new string(buf);
  }

  private static ushort ReadU16(byte[] d, int o, bool le) =>
    le ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o));
  private static uint ReadU32(byte[] d, int o, bool le) =>
    le ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
  private static void WriteU16(byte[] d, int o, ushort v, bool le) {
    if (le) BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o), v);
    else BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(o), v);
  }
  private static void WriteU32(byte[] d, int o, uint v, bool le) {
    if (le) BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
    else BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(o), v);
  }

  private static int TypeSize(ushort type) => type switch {
    1 or 2 or 6 or 7 => 1,
    3 or 8 => 2,
    4 or 9 or 11 => 4,
    5 or 10 or 12 => 8,
    _ => 4,
  };
}
