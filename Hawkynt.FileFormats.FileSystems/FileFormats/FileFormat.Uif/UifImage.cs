#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Uif;

/// <summary>
/// A MagicISO UIF image. A 64-byte <c>bbis</c> trailer ends the file. It points at a <c>blhr</c> block
/// table (zlib-compressed when flagged); each 24-byte entry places a run of sectors in the output and
/// says how it is stored: copied, zero-filled or zlib-compressed. Layout in <c>docs/UIF-ON-DISK.md</c>.
/// Only what real files proved is read. Version 1 and 2 images of type 8 (a plain ISO image) without
/// encryption are read; LZMA-compressed version 3 images, mixed-mode images (type 9) and encrypted
/// images are refused.
/// </summary>
public sealed class UifImage {
  public const int TrailerSize = 64;
  public const int EntrySize = 24;

  /// <summary>One run of output sectors.</summary>
  public sealed record Block(ulong Offset, uint StoredSize, uint Sector, uint Sectors, uint Type);

  public ushort Version { get; private init; }
  public ushort ImageType { get; private init; }
  public uint Sectors { get; private init; }
  public uint SectorSize { get; private init; }
  public uint LastDiff { get; private init; }
  public byte[] Hash { get; private init; } = [];
  public IReadOnlyList<Block> Blocks { get; private init; } = [];

  /// <summary>Bytes of the image: all sectors, less the unused tail of the last one when the trailer says so.</summary>
  public ulong Length => (ulong)this.Sectors * this.SectorSize - (this.LastDiff == 0 ? 0 : this.SectorSize - this.LastDiff);

  public static UifImage Read(Stream file) {
    var length = file.Length;
    if (length < TrailerSize) throw new InvalidDataException("UIF: too short for a trailer.");
    var t = ReadAt(file, length - TrailerSize, TrailerSize);
    if (!t.AsSpan(0, 4).SequenceEqual("bbis"u8)) throw new InvalidDataException("UIF: missing 'bbis' trailer.");
    var version = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(8));
    var imageType = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(10));
    var sectors = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(16));
    var sectorSize = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(20));
    var lastDiff = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(24));
    var tableAt = BinaryPrimitives.ReadUInt64LittleEndian(t.AsSpan(28));
    var (key1, key2) = (t[56], t[57]);
    // Encryption: a key in the trailer (version 2 and later) or a password block instead of the table.
    if (key2 == 2 || (version >= 2 && key1 is > 0 and <= 0x10)) throw new NotSupportedException("UIF: encrypted images are not supported.");
    if (version >= 3) throw new NotSupportedException("UIF: version 3 (LZMA) images are not supported.");
    if (imageType != 8) throw new NotSupportedException($"UIF: image type {imageType} (only 8, a plain ISO image, is supported).");
    if (sectorSize is 0 or > 1 << 16 || lastDiff >= sectorSize) throw new InvalidDataException("UIF: impossible sector geometry.");
    if (tableAt + 16 > (ulong)length) throw new InvalidDataException("UIF: the block table lies outside the file.");

    var head = ReadAt(file, (long)tableAt, 16);
    if (head.AsSpan(0, 4).SequenceEqual("bsdr"u8)) throw new NotSupportedException("UIF: password-protected images are not supported.");
    if (!head.AsSpan(0, 4).SequenceEqual("blhr"u8)) throw new InvalidDataException("UIF: missing 'blhr' block table.");
    var size = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
    var compressed = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
    var count = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12));
    if (size < 8 || tableAt + 8 + size > (ulong)length || count > int.MaxValue / EntrySize) throw new InvalidDataException("UIF: the block table is cut short.");
    var raw = ReadAt(file, (long)tableAt + 16, (int)size - 8);
    var table = compressed != 0 ? Inflate(raw) : raw;
    if (table.Length != count * EntrySize) throw new InvalidDataException($"UIF: the block table holds {table.Length} bytes for {count} entries.");

    var blocks = new List<Block>((int)count);
    for (var i = 0; i < count; ++i) {
      var e = table.AsSpan(i * EntrySize, EntrySize);
      var block = new Block(BinaryPrimitives.ReadUInt64LittleEndian(e), BinaryPrimitives.ReadUInt32LittleEndian(e[8..]),
        BinaryPrimitives.ReadUInt32LittleEndian(e[12..]), BinaryPrimitives.ReadUInt32LittleEndian(e[16..]), BinaryPrimitives.ReadUInt32LittleEndian(e[20..]));
      if (block.Type is not (1 or 3 or 5)) throw new InvalidDataException($"UIF: block {i} has unknown type {block.Type}.");
      if ((ulong)block.Sector + block.Sectors > sectors) throw new InvalidDataException($"UIF: block {i} reaches past sector {sectors}.");
      if (block.Type != 3 && block.Offset + block.StoredSize > tableAt) throw new InvalidDataException($"UIF: block {i} lies outside the data area.");
      if ((ulong)block.Sectors * sectorSize > int.MaxValue) throw new InvalidDataException($"UIF: block {i} is too large.");
      blocks.Add(block);
    }
    return new UifImage {
      Version = version, ImageType = imageType, Sectors = sectors, SectorSize = sectorSize, LastDiff = lastDiff,
      Hash = t.AsSpan(40, 16).ToArray(), Blocks = blocks,
    };
  }

  /// <summary>Writes the image: every block decoded into place, sectors no block covers left as zeros.</summary>
  public void CopyTo(Stream file, Stream output) {
    var start = output.Position;
    foreach (var block in this.Blocks) {
      var bytes = (int)(block.Sectors * this.SectorSize);
      var data = block.Type switch {
        3 => new byte[bytes],
        1 => Stored(ReadAt(file, (long)block.Offset, (int)block.StoredSize), bytes),
        _ => Inflate(ReadAt(file, (long)block.Offset, (int)block.StoredSize)),
      };
      if (data.Length != bytes) throw new InvalidDataException($"UIF: the block at sector {block.Sector} decodes to {data.Length} bytes, not {bytes}.");
      var at = (ulong)block.Sector * this.SectorSize;
      var keep = (int)Math.Min((ulong)bytes, this.Length - Math.Min(at, this.Length));
      output.Position = start + (long)at;
      output.Write(data, 0, keep);
    }
    output.SetLength(start + (long)this.Length);
  }

  private static byte[] Stored(byte[] data, int bytes) {
    if (data.Length > bytes) throw new InvalidDataException("UIF: a stored block is larger than its sectors.");
    if (data.Length == bytes) return data;
    var padded = new byte[bytes];
    data.CopyTo(padded, 0);
    return padded;
  }

  private static byte[] Inflate(byte[] data) {
    try {
      return FileFormat.Zlib.ZlibStream.Decompress(data);
    } catch (Exception ex) when (ex is not InvalidDataException) {
      throw new InvalidDataException("UIF: a zlib block does not decode.", ex);
    }
  }

  private static byte[] ReadAt(Stream file, long offset, int count) {
    var buffer = new byte[count];
    file.Position = offset;
    try {
      file.ReadExactly(buffer);
    } catch (EndOfStreamException ex) {
      throw new InvalidDataException("UIF: the image is truncated.", ex);
    }
    return buffer;
  }

  public string RenderMetadata() {
    var sb = new StringBuilder("[Uif]\n");
    sb.Append(CultureInfo.InvariantCulture, $"version={this.Version}\nimage_type={this.ImageType}\nsectors={this.Sectors}\nsector_size={this.SectorSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"last_sector_bytes={this.LastDiff}\nblocks={this.Blocks.Count}\nhash={Convert.ToHexStringLower(this.Hash)}\n");
    foreach (var type in this.Blocks.GroupBy(b => b.Type).OrderBy(g => g.Key))
      sb.Append(CultureInfo.InvariantCulture, $"blocks_type_{type.Key}={type.Count()}\n");
    return sb.ToString();
  }
}
