#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Dng;

/// <summary>
/// Minimal TIFF / DNG walker. A DNG is a TIFF whose IFD0 carries the camera-makers'
/// thumbnail, plus a <c>SubIFDs</c> tag (0x014A) pointing to N sub-IFDs — each one
/// either a full-res raw image (Bayer/CFA data addressed by <c>StripOffsets</c> /
/// <c>StripByteCounts</c>) or an embedded JPEG preview (<c>JpegInterchangeFormat</c>
/// + length). We only enumerate and extract bytes — no JPEG re-wrap, no raw decode.
/// Also walks the EXIF sub-IFD (tag 0x8769) so the MakerNote (0x927C) is visible.
/// </summary>
public sealed class DngReader {
  /// <summary>
  /// Defines the tag sub if ds constant value.
  /// </summary>
  public const ushort TagSubIFDs = 0x014A;
  /// <summary>
  /// Defines the tag strip offsets constant value.
  /// </summary>
  public const ushort TagStripOffsets = 0x0111;
  /// <summary>
  /// Defines the tag strip byte counts constant value.
  /// </summary>
  public const ushort TagStripByteCounts = 0x0117;
  /// <summary>
  /// Defines the tag jpeg interchange format constant value.
  /// </summary>
  public const ushort TagJpegInterchangeFormat = 0x0201;
  /// <summary>
  /// Defines the tag jpeg interchange format length constant value.
  /// </summary>
  public const ushort TagJpegInterchangeFormatLength = 0x0202;
  /// <summary>
  /// Defines the tag compression constant value.
  /// </summary>
  public const ushort TagCompression = 0x0103;
  /// <summary>
  /// Defines the tag photometric interpretation constant value.
  /// </summary>
  public const ushort TagPhotometricInterpretation = 0x0106;
  /// <summary>
  /// Defines the tag new sub file type constant value.
  /// </summary>
  public const ushort TagNewSubFileType = 0x00FE;
  /// <summary>
  /// Defines the tag exif ifd constant value.
  /// </summary>
  public const ushort TagExifIfd = 0x8769;
  /// <summary>
  /// Defines the tag maker note constant value.
  /// </summary>
  public const ushort TagMakerNote = 0x927C;
  /// <summary>
  /// Defines the tag dng version constant value.
  /// </summary>
  public const ushort TagDngVersion = 0xC612;

  /// <summary>
  /// Represents an ifd.
  /// </summary>
  public sealed record Ifd(long Offset, IReadOnlyList<Entry> Entries);
  /// <summary>
  /// Represents an entry.
  /// </summary>
  public sealed record Entry(ushort Tag, ushort Type, uint Count, uint ValueOrOffset);

  internal readonly record struct SourceRange(int Offset, int Length);
  internal sealed record StructuralLayout(
    bool IsBigEndian,
    IReadOnlyList<Ifd> TopLevelIfds,
    IReadOnlyList<Ifd> SubIfds,
    Ifd? ExifIfd,
    int DngVersionLength);

  /// <summary>
  /// Gets a value indicating whether is big endian.
  /// </summary>
  public bool IsBigEndian { get; }
  /// <summary>
  /// Gets the top level ifds.
  /// </summary>
  public IReadOnlyList<Ifd> TopLevelIfds { get; }
  /// <summary>
  /// Gets the sub ifds.
  /// </summary>
  public IReadOnlyList<Ifd> SubIfds { get; }
  /// <summary>
  /// Gets the exif ifd.
  /// </summary>
  public Ifd? ExifIfd { get; }
  /// <summary>
  /// Gets the raw.
  /// </summary>
  public byte[] Raw { get; }
  /// <summary>Size of the <c>DNGVersion</c> tag value or 0 when absent. Used to filter plain TIFF.</summary>
  public int DngVersionLength { get; }

  private readonly byte[] _data;

  internal static StructuralLayout ReadLayout(ReadOnlySpan<byte> data) {
    if (data.Length < 8)
      throw new InvalidDataException("TIFF: too small.");

    var isBigEndian = data[0] == 'M' && data[1] == 'M';
    if (!isBigEndian && !(data[0] == 'I' && data[1] == 'I'))
      throw new InvalidDataException("TIFF: bad byte-order mark.");
    if (ReadUInt16Layout(data, 2, isBigEndian) != 42)
      throw new InvalidDataException("TIFF: bad magic (need 42).");

    var topLevelIfds = new List<Ifd>();
    var ifdOffset = (long)ReadUInt32Layout(data, 4, isBigEndian);
    var guard = 0;
    while (ifdOffset != 0 && guard++ < 32) {
      if (ifdOffset < 0 || ifdOffset + 2 > data.Length)
        break;

      var ifd = ReadIfdLayout(data, isBigEndian, ifdOffset);
      topLevelIfds.Add(ifd);

      var afterEntries = ifdOffset + 2 + ifd.Entries.Count * 12L;
      if (afterEntries < 0 || afterEntries + 4 > data.Length)
        break;
      ifdOffset = ReadUInt32Layout(data, (int)afterEntries, isBigEndian);
    }

    var subIfds = new List<Ifd>();
    foreach (var ifd in topLevelIfds) {
      var sub = ifd.Entries.FirstOrDefault(static entry => entry.Tag == TagSubIFDs);
      if (sub == null)
        continue;

      foreach (var offset in ReadValuesAsUInt32Layout(data, isBigEndian, sub))
        if (offset != 0 && offset + 2 <= data.Length)
          subIfds.Add(ReadIfdLayout(data, isBigEndian, offset));
    }

    Ifd? exifIfd = null;
    var exifEntry = topLevelIfds.FirstOrDefault()?.Entries
      .FirstOrDefault(static entry => entry.Tag == TagExifIfd);
    if (exifEntry != null && exifEntry.ValueOrOffset != 0
        && exifEntry.ValueOrOffset + 2 <= data.Length)
      exifIfd = ReadIfdLayout(data, isBigEndian, exifEntry.ValueOrOffset);

    var dngTag = topLevelIfds.FirstOrDefault()?.Entries
      .FirstOrDefault(static entry => entry.Tag == TagDngVersion);

    return new StructuralLayout(
      isBigEndian,
      topLevelIfds,
      subIfds,
      exifIfd,
      dngTag != null ? checked((int)dngTag.Count) : 0);
  }

  internal static IReadOnlyList<SourceRange> GetStripRanges(
      ReadOnlySpan<byte> data, StructuralLayout layout, Ifd ifd) {
    var offsetsEntry = ifd.Entries.FirstOrDefault(static entry => entry.Tag == TagStripOffsets);
    var sizesEntry = ifd.Entries.FirstOrDefault(static entry => entry.Tag == TagStripByteCounts);
    if (offsetsEntry == null || sizesEntry == null)
      return [];

    var offsets = ReadValuesAsUInt32Layout(data, layout.IsBigEndian, offsetsEntry);
    var sizes = ReadValuesAsUInt32Layout(data, layout.IsBigEndian, sizesEntry);
    if (offsets.Count == 0 || offsets.Count != sizes.Count)
      return [];

    var ranges = new List<SourceRange>(offsets.Count);
    for (var index = 0; index < offsets.Count; ++index) {
      var offset = (long)offsets[index];
      var length = (long)sizes[index];
      if (offset < 0 || length <= 0 || offset > data.Length
          || length > data.Length - offset
          || offset > int.MaxValue || length > int.MaxValue)
        continue;

      ranges.Add(new SourceRange((int)offset, (int)length));
    }

    return ranges;
  }

  internal static IReadOnlyList<SourceRange> GetEmbeddedJpegRanges(
      ReadOnlySpan<byte> data, Ifd ifd) {
    var offsetEntry = ifd.Entries.FirstOrDefault(static entry => entry.Tag == TagJpegInterchangeFormat);
    var lengthEntry = ifd.Entries.FirstOrDefault(static entry => entry.Tag == TagJpegInterchangeFormatLength);
    if (offsetEntry == null || lengthEntry == null)
      return [];

    var offset = (long)offsetEntry.ValueOrOffset;
    var length = (long)lengthEntry.ValueOrOffset;
    if (offset < 0 || length <= 0 || offset > data.Length
        || length > data.Length - offset
        || offset > int.MaxValue || length > int.MaxValue)
      return [];

    return [new SourceRange((int)offset, (int)length)];
  }

  internal static SourceRange? GetExternalValueRange(
      ReadOnlySpan<byte> data, Entry entry) {
    if (entry.Count <= 4)
      return null;

    var offset = (long)entry.ValueOrOffset;
    var length = (long)entry.Count;
    if (offset < 0 || length <= 0 || offset > data.Length
        || length > data.Length - offset
        || offset > int.MaxValue || length > int.MaxValue)
      return null;

    return new SourceRange((int)offset, (int)length);
  }

  internal static int GetTotalLength(IReadOnlyList<SourceRange> ranges) {
    var total = 0;
    foreach (var range in ranges)
      total = checked(total + range.Length);
    return total;
  }

  private static Ifd ReadIfdLayout(ReadOnlySpan<byte> data, bool isBigEndian, long offset) {
    if (offset < 0 || offset > int.MaxValue || offset + 2 > data.Length)
      return new Ifd(offset, []);

    var count = ReadUInt16Layout(data, (int)offset, isBigEndian);
    var entries = new List<Entry>(count);
    var position = checked((int)offset + 2);
    for (var index = 0; index < count; ++index, position += 12) {
      if (position + 12 > data.Length)
        break;

      entries.Add(new Entry(
        ReadUInt16Layout(data, position, isBigEndian),
        ReadUInt16Layout(data, position + 2, isBigEndian),
        ReadUInt32Layout(data, position + 4, isBigEndian),
        ReadUInt32Layout(data, position + 8, isBigEndian)));
    }

    return new Ifd(offset, entries);
  }

  private static IReadOnlyList<uint> ReadValuesAsUInt32Layout(
      ReadOnlySpan<byte> data, bool isBigEndian, Entry entry) {
    var typeSize = entry.Type switch { 1 => 1, 3 => 2, 4 => 4, _ => 0 };
    if (typeSize == 0 || entry.Count > int.MaxValue)
      return [];

    var count = (int)entry.Count;
    var total = checked(typeSize * count);
    if (total <= 4)
      return ReadInlineValuesLayout(entry, typeSize, isBigEndian);

    var start = (long)entry.ValueOrOffset;
    if (start < 0 || start > data.Length || total > data.Length - start || start > int.MaxValue)
      return [];

    var result = new uint[count];
    var offset = (int)start;
    for (var index = 0; index < count; ++index) {
      result[index] = typeSize switch {
        1 => data[offset + index],
        2 => ReadUInt16Layout(data, offset + index * 2, isBigEndian),
        4 => ReadUInt32Layout(data, offset + index * 4, isBigEndian),
        _ => 0,
      };
    }

    return result;
  }

  private static uint[] ReadInlineValuesLayout(Entry entry, int typeSize, bool isBigEndian) {
    Span<byte> bytes = stackalloc byte[4];
    if (isBigEndian)
      BinaryPrimitives.WriteUInt32BigEndian(bytes, entry.ValueOrOffset);
    else
      BinaryPrimitives.WriteUInt32LittleEndian(bytes, entry.ValueOrOffset);

    var result = new uint[entry.Count];
    for (var index = 0; index < entry.Count; ++index) {
      result[index] = typeSize switch {
        1 => bytes[index],
        2 => isBigEndian
          ? BinaryPrimitives.ReadUInt16BigEndian(bytes[(index * 2)..])
          : BinaryPrimitives.ReadUInt16LittleEndian(bytes[(index * 2)..]),
        4 => entry.ValueOrOffset,
        _ => 0,
      };
    }

    return result;
  }

  private static ushort ReadUInt16Layout(ReadOnlySpan<byte> data, int position, bool isBigEndian) =>
    isBigEndian
      ? BinaryPrimitives.ReadUInt16BigEndian(data[position..])
      : BinaryPrimitives.ReadUInt16LittleEndian(data[position..]);

  private static uint ReadUInt32Layout(ReadOnlySpan<byte> data, int position, bool isBigEndian) =>
    isBigEndian
      ? BinaryPrimitives.ReadUInt32BigEndian(data[position..])
      : BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);

  /// <summary>
  /// Initializes a new instance of <see cref="DngReader"/>.
  /// </summary>
  public DngReader(byte[] data) {
    this._data = data;
    this.Raw = data;
    if (data.Length < 8) throw new InvalidDataException("TIFF: too small.");
    this.IsBigEndian = data[0] == 'M' && data[1] == 'M';
    if (!this.IsBigEndian && !(data[0] == 'I' && data[1] == 'I'))
      throw new InvalidDataException("TIFF: bad byte-order mark.");
    var magic = ReadUInt16(data, 2);
    if (magic != 42) throw new InvalidDataException("TIFF: bad magic (need 42).");

    var ifds = new List<Ifd>();
    var ifd0Offset = (long)ReadUInt32(data, 4);
    var guard = 0;
    while (ifd0Offset != 0 && guard++ < 32) {
      if (ifd0Offset + 2 > data.Length) break;
      var ifd = ReadIfd(ifd0Offset);
      ifds.Add(ifd);
      // Next IFD offset lives immediately after the entry array.
      var afterEntries = ifd0Offset + 2 + ifd.Entries.Count * 12L;
      if (afterEntries + 4 > data.Length) break;
      ifd0Offset = ReadUInt32(data, (int)afterEntries);
    }
    this.TopLevelIfds = ifds;

    // Walk SubIFD chains.
    var subList = new List<Ifd>();
    foreach (var ifd in ifds) {
      var sub = ifd.Entries.FirstOrDefault(e => e.Tag == TagSubIFDs);
      if (sub == null) continue;
      foreach (var off in ReadValuesAsUInt32(sub))
        if (off != 0 && off + 2 <= data.Length)
          subList.Add(ReadIfd(off));
    }
    this.SubIfds = subList;

    // Optional EXIF sub-IFD.
    var exifEntry = ifds.FirstOrDefault()?.Entries.FirstOrDefault(e => e.Tag == TagExifIfd);
    if (exifEntry != null && exifEntry.ValueOrOffset != 0 && exifEntry.ValueOrOffset + 2 <= data.Length)
      this.ExifIfd = ReadIfd(exifEntry.ValueOrOffset);

    var dngTag = ifds.FirstOrDefault()?.Entries.FirstOrDefault(e => e.Tag == TagDngVersion);
    this.DngVersionLength = dngTag != null ? (int)dngTag.Count : 0;
  }

  private Ifd ReadIfd(long offset) {
    var count = ReadUInt16(this._data, (int)offset);
    var entries = new List<Entry>(count);
    var p = (int)offset + 2;
    for (var i = 0; i < count; i++, p += 12) {
      if (p + 12 > this._data.Length) break;
      entries.Add(new Entry(
        Tag: ReadUInt16(this._data, p),
        Type: ReadUInt16(this._data, p + 2),
        Count: ReadUInt32(this._data, p + 4),
        ValueOrOffset: ReadUInt32(this._data, p + 8)));
    }
    return new Ifd(offset, entries);
  }

  /// <summary>
  /// Reads the values as u int 32 from the supplied input.
  /// </summary>
  public IReadOnlyList<uint> ReadValuesAsUInt32(Entry e) {
    // TIFF types: 1=BYTE, 3=SHORT (2), 4=LONG (4), 5=RATIONAL (8).
    var tsize = e.Type switch { 1 => 1, 3 => 2, 4 => 4, _ => 0 };
    if (tsize == 0) return Array.Empty<uint>();
    var total = tsize * (int)e.Count;
    int start;
    if (total <= 4) {
      // Inline in the entry — the 4 value bytes have already been read as LE/BE by ReadUInt32.
      // Re-derive their location: ValueOrOffset occupies offset +8 of the entry, but we need
      // byte-accurate access because SHORT+SHORT packs two values into 4 bytes.
      // Reconstruct by finding this entry's byte position.
      return ReadInlineValues(e, tsize);
    }
    start = (int)e.ValueOrOffset;
    if (start + total > this._data.Length) return Array.Empty<uint>();
    var result = new uint[e.Count];
    for (var i = 0; i < e.Count; i++) {
      result[i] = tsize switch {
        1 => this._data[start + i],
        2 => ReadUInt16(this._data, start + i * 2),
        4 => ReadUInt32(this._data, start + i * 4),
        _ => 0,
      };
    }
    return result;
  }

  private uint[] ReadInlineValues(Entry e, int tsize) {
    // Reconstitute inline bytes from ValueOrOffset in the file's byte order.
    Span<byte> buf = stackalloc byte[4];
    if (this.IsBigEndian) {
      buf[0] = (byte)(e.ValueOrOffset >> 24);
      buf[1] = (byte)(e.ValueOrOffset >> 16);
      buf[2] = (byte)(e.ValueOrOffset >> 8);
      buf[3] = (byte)e.ValueOrOffset;
    } else {
      buf[0] = (byte)e.ValueOrOffset;
      buf[1] = (byte)(e.ValueOrOffset >> 8);
      buf[2] = (byte)(e.ValueOrOffset >> 16);
      buf[3] = (byte)(e.ValueOrOffset >> 24);
    }
    var result = new uint[e.Count];
    for (var i = 0; i < e.Count; i++) {
      result[i] = tsize switch {
        1 => buf[i],
        2 => (uint)(this.IsBigEndian
          ? (buf[i * 2] << 8) | buf[i * 2 + 1]
          : buf[i * 2] | (buf[i * 2 + 1] << 8)),
        4 => e.ValueOrOffset,
        _ => 0,
      };
    }
    return result;
  }

  /// <summary>
  /// Reads the bytes at from the supplied input.
  /// </summary>
  public byte[] ReadBytesAt(long offset, long length) {
    if (offset < 0 || length <= 0 || offset + length > this._data.Length) return Array.Empty<byte>();
    var result = new byte[length];
    Buffer.BlockCopy(this._data, (int)offset, result, 0, (int)length);
    return result;
  }

  /// <summary>
  /// Joins all strip bytes for an IFD, or returns empty if no strip data. Useful for both
  /// JPEG-in-strip previews and raw sensor data — the caller picks which by inspecting
  /// <c>Compression</c> / <c>PhotometricInterpretation</c>.
  /// </summary>
  public byte[] ReadStripBytes(Ifd ifd) {
    var offsetsEntry = ifd.Entries.FirstOrDefault(e => e.Tag == TagStripOffsets);
    var sizesEntry = ifd.Entries.FirstOrDefault(e => e.Tag == TagStripByteCounts);
    if (offsetsEntry == null || sizesEntry == null) return Array.Empty<byte>();
    var offsets = ReadValuesAsUInt32(offsetsEntry);
    var sizes = ReadValuesAsUInt32(sizesEntry);
    if (offsets.Count == 0 || offsets.Count != sizes.Count) return Array.Empty<byte>();
    using var ms = new MemoryStream();
    for (var i = 0; i < offsets.Count; i++)
      ms.Write(ReadBytesAt(offsets[i], sizes[i]));
    return ms.ToArray();
  }

  /// <summary>Extracts an embedded JPEG preview using tags 0x0201/0x0202, or empty if absent.</summary>
  public byte[] ReadEmbeddedJpeg(Ifd ifd) {
    var jifOff = ifd.Entries.FirstOrDefault(e => e.Tag == TagJpegInterchangeFormat);
    var jifLen = ifd.Entries.FirstOrDefault(e => e.Tag == TagJpegInterchangeFormatLength);
    if (jifOff == null || jifLen == null) return Array.Empty<byte>();
    // Length < 4 could hit inline; but these tags always point at the file.
    return ReadBytesAt(jifOff.ValueOrOffset, jifLen.ValueOrOffset);
  }

  /// <summary>Returns true when this IFD looks like an embedded JPEG preview.</summary>
  public static bool IsJpegPreviewIfd(Ifd ifd) =>
    ifd.Entries.Any(e => e.Tag == TagJpegInterchangeFormat) &&
    ifd.Entries.Any(e => e.Tag == TagJpegInterchangeFormatLength);

  /// <summary>Heuristic: photometric-interpretation in {32803 CFA, 34892 LinearRaw} → raw sensor IFD.</summary>
  public static bool IsRawSensorIfd(Ifd ifd) {
    var photo = ifd.Entries.FirstOrDefault(e => e.Tag == TagPhotometricInterpretation);
    if (photo == null) return false;
    var p = photo.ValueOrOffset;
    return p == 32803 || p == 34892;
  }

  // ----- byte-order helpers -----

  private ushort ReadUInt16(byte[] d, int pos) => this.IsBigEndian
    ? BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos))
    : BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pos));

  private uint ReadUInt32(byte[] d, int pos) => this.IsBigEndian
    ? BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos))
    : BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos));
}
