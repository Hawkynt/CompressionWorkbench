#pragma warning disable CS1591
using System.Buffers.Binary;
using FileFormat.FirmwareCommon;

namespace FileFormat.Trx;

/// <summary>
/// A Broadcom TRX firmware image (magic <c>HDR0</c>): a little-endian header carrying the total
/// length, a CRC-32 over everything after the CRC field, flags, a version (1 or 2) and three (v1)
/// or four (v2) partition offsets, followed by the partitions — typically loader, kernel, rootfs.
/// </summary>
/// <remarks>
/// Layout and checksum from binwalk v3 <c>src/structures/trx.rs</c> and <c>src/extractors/trx.rs</c>
/// (MIT, ReFirmLabs), converted: the CRC is the reflected CRC-32 register over bytes 12..length
/// with initial value 0xFFFFFFFF and <em>no</em> final inversion, and each partition runs to the
/// next non-zero offset or to the header's length.
/// </remarks>
public sealed class TrxImage {

  public const uint Magic = 0x30524448; // "HDR0"
  public const int CrcStart = 12;

  public ushort Flags { get; set; }
  public ushort Version { get; set; } = 1;
  /// <summary>Partition contents, in offset order.</summary>
  public List<byte[]> Partitions { get; set; } = [];
  /// <summary>The alignment each partition's start keeps when the image is laid out again.</summary>
  public List<int> Alignments { get; set; } = [];
  /// <summary>Bytes after the length the header declares.</summary>
  public byte[] Trailer { get; set; } = [];
  /// <summary>
  /// What the length is rounded up to: OpenWrt's <c>trx</c> and <c>otrx</c> round to 4 KiB (the zero
  /// fill belongs to the last partition); an existing image keeps the power of two its length has.
  /// </summary>
  public int LengthAlignment { get; set; } = 0x1000;

  public uint StoredCrc { get; private set; }
  public uint ComputedCrc { get; private set; }
  public bool CrcValid => this.StoredCrc == this.ComputedCrc;
  public uint Length { get; private set; }
  public uint[] Offsets { get; private set; } = [];

  public int HeaderSize => HeaderSizeOf(this.Version);
  public int MaxPartitions => this.Version >= 2 ? 4 : 3;

  public static int HeaderSizeOf(int version) => version >= 2 ? 32 : 28;

  public static TrxImage Parse(ReadOnlySpan<byte> d) {
    if (d.Length < 28 || BinaryPrimitives.ReadUInt32LittleEndian(d) != Magic)
      throw new InvalidDataException("TRX: no HDR0 header.");
    var img = new TrxImage {
      Length = BinaryPrimitives.ReadUInt32LittleEndian(d[4..]),
      StoredCrc = BinaryPrimitives.ReadUInt32LittleEndian(d[8..]),
      Flags = BinaryPrimitives.ReadUInt16LittleEndian(d[12..]),
      Version = BinaryPrimitives.ReadUInt16LittleEndian(d[14..]),
    };
    if (img.Version is not (1 or 2)) throw new InvalidDataException($"TRX: version {img.Version} is not 1 or 2.");
    var header = img.HeaderSize;
    if (d.Length < header) throw new InvalidDataException("TRX: header cut short.");
    if (img.Length <= header || img.Length > d.Length)
      throw new InvalidDataException($"TRX: declared length {img.Length} does not fit the {d.Length}-byte image.");
    var offsets = new List<uint>();
    for (var i = 0; i < img.MaxPartitions; ++i) {
      var offset = BinaryPrimitives.ReadUInt32LittleEndian(d[(16 + 4 * i)..]);
      if (offset > img.Length) throw new InvalidDataException($"TRX: partition {i} offset {offset} is past the length {img.Length}.");
      if (offset != 0) offsets.Add(offset);
    }
    img.Offsets = [.. offsets];
    for (var i = 0; i < offsets.Count; ++i) {
      var start = offsets[i];
      var end = i + 1 < offsets.Count ? offsets[i + 1] : img.Length;
      if (end < start || start < header) throw new InvalidDataException($"TRX: partition {i} spans {start}..{end}, which is not inside the image.");
      img.Partitions.Add(d[(int)start..(int)end].ToArray());
      img.Alignments.Add(AlignmentOf(start));
    }
    img.LengthAlignment = Math.Min(AlignmentOf(img.Length), 0x1000);
    img.ComputedCrc = FirmwareCrc32.Update(0xFFFFFFFFu, d[CrcStart..(int)img.Length]);
    img.Trailer = d[(int)img.Length..].ToArray();
    return img;
  }

  /// <summary>The largest power of two up to 64 KiB dividing <paramref name="offset"/>, at least 4.</summary>
  public static int AlignmentOf(uint offset) {
    var a = 4;
    while (a < 0x10000 && offset % (uint)(a * 2) == 0) a *= 2;
    return a;
  }

  /// <summary>
  /// Writes the image: header, then each partition at the next offset its alignment allows, the
  /// gaps zero, the length running to the end of the last partition rounded up to <see cref="LengthAlignment"/>, and
  /// the CRC computed over bytes 12..length.
  /// </summary>
  public byte[] ToBytes() {
    if (this.Version is not (1 or 2)) throw new InvalidOperationException($"TRX: version {this.Version} is not 1 or 2.");
    if (this.Partitions.Count > this.MaxPartitions)
      throw new InvalidOperationException($"TRX v{this.Version} holds at most {this.MaxPartitions} partitions; {this.Partitions.Count} given.");
    var header = this.HeaderSize;
    var offsets = new uint[this.Partitions.Count];
    long position = header;
    for (var i = 0; i < this.Partitions.Count; ++i) {
      var align = i < this.Alignments.Count ? this.Alignments[i] : 4;
      position = (position + align - 1) / align * align;
      offsets[i] = checked((uint)position);
      position += this.Partitions[i].Length;
    }
    var lengthAlign = Math.Max(4, this.LengthAlignment);
    var length = checked((int)((position + lengthAlign - 1) / lengthAlign * lengthAlign));
    if (length <= header || this.Partitions.All(p => p.Length == 0)) throw new InvalidOperationException("TRX: an image needs at least one non-empty partition; its length must exceed the header.");
    var image = new byte[length + this.Trailer.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(image, Magic);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)length);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(12), this.Flags);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(14), this.Version);
    for (var i = 0; i < offsets.Length; ++i) {
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16 + 4 * i), offsets[i]);
      this.Partitions[i].CopyTo(image, (int)offsets[i]);
    }
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), FirmwareCrc32.Update(0xFFFFFFFFu, image.AsSpan(CrcStart, length - CrcStart)));
    this.Trailer.CopyTo(image, length);
    return image;
  }
}
