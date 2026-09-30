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

  private StructuralLayout Layout { get; }

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
      dngTag != null ? (int)Math.Min(dngTag.Count, int.MaxValue) : 0);
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
    var total = 0L;
    for (var index = 0; index < offsets.Count; ++index) {
      var offset = (long)offsets[index];
      var length = (long)sizes[index];
      if (offset < 0 || length <= 0 || offset > data.Length
          || length > data.Length - offset
          || offset > int.MaxValue || length > int.MaxValue)
        continue;

      // Strips may repeat source bytes; a payload that could not fit one array
      // is unreadable rather than a reason for the listing to throw.
      total += length;
      if (total > Array.MaxLength)
        return [];
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
    var total = (long)typeSize * count;
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
    // One IFD walker serves both paths; the owning reader is its layout.
    var layout = this.Layout = ReadLayout(data);
    this.IsBigEndian = layout.IsBigEndian;
    this.TopLevelIfds = layout.TopLevelIfds;
    this.SubIfds = layout.SubIfds;
    this.ExifIfd = layout.ExifIfd;
    this.DngVersionLength = layout.DngVersionLength;
  }

  /// <summary>
  /// Reads the values as u int 32 from the supplied input.
  /// </summary>
  public IReadOnlyList<uint> ReadValuesAsUInt32(Entry e) => ReadValuesAsUInt32Layout(this._data, this.IsBigEndian, e);

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
  public byte[] ReadStripBytes(Ifd ifd) => this.Materialize(GetStripRanges(this._data, this.Layout, ifd));

  /// <summary>Extracts an embedded JPEG preview using tags 0x0201/0x0202, or empty if absent.</summary>
  public byte[] ReadEmbeddedJpeg(Ifd ifd) => this.Materialize(GetEmbeddedJpegRanges(this._data, ifd));

  private byte[] Materialize(IReadOnlyList<SourceRange> ranges) {
    var result = new byte[GetTotalLength(ranges)];
    var written = 0;
    foreach (var range in ranges) {
      this._data.AsSpan(range.Offset, range.Length).CopyTo(result.AsSpan(written));
      written += range.Length;
    }
    return result;
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
}
