using System.Buffers.Binary;
using FileFormat.Lzfse;
using FileFormat.Zlib;
using LzbitmapCodec = Compression.Core.Dictionary.Lzbitmap.Lzbitmap;

namespace FileSystem.HfsPlus;

/// <summary>
/// The compression an HFS+ writer applies to file contents (HFS+ transparent compression, the
/// "com.apple.decmpfs" attribute). Each value names a pair of decmpfs methods: the first stores
/// a file of at most 64 KiB whose encoding fits the attribute inline, the second stores the
/// encoding as 64 KiB chunks in the resource fork.
/// </summary>
public enum HfsPlusCompression {
  /// <summary>Files are stored in their data fork, uncompressed.</summary>
  None,
  /// <summary>zlib — decmpfs methods 3 (inline) and 4 (resource fork).</summary>
  Zlib,
  /// <summary>LZVN — decmpfs methods 7 and 8.</summary>
  Lzvn,
  /// <summary>LZFSE — decmpfs methods 11 and 12.</summary>
  Lzfse,
  /// <summary>LZBITMAP — decmpfs methods 13 and 14.</summary>
  Lzbitmap,
  /// <summary>Uncompressed chunks behind decmpfs — methods 9 and 10.</summary>
  Raw,
  /// <summary>Uncompressed bytes in the attribute itself — method 1; larger files stay uncompressed.</summary>
  InlineUncompressed,
}

/// <summary>
/// Reads and writes a file stored with HFS+ transparent compression: the "com.apple.decmpfs"
/// extended attribute and, for the chunked methods, the resource fork it points into.
/// </summary>
/// <remarks>
/// <para>
/// The attribute starts with a 16-byte header — the bytes "fpmc", the method (u32 little-endian),
/// the uncompressed size (u64 little-endian) — at most 3802 bytes in all (MAX_DECMPFS_XATTR_SIZE,
/// xnu bsd/sys/decmpfs.h). Methods and layouts, from the libyal libfshfs documentation and xnu's
/// decmpfs.h/decmpfs.c, confirmed on keramics' hfsplus.raw against libfshfs 20260922:
/// </para>
/// <list type="bullet">
///   <item><description>1: the bytes follow the header (xnu's own Type1 decompressor).</description></item>
///   <item><description>3 zlib, 7 LZVN, 9 raw, 11 LZFSE, 13 LZBITMAP: one encoded chunk follows the
///   header.</description></item>
///   <item><description>4 zlib, 10 raw (libfshfs layout; see below): a classic resource fork for zlib
///   — header (data offset 0x100, map offset, data length, map length; u32 big-endian), the
///   resource's length (u32 big-endian), a block count and (offset, size) pairs (u32
///   little-endian, offsets from just after the length), the blocks, then a 50-byte map naming
///   one 'cmpf' resource with ID 1.</description></item>
///   <item><description>8 LZVN, 10 raw, 12 LZFSE, 14 LZBITMAP: a table of u32 little-endian chunk
///   offsets from the fork's start (the first is the table's own length), chunk i running to
///   offset i+1.</description></item>
///   <item><description>5: libfshfs reads the content as zeros of the recorded size; that is what
///   is returned. It is never written.</description></item>
///   <item><description>0x80000001 / 0x80000002 (xnu DATALESS_CMPFS_TYPE, DATALESS_PKG_CMPFS_TYPE):
///   a placeholder whose content lives with a file provider, not on the volume. It has no local
///   bytes: it lists as empty and extracts as an empty file, never as invented content.</description></item>
/// </list>
/// <para>
/// Every chunk holds 65536 bytes except the last. A chunk is stored verbatim after a marker byte
/// when encoding would not shrink it: 0xFF for zlib, LZFSE and LZBITMAP, 0x06 (LZVN's
/// end-of-stream opcode) for LZVN, 0xCC for raw — the markers libfshfs reads; for LZBITMAP the
/// marker is taken from linux-apfs-rw's reader (any first byte whose low nibble is 0xF), which
/// also reads an LZBITMAP chunk as a stream starting with 'Z'. libfshfs reads method 10 from a
/// chunk table and linux-apfs-rw from a zlib-style resource fork; the reader accepts both and
/// the writer emits the chunk table.
/// </para>
/// </remarks>
internal static class HfsPlusDecmpfs {

  private const int HeaderSize = 16;
  private const int ChunkSize = 65536;

  /// <summary>Largest decmpfs attribute (xnu MAX_DECMPFS_XATTR_SIZE).</summary>
  public const int MaxAttributeSize = 3802;

  private const uint DatalessFile = 0x80000001;
  private const uint DatalessPackage = 0x80000002;

  private static ReadOnlySpan<byte> Magic => "fpmc"u8;

  /// <summary>The method a well-formed attribute names, or null.</summary>
  public static uint? Method(ReadOnlySpan<byte> attribute)
    => attribute.Length >= HeaderSize && attribute[..4].SequenceEqual(Magic) ? BinaryPrimitives.ReadUInt32LittleEndian(attribute[4..]) : null;

  /// <summary>Whether the attribute marks a dataless placeholder, whose content is not on the volume.</summary>
  public static bool IsDataless(ReadOnlySpan<byte> attribute) => Method(attribute) is DatalessFile or DatalessPackage;

  /// <summary>
  /// The size the file has on this volume: the header's uncompressed size, or 0 for a dataless
  /// placeholder (whose header records the size of content held elsewhere).
  /// </summary>
  public static bool TryReadSize(ReadOnlySpan<byte> attribute, out long size) {
    size = 0;
    if (Method(attribute) is not { } method) return false;
    if (method is DatalessFile or DatalessPackage) return true;
    var raw = BinaryPrimitives.ReadUInt64LittleEndian(attribute[8..]);
    if (raw > int.MaxValue) return false;
    size = (long)raw;
    return true;
  }

  // ── Reading ────────────────────────────────────────────────────────

  /// <summary>The file's content.</summary>
  /// <param name="attribute">The decmpfs attribute.</param>
  /// <param name="resourceFork">Reads the file's resource fork, for the chunked methods.</param>
  /// <exception cref="NotSupportedException">The method is one no decmpfs reader defines.</exception>
  /// <exception cref="InvalidDataException">The compressed data does not decode to the recorded size.</exception>
  public static byte[] Decode(byte[] attribute, Func<byte[]> resourceFork) {
    if (Method(attribute) is not { } method || !TryReadSize(attribute, out var size))
      throw new InvalidDataException("HFS+ decmpfs attribute has no valid header.");
    if (method is DatalessFile or DatalessPackage) return [];
    var length = (int)size;
    var payload = attribute.AsSpan(HeaderSize);
    var result = method switch {
      1 => payload.ToArray(),
      5 => new byte[length],
      3 or 7 or 9 or 11 or 13 => DecodeChunk(method, payload, length),
      4 => ZlibResourceFork(resourceFork(), length),
      10 => RawResourceFork(resourceFork(), length),
      8 or 12 or 14 => ChunkTable(resourceFork(), length, method),
      _ => throw new NotSupportedException($"HFS+ decmpfs method {method} is not defined."),
    };
    if (result.Length != length)
      throw new InvalidDataException($"HFS+ decmpfs method {method} decoded {result.Length} bytes, the header records {length}.");
    return result;
  }

  private static byte[] DecodeChunk(uint method, ReadOnlySpan<byte> chunk, int length) {
    switch (method) {
      case 3 or 4:
        if (chunk.Length > 0 && chunk[0] == 0xFF) return chunk[1..].ToArray();
        return ZlibStream.Decompress(chunk);
      case 7 or 8:
        if (chunk.Length > 0 && chunk[0] == 0x06) return chunk[1..].ToArray();
        return LzfseStream.DecodeLzvn(chunk, length);
      case 9 or 10:
        if (chunk.Length == 0 || chunk[0] != 0xCC) throw new InvalidDataException("HFS+ decmpfs raw chunk lacks its 0xCC marker.");
        return chunk[1..].ToArray();
      case 11 or 12: {
        if (chunk.Length > 0 && chunk[0] == 0xFF) return chunk[1..].ToArray();
        using var output = new MemoryStream(length);
        LzfseStream.Decompress(new MemoryStream(chunk.ToArray(), writable: false), output);
        return output.ToArray();
      }
      case 13 or 14:
        if (chunk.Length > 0 && chunk[0] == (byte)'Z') return LzbitmapCodec.Decompress(chunk);
        if (chunk.Length > 0 && (chunk[0] & 0x0F) == 0x0F) return chunk[1..].ToArray();
        throw new InvalidDataException("HFS+ decmpfs LZBITMAP chunk is neither a stream nor stored.");
      default:
        throw new NotSupportedException($"HFS+ decmpfs method {method} has no chunk encoding.");
    }
  }

  private static byte[] ChunkTable(byte[] fork, int length, uint method) {
    var chunks = (length + ChunkSize - 1) / ChunkSize;
    if (fork.Length < 4 * (chunks + 1))
      throw new InvalidDataException("HFS+ decmpfs chunk table is truncated.");
    var result = new byte[length];
    for (var i = 0; i < chunks; ++i) {
      var start = BinaryPrimitives.ReadUInt32LittleEndian(fork.AsSpan(4 * i));
      var end = BinaryPrimitives.ReadUInt32LittleEndian(fork.AsSpan(4 * i + 4));
      if (start > end || end > fork.Length)
        throw new InvalidDataException("HFS+ decmpfs chunk lies outside the resource fork.");
      Place(result, i, DecodeChunk(method, fork.AsSpan((int)start, (int)(end - start)), Math.Min(ChunkSize, length - i * ChunkSize)));
    }
    return result;
  }

  /// <summary>Method 10: a chunk table (libfshfs) or, failing that, a zlib-style resource fork (linux-apfs-rw).</summary>
  private static byte[] RawResourceFork(byte[] fork, int length) {
    var chunks = (length + ChunkSize - 1) / ChunkSize;
    return fork.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(fork) == 4 * (chunks + 1)
      ? ChunkTable(fork, length, 10)
      : ClassicResourceFork(fork, length, 10);
  }

  private static byte[] ZlibResourceFork(byte[] fork, int length) => ClassicResourceFork(fork, length, 4);

  private static byte[] ClassicResourceFork(byte[] fork, int length, uint method) {
    if (fork.Length < 16) throw new InvalidDataException("HFS+ decmpfs resource fork is truncated.");
    var dataOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fork);
    var tableAt = dataOffset + 4;
    if (dataOffset < 16 || tableAt + 4 > fork.Length) throw new InvalidDataException("HFS+ decmpfs resource data lies outside the fork.");
    var count = BinaryPrimitives.ReadUInt32LittleEndian(fork.AsSpan(tableAt));
    var chunks = (length + ChunkSize - 1) / ChunkSize;
    if (count != chunks || tableAt + 4 + 8L * count > fork.Length)
      throw new InvalidDataException($"HFS+ decmpfs resource fork lists {count} blocks for {chunks} chunks.");
    var result = new byte[length];
    for (var i = 0; i < chunks; ++i) {
      var offset = BinaryPrimitives.ReadUInt32LittleEndian(fork.AsSpan(tableAt + 4 + 8 * i));
      var size = BinaryPrimitives.ReadUInt32LittleEndian(fork.AsSpan(tableAt + 8 + 8 * i));
      if ((long)tableAt + offset + size > fork.Length)
        throw new InvalidDataException("HFS+ decmpfs block lies outside the resource fork.");
      Place(result, i, DecodeChunk(method, fork.AsSpan(tableAt + (int)offset, (int)size), Math.Min(ChunkSize, length - i * ChunkSize)));
    }
    return result;
  }

  private static void Place(byte[] result, int chunk, byte[] decoded) {
    var expected = Math.Min(ChunkSize, result.Length - chunk * ChunkSize);
    if (decoded.Length != expected)
      throw new InvalidDataException($"HFS+ decmpfs chunk {chunk} decoded {decoded.Length} bytes, expected {expected}.");
    decoded.CopyTo(result, chunk * ChunkSize);
  }

  // ── Writing ────────────────────────────────────────────────────────

  /// <summary>A file's decmpfs form: the attribute and, for a chunked method, the resource fork.</summary>
  internal sealed record Encoding(byte[] Attribute, byte[]? ResourceFork);

  /// <summary>
  /// The decmpfs form of <paramref name="data"/>, or null when the file is better stored plainly:
  /// an empty file, a method-1 file too large for the attribute, or a compressing method whose
  /// resource fork would not be smaller than the file. An inline attribute is always taken,
  /// because it costs no allocation block (macOS compresses a 19-byte file inline as a stored
  /// chunk, 36 bytes of attribute — keramics' compressed1).
  /// </summary>
  public static Encoding? Encode(ReadOnlySpan<byte> data, HfsPlusCompression compression) {
    if (data.IsEmpty || compression == HfsPlusCompression.None) return null;
    if (compression == HfsPlusCompression.InlineUncompressed)
      return HeaderSize + data.Length <= MaxAttributeSize ? new Encoding(Attribute(1, data.Length, data), null) : null;

    var (inline, chunked) = compression switch {
      HfsPlusCompression.Zlib => (3u, 4u),
      HfsPlusCompression.Lzvn => (7u, 8u),
      HfsPlusCompression.Lzfse => (11u, 12u),
      HfsPlusCompression.Lzbitmap => (13u, 14u),
      HfsPlusCompression.Raw => (9u, 10u),
      _ => throw new ArgumentOutOfRangeException(nameof(compression)),
    };

    if (data.Length <= ChunkSize) {
      var payload = EncodeChunk(inline, data);
      if (HeaderSize + payload.Length <= MaxAttributeSize)
        return new Encoding(Attribute(inline, data.Length, payload), null);
    }

    var chunks = new List<byte[]>();
    for (var at = 0; at < data.Length; at += ChunkSize)
      chunks.Add(EncodeChunk(chunked, data.Slice(at, Math.Min(ChunkSize, data.Length - at))));
    var fork = chunked == 4 ? ZlibFork(chunks) : OffsetTableFork(chunks);
    if (compression != HfsPlusCompression.Raw && fork.Length >= data.Length) return null;
    return new Encoding(Attribute(chunked, data.Length, []), fork);
  }

  private static byte[] Attribute(uint method, long size, ReadOnlySpan<byte> payload) {
    var attribute = new byte[HeaderSize + payload.Length];
    Magic.CopyTo(attribute);
    BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(4), method);
    BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(8), (ulong)size);
    payload.CopyTo(attribute.AsSpan(HeaderSize));
    return attribute;
  }

  /// <summary>One chunk, encoded, or stored behind the method's marker when encoding does not shrink it.</summary>
  private static byte[] EncodeChunk(uint method, ReadOnlySpan<byte> chunk) {
    var (encoded, marker) = method switch {
      3 or 4 => (Zlib(chunk), (byte)0xFF),
      7 or 8 => (LzfseStream.EncodeLzvn(chunk), (byte)0x06),
      11 or 12 => (Lzfse(chunk), (byte)0xFF),
      13 or 14 => (LzbitmapCodec.Compress(chunk), (byte)0xFF),
      9 or 10 => ((byte[]?)null, (byte)0xCC),
      _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };
    if (encoded is { } e && e.Length < chunk.Length) return e;
    return [marker, .. chunk];
  }

  private static byte[] Zlib(ReadOnlySpan<byte> chunk) => ZlibStream.Compress(chunk);

  private static byte[] Lzfse(ReadOnlySpan<byte> chunk) {
    using var output = new MemoryStream();
    LzfseStream.Compress(new MemoryStream(chunk.ToArray(), writable: false), output);
    return output.ToArray();
  }

  private static byte[] OffsetTableFork(List<byte[]> chunks) {
    var table = 4 * (chunks.Count + 1);
    var fork = new byte[table + chunks.Sum(static c => c.Length)];
    var offset = table;
    for (var i = 0; i < chunks.Count; ++i) {
      BinaryPrimitives.WriteInt32LittleEndian(fork.AsSpan(4 * i), offset);
      chunks[i].CopyTo(fork, offset);
      offset += chunks[i].Length;
    }
    BinaryPrimitives.WriteInt32LittleEndian(fork.AsSpan(4 * chunks.Count), offset);
    return fork;
  }

  /// <summary>
  /// The resource map macOS writes after the 'cmpf' resource, copied from keramics'
  /// compressed2: reserved header copy, next-map handle, file reference and attributes (zero),
  /// type list at 0x1C, name list at 0x32, one type 'cmpf' with one reference at 0x0A — ID 1,
  /// no name, data at offset 0.
  /// </summary>
  private static ReadOnlySpan<byte> ResourceMap => [
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0,
    0x00, 0x1C, 0x00, 0x32,
    0x00, 0x00, (byte)'c', (byte)'m', (byte)'p', (byte)'f', 0x00, 0x00, 0x00, 0x0A,
    0x00, 0x01, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
  ];

  private const int ResourceDataOffset = 0x100;

  private static byte[] ZlibFork(List<byte[]> chunks) {
    var table = 4 + 8 * chunks.Count;
    var resourceLength = table + chunks.Sum(static c => c.Length);
    var dataLength = 4 + resourceLength;
    var mapOffset = ResourceDataOffset + dataLength;
    var fork = new byte[mapOffset + ResourceMap.Length];
    BinaryPrimitives.WriteInt32BigEndian(fork, ResourceDataOffset);
    BinaryPrimitives.WriteInt32BigEndian(fork.AsSpan(4), mapOffset);
    BinaryPrimitives.WriteInt32BigEndian(fork.AsSpan(8), dataLength);
    BinaryPrimitives.WriteInt32BigEndian(fork.AsSpan(12), ResourceMap.Length);
    BinaryPrimitives.WriteInt32BigEndian(fork.AsSpan(ResourceDataOffset), resourceLength);
    var tableAt = ResourceDataOffset + 4;
    BinaryPrimitives.WriteInt32LittleEndian(fork.AsSpan(tableAt), chunks.Count);
    var offset = table;
    for (var i = 0; i < chunks.Count; ++i) {
      BinaryPrimitives.WriteInt32LittleEndian(fork.AsSpan(tableAt + 4 + 8 * i), offset);
      BinaryPrimitives.WriteInt32LittleEndian(fork.AsSpan(tableAt + 8 + 8 * i), chunks[i].Length);
      chunks[i].CopyTo(fork, tableAt + offset);
      offset += chunks[i].Length;
    }
    ResourceMap.CopyTo(fork.AsSpan(mapOffset));
    return fork;
  }
}
