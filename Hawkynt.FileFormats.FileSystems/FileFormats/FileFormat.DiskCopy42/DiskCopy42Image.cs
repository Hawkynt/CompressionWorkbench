using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.DiskCopy42;

/// <summary>
/// One Apple Disk Copy 4.2 image: an 84-byte big-endian header, the sector data and the
/// optional 12-byte-per-sector tag data.
/// </summary>
/// <remarks>
/// Layout and checksum rule from Apple's Disk Copy 4.2 format as implemented by MAME's
/// <c>src/lib/formats/ap_dsk35.cpp</c> (BSD-3-Clause, O. Galibert / R. Belmont); see
/// <c>THIRD-PARTY-NOTICE.Mame.txt</c> beside this file.
/// </remarks>
public sealed record DiskCopy42Image(byte[] Header, byte[] Data, byte[] Tags) {
  public const int HeaderSize = 0x54;
  public const int TagBytesPerSector = 12;
  private const int NameCapacity = 63;
  private const ushort PrivateMagic = 0x0100;

  /// <summary>The Pascal-string disk name (MacRoman bytes, rendered byte-for-byte as Latin-1).</summary>
  public string Name => Encoding.Latin1.GetString(this.Header, 1, Math.Min(this.Header[0], (byte)NameCapacity));

  /// <summary>Disk encoding byte at 0x50 (0 = GCR 400K, 1 = GCR 800K, 2 = MFM 720K, 3 = MFM 1440K).</summary>
  public byte DiskEncoding => this.Header[0x50];

  /// <summary>Format byte at 0x51: the GCR format nybble (0x02 Mac 400K, 0x12 Lisa, 0x22 Mac 800K, 0x24 ProDOS 800K) or MFM sector-size/sides bits (0x22).</summary>
  public byte FormatByte => this.Header[0x51];

  public uint StoredDataChecksum => BinaryPrimitives.ReadUInt32BigEndian(this.Header.AsSpan(0x48));
  public uint StoredTagChecksum => BinaryPrimitives.ReadUInt32BigEndian(this.Header.AsSpan(0x4C));
  public uint ComputedDataChecksum => Checksum(this.Data);
  public uint ComputedTagChecksum => this.Tags.Length > TagBytesPerSector ? Checksum(this.Tags.AsSpan(TagBytesPerSector)) : 0;
  public bool DataChecksumValid => this.StoredDataChecksum == this.ComputedDataChecksum;
  public bool TagChecksumValid => this.StoredTagChecksum == this.ComputedTagChecksum;

  /// <summary>
  /// Disk Copy 4.2 checksum: add each big-endian 16-bit word, then rotate the 32-bit sum
  /// right by one. Tag checksums skip the first sector's 12 tag bytes.
  /// </summary>
  public static uint Checksum(ReadOnlySpan<byte> data) {
    var sum = 0u;
    for (var i = 0; i + 1 < data.Length; i += 2) {
      sum += BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
      sum = uint.RotateRight(sum, 1);
    }
    return sum;
  }

  /// <summary>True when <paramref name="prefix"/> (the start of a file of <paramref name="length"/> bytes) has a coherent header.</summary>
  public static bool LooksLikeHeader(ReadOnlySpan<byte> prefix, long length) {
    if (prefix.Length < HeaderSize || length < HeaderSize)
      return false;
    var dataSize = BinaryPrimitives.ReadUInt32BigEndian(prefix[0x40..]);
    var tagSize = BinaryPrimitives.ReadUInt32BigEndian(prefix[0x44..]);
    return prefix[0] <= NameCapacity &&
           BinaryPrimitives.ReadUInt16BigEndian(prefix[0x52..]) == PrivateMagic &&
           (dataSize & 1) == 0 && (tagSize & 1) == 0 &&
           HeaderSize + (long)dataSize + tagSize <= length;
  }

  /// <summary>Parses a whole image.</summary>
  /// <exception cref="InvalidDataException">The header is not a Disk Copy 4.2 header or the file is truncated.</exception>
  public static DiskCopy42Image Read(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    var header = new byte[HeaderSize];
    if (stream.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) != HeaderSize)
      throw new InvalidDataException("Disk Copy 4.2: file shorter than the 84-byte header.");
    var length = stream.CanSeek ? stream.Length : long.MaxValue;
    if (!LooksLikeHeader(header, length))
      throw new InvalidDataException("Disk Copy 4.2: header magic 0x0100, name length or sizes are invalid.");
    var data = ReadExact(stream, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x40)));
    var tags = ReadExact(stream, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x44)));
    return new(header, data, tags);
  }

  /// <summary>Builds a fresh header for <paramref name="data"/>/<paramref name="tags"/>, deriving the encoding from the size.</summary>
  public static DiskCopy42Image Create(string name, byte[] data, byte[] tags, byte? encoding = null, byte? formatByte = null) {
    var header = new byte[HeaderSize];
    var nameBytes = System.Text.Encoding.Latin1.GetBytes(name);
    if (nameBytes.Length > NameCapacity)
      throw new ArgumentException($"Disk Copy 4.2 names hold at most {NameCapacity} bytes.", nameof(name));
    header[0] = (byte)nameBytes.Length;
    nameBytes.CopyTo(header, 1);
    if (encoding is null || formatByte is null) {
      var (defaultEncoding, defaultFormat) = DefaultsFor(data.Length);
      encoding ??= defaultEncoding;
      formatByte ??= defaultFormat;
    }
    header[0x50] = encoding.Value;
    header[0x51] = formatByte.Value;
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0x52), PrivateMagic);
    return new DiskCopy42Image(header, data, tags).WithPayload(data, tags);
  }

  /// <summary>Returns a copy with new data/tags and recomputed sizes and checksums; every other header byte is kept.</summary>
  public DiskCopy42Image WithPayload(byte[] data, byte[] tags) {
    if ((data.Length & 1) != 0 || (tags.Length & 1) != 0)
      throw new ArgumentException("Disk Copy 4.2 data and tag sizes must be even (the checksum runs over 16-bit words).");
    var header = (byte[])this.Header.Clone();
    var image = new DiskCopy42Image(header, data, tags);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x40), (uint)data.Length);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x44), (uint)tags.Length);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x48), image.ComputedDataChecksum);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0x4C), image.ComputedTagChecksum);
    return image;
  }

  /// <summary>Returns a copy with name/encoding/format changed; other header bytes are kept.</summary>
  public DiskCopy42Image WithHeaderFields(string? name, byte? encoding, byte? formatByte) {
    var header = (byte[])this.Header.Clone();
    if (name is not null) {
      var nameBytes = System.Text.Encoding.Latin1.GetBytes(name);
      if (nameBytes.Length > NameCapacity)
        throw new ArgumentException($"Disk Copy 4.2 names hold at most {NameCapacity} bytes.", nameof(name));
      header.AsSpan(0, 1 + NameCapacity).Clear();
      header[0] = (byte)nameBytes.Length;
      nameBytes.CopyTo(header, 1);
    }
    if (encoding is { } e) header[0x50] = e;
    if (formatByte is { } f) header[0x51] = f;
    return this with { Header = header };
  }

  public void Write(Stream output) {
    output.Write(this.Header);
    output.Write(this.Data);
    output.Write(this.Tags);
  }

  /// <summary>The <c>metadata.ini</c> rendering.</summary>
  public byte[] RenderMetadata() {
    var sb = new StringBuilder();
    sb.Append("[diskcopy42]\n");
    sb.Append("name=").Append(this.Name).Append('\n');
    sb.Append("encoding=0x").Append(this.DiskEncoding.ToString("X2", CultureInfo.InvariantCulture)).Append(" (").Append(EncodingName(this.DiskEncoding)).Append(")\n");
    sb.Append("format=0x").Append(this.FormatByte.ToString("X2", CultureInfo.InvariantCulture)).Append('\n');
    sb.Append("data_size=").Append(this.Data.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
    sb.Append("tag_size=").Append(this.Tags.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
    sb.Append("data_checksum=0x").Append(this.StoredDataChecksum.ToString("X8", CultureInfo.InvariantCulture))
      .Append(this.DataChecksumValid ? " (valid)\n" : $" (INVALID, computed 0x{this.ComputedDataChecksum:X8})\n");
    sb.Append("tag_checksum=0x").Append(this.StoredTagChecksum.ToString("X8", CultureInfo.InvariantCulture))
      .Append(this.TagChecksumValid ? " (valid)\n" : $" (INVALID, computed 0x{this.ComputedTagChecksum:X8})\n");
    return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
  }

  /// <summary>Parses the editable keys of a <c>metadata.ini</c>: name, encoding, format.</summary>
  public static (string? Name, byte? Encoding, byte? Format) ParseMetadata(byte[] ini) {
    string? name = null;
    byte? encoding = null, format = null;
    foreach (var rawLine in System.Text.Encoding.UTF8.GetString(ini).Split('\n')) {
      var line = rawLine.TrimEnd('\r');
      var eq = line.IndexOf('=');
      if (eq <= 0 || line.StartsWith('[') || line.StartsWith(';')) continue;
      var key = line[..eq].Trim();
      var value = line[(eq + 1)..];
      switch (key.ToLowerInvariant()) {
        case "name": name = value; break;
        case "encoding": encoding = ParseByte(value); break;
        case "format": format = ParseByte(value); break;
      }
    }
    return (name, encoding, format);
  }

  private static byte ParseByte(string value) {
    var token = value.Trim().Split(' ', 2)[0];
    return token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
      ? byte.Parse(token.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
      : byte.Parse(token, CultureInfo.InvariantCulture);
  }

  private static (byte Encoding, byte Format) DefaultsFor(int dataSize) => dataSize switch {
    409600 => (0x00, 0x02),
    819200 => (0x01, 0x22),
    737280 => (0x02, 0x22),
    1474560 => (0x03, 0x22),
    _ => throw new ArgumentException(
      $"Disk Copy 4.2 holds 400K, 800K, 720K or 1440K disks; {dataSize} bytes needs an explicit encoding in metadata.ini."),
  };

  private static string EncodingName(byte encoding) => encoding switch {
    0 => "GCR CLV ssdd 400K",
    1 => "GCR CLV dsdd 800K",
    2 => "MFM CAV dsdd 720K",
    3 => "MFM CAV dshd 1440K",
    _ => "other",
  };

  private static byte[] ReadExact(Stream stream, uint size) {
    var buffer = new byte[checked((int)size)];
    if (stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) != buffer.Length)
      throw new InvalidDataException("Disk Copy 4.2: file truncated before the declared data/tag size.");
    return buffer;
  }
}
