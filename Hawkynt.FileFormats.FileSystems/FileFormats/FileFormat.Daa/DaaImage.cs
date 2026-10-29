using System.Buffers.Binary;
using Compression.Core.Deflate;

namespace FileFormat.Daa;

/// <summary>
/// PowerISO DAA image, format version 0x100 (single volume, unencrypted): a 76-byte header,
/// a table of 3-byte compressed chunk lengths and raw-deflate chunks. Implemented from
/// <c>docs/DAA-ON-DISK.md</c>.
/// </summary>
public sealed class DaaImage {
  public const int HeaderSize = 0x4C;
  public const uint Version100 = 0x100;
  public const uint Version110 = 0x110;
  public const int DefaultChunkSize = 0x10000;

  public uint Version { get; private init; }
  public uint ChunkSize { get; private init; }
  public long IsoSize { get; private init; }
  public IReadOnlyList<(long Offset, int Length)> Chunks { get; private init; } = [];

  /// <exception cref="InvalidDataException">Not a DAA image, or inconsistent.</exception>
  /// <exception cref="NotSupportedException">Version 0x110, split, encrypted or gBurner images.</exception>
  public static DaaImage Read(Stream stream) {
    stream.Position = 0;
    var h = new byte[HeaderSize];
    if (stream.ReadAtLeast(h, HeaderSize, throwOnEndOfStream: false) != HeaderSize)
      throw new InvalidDataException("DAA: file shorter than the 76-byte header.");
    var signature = h.AsSpan(0, 16);
    if (signature.StartsWith("DAA VOL"u8) || signature.StartsWith("GBI VOL"u8))
      throw new NotSupportedException("DAA: this is a later volume of a split image; open the first part.");
    if (signature.StartsWith("GBI\0"u8))
      throw new NotSupportedException("DAA: gBurner GBI images obfuscate their chunk table and are not supported.");
    if (!signature.SequenceEqual("DAA\0\0\0\0\0\0\0\0\0\0\0\0\0"u8))
      throw new InvalidDataException("DAA: \"DAA\" signature missing.");
    if (Crc32(h.AsSpan(0, 0x48)) != BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x48)))
      throw new InvalidDataException("DAA: header CRC mismatch.");

    var sizeOffset = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x10));
    var version = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x14));
    var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x18));
    var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x24));
    var isoSize = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(0x28));
    var daaSize = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(0x30));
    if (version == Version110)
      throw new NotSupportedException("DAA: format version 0x110 (bit-packed, optionally LZMA chunk table) is not supported yet.");
    if (version != Version100)
      throw new InvalidDataException($"DAA: unknown format version 0x{version:X}.");
    if (daaSize != stream.Length)
      throw new NotSupportedException("DAA: the header size differs from the file size — a split (multi-volume) image.");
    if (sizeOffset < HeaderSize || dataOffset < sizeOffset || dataOffset > stream.Length || chunkSize == 0 || chunkSize > 1 << 24 || isoSize < 0)
      throw new InvalidDataException("DAA: header offsets or sizes are invalid.");

    // Descriptors between the header and the chunk table: type 1 = multi-volume, 3 = password.
    var position = (long)HeaderSize;
    Span<byte> d = stackalloc byte[8];
    while (position < sizeOffset) {
      stream.Position = position;
      stream.ReadExactly(d);
      var type = BinaryPrimitives.ReadUInt32LittleEndian(d);
      var length = BinaryPrimitives.ReadUInt32LittleEndian(d[4..]);
      if (type == 3) throw new NotSupportedException("DAA: password-protected images are not supported.");
      if (type == 1) throw new NotSupportedException("DAA: multi-volume images are not supported.");
      if (length < 8) throw new InvalidDataException("DAA: descriptor length is invalid.");
      position += length;
    }

    var table = new byte[dataOffset - sizeOffset];
    stream.Position = sizeOffset;
    stream.ReadExactly(table);
    var count = table.Length / 3;
    if ((long)count * chunkSize < isoSize)
      throw new InvalidDataException("DAA: the chunk table is too short for the ISO size.");
    var chunks = new List<(long, int)>(count);
    long offset = dataOffset;
    for (var i = 0; i < count; ++i) {
      var length = table[3 * i] << 16 | table[3 * i + 1] | table[3 * i + 2] << 8;
      chunks.Add((offset, length));
      offset += length;
    }
    if (offset > stream.Length)
      throw new InvalidDataException("DAA: chunk data runs past the end of the file.");
    return new DaaImage { Version = version, ChunkSize = chunkSize, IsoSize = isoSize, Chunks = chunks };
  }

  public void CopyIsoTo(Stream source, Stream output) {
    var written = 0L;
    for (var i = 0; i < this.Chunks.Count && written < this.IsoSize; ++i) {
      var (offset, length) = this.Chunks[i];
      var packed = new byte[length];
      source.Position = offset;
      source.ReadExactly(packed);
      if (packed.Length < 4 || Crc32(packed.AsSpan(0, packed.Length - 4)) != BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(packed.Length - 4)))
        throw new InvalidDataException($"DAA: chunk {i} fails its CRC-32.");
      var data = DeflateDecompressor.Decompress(packed.AsSpan(0, packed.Length - 4));
      var want = (int)Math.Min(this.ChunkSize, this.IsoSize - written);
      if (data.Length < want || data.Length > this.ChunkSize)
        throw new InvalidDataException($"DAA: chunk {i} decodes to {data.Length} bytes, expected {want}.");
      output.Write(data, 0, want);
      written += want;
    }
    if (written != this.IsoSize)
      throw new InvalidDataException("DAA: chunks end before the ISO size.");
  }

  /// <summary>Writes a version 0x100 image: header, 3-byte length table, raw-deflate chunks each followed by its CRC-32.</summary>
  public static void Write(Stream output, ReadOnlySpan<byte> iso, int chunkSize = DefaultChunkSize, DeflateCompressionLevel level = DeflateCompressionLevel.Default) {
    if (chunkSize <= 0 || chunkSize > 1 << 24)
      throw new ArgumentOutOfRangeException(nameof(chunkSize));
    var count = Math.Max(1, (iso.Length + chunkSize - 1) / chunkSize);
    var packed = new List<byte[]>(count);
    for (var i = 0; i < count; ++i) {
      var slice = iso.Slice(Math.Min(i * chunkSize, iso.Length), Math.Clamp(iso.Length - i * chunkSize, 0, chunkSize));
      var deflated = DeflateCompressor.Compress(slice, level);
      var chunk = new byte[deflated.Length + 4];
      deflated.CopyTo(chunk, 0);
      BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(deflated.Length), Crc32(deflated));
      if (chunk.Length > 0xFFFFFF) throw new InvalidDataException("DAA: a compressed chunk exceeds the 24-bit length field.");
      packed.Add(chunk);
    }
    var sizeOffset = HeaderSize;
    var dataOffset = sizeOffset + 3 * count;
    var total = dataOffset + packed.Sum(c => (long)c.Length);

    var h = new byte[HeaderSize];
    "DAA"u8.CopyTo(h);
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x10), (uint)sizeOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x14), Version100);
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x18), (uint)dataOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x1C), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x24), (uint)chunkSize);
    BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(0x28), iso.Length);
    BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(0x30), total);
    BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x48), Crc32(h.AsSpan(0, 0x48)));
    output.Write(h);
    Span<byte> entry = stackalloc byte[3];
    foreach (var c in packed) {
      entry[0] = (byte)(c.Length >> 16);
      entry[1] = (byte)c.Length;
      entry[2] = (byte)(c.Length >> 8);
      output.Write(entry);
    }
    foreach (var c in packed) output.Write(c);
  }

  private static uint Crc32(ReadOnlySpan<byte> data) {
    var crc = 0xFFFFFFFFu;
    foreach (var b in data) {
      crc ^= b;
      for (var k = 0; k < 8; ++k) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
    }
    return ~crc;
  }
}
