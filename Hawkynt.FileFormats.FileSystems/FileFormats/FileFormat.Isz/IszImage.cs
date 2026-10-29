using System.Buffers.Binary;
using FileFormat.Bzip2;

namespace FileFormat.Isz;

/// <summary>
/// UltraISO compressed ISO (ISZ v1), single segment. Implemented from EZB Systems' published
/// specification plus the measured details recorded in <c>docs/ISZ-ON-DISK.md</c>.
/// </summary>
public sealed class IszImage {
  public const int HeaderSize = 64;
  private static ReadOnlySpan<byte> Signature => "IsZ!"u8;
  private static ReadOnlySpan<byte> PointerKey => [0xB6, 0x8C, 0xA5, 0xDE];
  public const int DefaultBlockSize = 0x8000;

  public enum ChunkKind : byte { Zero = 0, Data = 1, Zlib = 2, Bzip2 = 3 }

  public readonly record struct Chunk(ChunkKind Kind, int Length, long Offset);

  public byte Version { get; private init; }
  public uint VolumeSerial { get; private init; }
  public ushort SectorSize { get; private init; }
  public uint TotalSectors { get; private init; }
  public byte Encryption { get; private init; }
  public uint BlockSize { get; private init; }
  public uint DataChecksum { get; private init; }
  public uint PackedChecksum { get; private init; }
  public IReadOnlyList<Chunk> Chunks { get; private init; } = [];
  public long ImageSize => (long)this.SectorSize * this.TotalSectors;

  /// <exception cref="InvalidDataException">Not ISZ, truncated or inconsistent.</exception>
  /// <exception cref="NotSupportedException">Encrypted or segmented images.</exception>
  public static IszImage Read(Stream stream) {
    stream.Position = 0;
    Span<byte> h = stackalloc byte[HeaderSize];
    if (stream.ReadAtLeast(h, HeaderSize, throwOnEndOfStream: false) != HeaderSize || !h[..4].SequenceEqual(Signature))
      throw new InvalidDataException("ISZ: \"IsZ!\" signature missing.");
    var version = h[5];
    var sectorSize = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]);
    var totalSectors = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
    var encryption = h[16];
    var blocks = BinaryPrimitives.ReadUInt32LittleEndian(h[25..]);
    var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(h[29..]);
    var pointerLength = h[33];
    var pointerOffset = BinaryPrimitives.ReadUInt32LittleEndian(h[35..]);
    var segmentOffset = BinaryPrimitives.ReadUInt32LittleEndian(h[39..]);
    var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(h[43..]);
    var size1 = BinaryPrimitives.ReadUInt32LittleEndian(h[52..]);
    if (version != 1) throw new NotSupportedException($"ISZ: version {version} is not supported.");
    if (encryption != 0) throw new NotSupportedException("ISZ: password-protected/encrypted images are not supported.");
    if (segmentOffset != 0) throw new NotSupportedException("ISZ: split (multi-segment) images need their .i01.. siblings.");
    if (sectorSize == 0 || blockSize == 0 || blockSize % sectorSize != 0 || blockSize > 1 << 24)
      throw new InvalidDataException("ISZ: sector or chunk size is invalid.");
    if (dataOffset < HeaderSize || dataOffset > stream.Length)
      throw new InvalidDataException("ISZ: data offset is outside the file.");

    var chunks = new List<Chunk>();
    if (pointerOffset == 0) {
      chunks.Add(new(ChunkKind.Data, (int)size1, dataOffset));
    } else {
      if (pointerLength != 3) throw new NotSupportedException($"ISZ: {pointerLength}-byte chunk pointers are not supported.");
      if (blocks > (stream.Length - pointerOffset) / 3) throw new InvalidDataException("ISZ: chunk table runs past the end of the file.");
      var table = new byte[blocks * 3];
      stream.Position = pointerOffset;
      stream.ReadExactly(table);
      for (var i = 0; i < table.Length; ++i) table[i] ^= PointerKey[i & 3];
      long offset = dataOffset;
      for (var i = 0; i < blocks; ++i) {
        var value = table[3 * i] | table[3 * i + 1] << 8 | table[3 * i + 2] << 16;
        var kind = (ChunkKind)(value >> 22);
        var length = value & 0x3FFFFF;
        if (length > blockSize) throw new InvalidDataException($"ISZ: chunk {i} is longer than the chunk size.");
        chunks.Add(new(kind, length, offset));
        if (kind != ChunkKind.Zero) offset += length;
      }
      if (offset > stream.Length) throw new InvalidDataException("ISZ: chunk data runs past the end of the file.");
    }

    return new IszImage {
      Version = version, VolumeSerial = BinaryPrimitives.ReadUInt32LittleEndian(h[6..]), SectorSize = sectorSize,
      TotalSectors = totalSectors, Encryption = encryption, BlockSize = blockSize, Chunks = chunks,
      DataChecksum = BinaryPrimitives.ReadUInt32LittleEndian(h[48..]),
      PackedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(h[60..]),
    };
  }

  /// <summary>Writes the ISO image, verifying the stored CRC-32 of the decoded data.</summary>
  public void CopyImageTo(Stream source, Stream output) {
    var crc = Crc32.Seed;
    var written = 0L;
    foreach (var c in this.Chunks) {
      byte[] data;
      if (c.Kind == ChunkKind.Zero) data = new byte[c.Length];
      else {
        var raw = new byte[c.Length];
        source.Position = c.Offset;
        source.ReadExactly(raw);
        data = c.Kind switch {
          ChunkKind.Data => raw,
          ChunkKind.Zlib => FileFormat.Zlib.ZlibStream.Decompress(raw),
          ChunkKind.Bzip2 => DecodeBzip2(raw),
          _ => throw new InvalidDataException($"ISZ: unknown chunk kind {c.Kind}."),
        };
      }
      if (data.Length > this.BlockSize) throw new InvalidDataException("ISZ: chunk decodes beyond the chunk size.");
      var take = (int)Math.Min(data.Length, this.ImageSize - written);
      if (take < 0) take = 0;
      crc = Crc32.Update(crc, data.AsSpan(0, take));
      output.Write(data, 0, take);
      written += take;
    }
    if (written != this.ImageSize)
      throw new InvalidDataException($"ISZ: chunks decode to {written} bytes, header promises {this.ImageSize}.");
    if (crc != this.DataChecksum)
      throw new InvalidDataException("ISZ: CRC-32 of the decoded image does not match the header.");
  }

  private static byte[] DecodeBzip2(byte[] raw) {
    // ISZ blanks the "BZh" stream signature; restore it before decoding.
    if (raw.Length < 3) throw new InvalidDataException("ISZ: bzip2 chunk too short.");
    raw[0] = (byte)'B'; raw[1] = (byte)'Z'; raw[2] = (byte)'h';
    return new Bzip2BuildingBlock().Decompress(raw);
  }

  /// <summary>Writes a single-segment ISZ with 3-byte chunk pointers: zero chunks, zlib where smaller, else stored.</summary>
  public static void Write(Stream output, ReadOnlySpan<byte> image, int blockSize = DefaultBlockSize, ushort sectorSize = 2048, uint volumeSerial = 0) {
    if (image.Length % sectorSize != 0)
      throw new ArgumentException($"ISZ images hold whole {sectorSize}-byte sectors; {image.Length} bytes is not a multiple.");
    if (blockSize <= 0 || blockSize % sectorSize != 0 || blockSize > 0x3FFFFF)
      throw new ArgumentOutOfRangeException(nameof(blockSize));
    var blocks = (image.Length + blockSize - 1) / blockSize;
    var pointerOffset = HeaderSize;
    var dataOffset = pointerOffset + blocks * 3;
    var table = new byte[blocks * 3];
    var payload = new MemoryStream();
    uint dataCrc = Crc32.Seed, packedCrc = Crc32.Seed;
    for (var i = 0; i < blocks; ++i) {
      var slice = image.Slice(i * blockSize, Math.Min(blockSize, image.Length - i * blockSize));
      dataCrc = Crc32.Update(dataCrc, slice);
      ChunkKind kind;
      byte[] stored;
      if (!slice.ContainsAnyExcept((byte)0)) {
        kind = ChunkKind.Zero;
        stored = [];
      } else {
        var packed = FileFormat.Zlib.ZlibStream.Compress(slice);
        (kind, stored) = packed.Length < slice.Length ? (ChunkKind.Zlib, packed) : (ChunkKind.Data, slice.ToArray());
      }
      var length = kind == ChunkKind.Zero ? slice.Length : stored.Length;
      var value = (int)kind << 22 | length;
      table[3 * i] = (byte)value;
      table[3 * i + 1] = (byte)(value >> 8);
      table[3 * i + 2] = (byte)(value >> 16);
      packedCrc = Crc32.Update(packedCrc, stored);
      payload.Write(stored);
    }
    for (var i = 0; i < table.Length; ++i) table[i] ^= PointerKey[i & 3];

    Span<byte> h = stackalloc byte[HeaderSize];
    h.Clear();
    Signature.CopyTo(h);
    h[4] = HeaderSize;
    h[5] = 1;
    BinaryPrimitives.WriteUInt32LittleEndian(h[6..], volumeSerial);
    BinaryPrimitives.WriteUInt16LittleEndian(h[10..], sectorSize);
    BinaryPrimitives.WriteUInt32LittleEndian(h[12..], (uint)(image.Length / sectorSize));
    BinaryPrimitives.WriteUInt32LittleEndian(h[25..], (uint)blocks);
    BinaryPrimitives.WriteUInt32LittleEndian(h[29..], (uint)blockSize);
    h[33] = 3;
    BinaryPrimitives.WriteUInt32LittleEndian(h[35..], (uint)pointerOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(h[43..], (uint)dataOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(h[48..], dataCrc);
    BinaryPrimitives.WriteUInt32LittleEndian(h[52..], (uint)image.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(h[60..], packedCrc);
    output.Write(h);
    output.Write(table);
    payload.Position = 0;
    payload.CopyTo(output);
  }

  /// <summary>CRC-32 register (zlib polynomial, seed 0xFFFFFFFF, no final inversion). ISZ stores the register, the complement of zlib's crc32.</summary>
  private static class Crc32 {
    public const uint Seed = 0xFFFFFFFF;
    private static readonly uint[] Table = Build();

    private static uint[] Build() {
      var t = new uint[256];
      for (uint i = 0; i < 256; ++i) {
        var c = i;
        for (var k = 0; k < 8; ++k) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        t[i] = c;
      }
      return t;
    }

    public static uint Update(uint crc, ReadOnlySpan<byte> data) {
      foreach (var b in data) crc = Table[(byte)(crc ^ b)] ^ (crc >> 8);
      return crc;
    }
  }
}
