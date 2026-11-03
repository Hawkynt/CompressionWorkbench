#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FileFormat.Seama;

/// <summary>One SEAMA header with its meta strings and, when it carries an image, the image and its MD5.</summary>
public sealed class SeamaEntity {
  public List<string> Meta { get; set; } = [];
  public byte[] Image { get; set; } = [];
  public byte[] StoredMd5 { get; set; } = [];
  public bool Md5Valid => this.Image.Length == 0 || MD5.HashData(this.Image).AsSpan().SequenceEqual(this.StoredMd5);
}

/// <summary>
/// A D-Link SEAMA image (magic 0x5EA3A417): either a single entity — header, MD5 of the image,
/// NUL-separated <c>key=value</c> meta strings, image — or a <em>seal</em>, a header with an empty
/// image whose meta (typically the firmware <c>signature=</c>) is followed by one or more entities.
/// The seal is what binwalk reports as a DLOB header.
/// </summary>
/// <remarks>
/// Layout from binwalk v3 <c>src/structures/seama.rs</c> and <c>dlob.rs</c> (MIT), with the meta
/// encoding, MD5 coverage and seal structure measured from OpenWrt firmware-utils <c>seama</c> /
/// <c>oseama</c> run as black boxes: the 12-byte header is magic, a reserved 16-bit word, the 16-bit
/// meta size and the 32-bit image size; the MD5 (present only when the image size is non-zero)
/// covers the image; meta strings are NUL-terminated and the block is zero-padded to four bytes.
/// Big-endian is what the tools write; a little-endian image is read and rewritten as such.
/// </remarks>
public sealed class SeamaImage {

  public const uint Magic = 0x5EA3A417;
  public const int HeaderSize = 12;

  public bool BigEndian { get; set; } = true;
  /// <summary>Null for a single entity; the seal's own meta otherwise.</summary>
  public List<string>? SealMeta { get; set; }
  public List<SeamaEntity> Entities { get; set; } = [];
  public byte[] Trailer { get; set; } = [];

  public bool IsSeal => this.SealMeta is not null;

  public static SeamaImage Parse(ReadOnlySpan<byte> d) {
    if (d.Length < HeaderSize) throw new InvalidDataException("SEAMA: shorter than a header.");
    var big = BinaryPrimitives.ReadUInt32BigEndian(d) == Magic;
    if (!big && BinaryPrimitives.ReadUInt32LittleEndian(d) != Magic) throw new InvalidDataException("SEAMA: no 0x5EA3A417 magic.");
    var img = new SeamaImage { BigEndian = big };
    var offset = 0;
    var first = ReadEntity(d, ref offset, big);
    if (first.Image.Length == 0 && offset < d.Length && d.Length - offset >= HeaderSize && ReadU32(d[offset..], big) == Magic) {
      img.SealMeta = first.Meta;
      while (d.Length - offset >= HeaderSize && ReadU32(d[offset..], big) == Magic)
        img.Entities.Add(ReadEntity(d, ref offset, big));
    } else
      img.Entities.Add(first);
    img.Trailer = d[offset..].ToArray();
    return img;
  }

  private static uint ReadU32(ReadOnlySpan<byte> d, bool big) => big ? BinaryPrimitives.ReadUInt32BigEndian(d) : BinaryPrimitives.ReadUInt32LittleEndian(d);
  private static ushort ReadU16(ReadOnlySpan<byte> d, bool big) => big ? BinaryPrimitives.ReadUInt16BigEndian(d) : BinaryPrimitives.ReadUInt16LittleEndian(d);

  private static SeamaEntity ReadEntity(ReadOnlySpan<byte> d, ref int offset, bool big) {
    var h = d[offset..];
    if (h.Length < HeaderSize || ReadU32(h, big) != Magic) throw new InvalidDataException($"SEAMA: no header at offset {offset}.");
    var metaSize = ReadU16(h[6..], big);
    var size = ReadU32(h[8..], big);
    var md5Size = size > 0 ? 16 : 0;
    long end = (long)offset + HeaderSize + md5Size + metaSize + size;
    if (end > d.Length) throw new InvalidDataException($"SEAMA: the entity at {offset} declares {metaSize} meta and {size} image bytes past the end of the file.");
    var entity = new SeamaEntity { StoredMd5 = h.Slice(HeaderSize, md5Size).ToArray() };
    var meta = h.Slice(HeaderSize + md5Size, metaSize);
    foreach (var s in Encoding.Latin1.GetString(meta).Split('\0'))
      if (s.Length > 0) entity.Meta.Add(s);
    entity.Image = h.Slice(HeaderSize + md5Size + metaSize, (int)size).ToArray();
    offset = (int)end;
    return entity;
  }

  /// <summary>Writes the image as <c>seama</c> writes it: meta strings NUL-terminated and padded to four bytes, MD5 recomputed.</summary>
  public byte[] ToBytes() {
    using var ms = new MemoryStream();
    if (this.SealMeta is { } seal) WriteEntity(ms, seal, []);
    else if (this.Entities.Count != 1) throw new InvalidOperationException($"SEAMA: a single-entity image holds exactly one image, not {this.Entities.Count}.");
    foreach (var e in this.Entities) WriteEntity(ms, e.Meta, e.Image);
    ms.Write(this.Trailer);
    return ms.ToArray();
  }

  private void WriteEntity(Stream output, List<string> meta, byte[] image) {
    var text = new MemoryStream();
    foreach (var m in meta) {
      if (m.Contains('\0')) throw new InvalidOperationException("SEAMA: a meta string cannot contain NUL.");
      text.Write(Encoding.Latin1.GetBytes(m));
      text.WriteByte(0);
    }
    while (text.Length % 4 != 0) text.WriteByte(0);
    if (text.Length > ushort.MaxValue) throw new InvalidOperationException("SEAMA: meta block exceeds 65535 bytes.");
    var header = new byte[HeaderSize];
    if (this.BigEndian) {
      BinaryPrimitives.WriteUInt32BigEndian(header, Magic);
      BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)text.Length);
      BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)image.Length);
    } else {
      BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
      BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)text.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)image.Length);
    }
    output.Write(header);
    if (image.Length > 0) output.Write(MD5.HashData(image));
    text.Position = 0;
    text.CopyTo(output);
    output.Write(image);
  }
}
