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
  public const ushort TagSubIFDs = 0x014A;
  public const ushort TagStripOffsets = 0x0111;
  public const ushort TagStripByteCounts = 0x0117;
  public const ushort TagJpegInterchangeFormat = 0x0201;
  public const ushort TagJpegInterchangeFormatLength = 0x0202;
  public const ushort TagCompression = 0x0103;
  public const ushort TagPhotometricInterpretation = 0x0106;
  public const ushort TagNewSubFileType = 0x00FE;
  public const ushort TagExifIfd = 0x8769;
  public const ushort TagMakerNote = 0x927C;
  public const ushort TagDngVersion = 0xC612;

  public sealed record Ifd(long Offset, IReadOnlyList<Entry> Entries);
  public sealed record Entry(ushort Tag, ushort Type, uint Count, uint ValueOrOffset);

  public bool IsBigEndian { get; }
  public IReadOnlyList<Ifd> TopLevelIfds { get; }
  public IReadOnlyList<Ifd> SubIfds { get; }
  public Ifd? ExifIfd { get; }
  public byte[] Raw { get; }
  /// <summary>Size of the <c>DNGVersion</c> tag value or 0 when absent. Used to filter plain TIFF.</summary>
  public int DngVersionLength { get; }

  private readonly byte[] _data;

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
