using System.Buffers.Binary;
using System.IO.Compression;
using FileFormat.Lzfse;

namespace FileSystem.HfsPlus;

/// <summary>
/// Decodes a file stored with HFS+ transparent compression: the "com.apple.decmpfs" extended
/// attribute and, for the chunked methods, the resource fork it points into.
/// </summary>
/// <remarks>
/// <para>
/// Layout, from the libyal libfshfs format documentation ("Compressed data extended attribute",
/// "File content"), confirmed byte for byte on the six afsctool-compressed files of keramics'
/// <c>test_data/hfs/hfsplus.raw</c> against libfshfs 20260922: a 16-byte header — the bytes
/// "fpmc", the method (u32 little-endian), the uncompressed size (u64 little-endian) — then,
/// for an inline method, the compressed bytes.
/// </para>
/// <list type="bullet">
///   <item><description>3 zlib, 7 LZVN, 11 LZFSE: one stream after the header. A first byte of
///   0xFF (zlib, LZFSE) or 0x06 (LZVN, its end-of-stream opcode) marks the rest as stored.</description></item>
///   <item><description>4 zlib in the resource fork: a classic resource fork (header u32 big-endian
///   data offset); at that offset the resource's length (u32 big-endian), a block count (u32
///   little-endian) and that many (offset, size) pairs (u32 little-endian), offsets counted from
///   just after the length.</description></item>
///   <item><description>8 LZVN, 12 LZFSE in the resource fork: a table of u32 little-endian chunk
///   offsets from the fork's start, the first of which is the table's own length; chunk i runs
///   to offset i+1.</description></item>
/// </list>
/// <para>
/// Every chunk holds 65536 uncompressed bytes except the last. The other methods (raw 9/10,
/// sparse 5, LZBITMAP 13/14) have no reference sample here and are refused rather than guessed.
/// </para>
/// </remarks>
internal static class HfsPlusDecmpfs {

  private const int HeaderSize = 16;
  private const int ChunkSize = 65536;

  /// <summary>The uncompressed size the header records, when the attribute has a header.</summary>
  public static bool TryReadSize(ReadOnlySpan<byte> attribute, out long size) {
    size = 0;
    if (attribute.Length < HeaderSize || !attribute[..4].SequenceEqual("fpmc"u8)) return false;
    var raw = BinaryPrimitives.ReadUInt64LittleEndian(attribute[8..]);
    if (raw > int.MaxValue) return false;
    size = (long)raw;
    return true;
  }

  /// <summary>The file's content.</summary>
  /// <param name="attribute">The decmpfs attribute.</param>
  /// <param name="resourceFork">Reads the file's resource fork, for the chunked methods.</param>
  /// <exception cref="NotSupportedException">The method is one this decoder does not implement.</exception>
  /// <exception cref="InvalidDataException">The compressed data does not decode to the recorded size.</exception>
  public static byte[] Decode(byte[] attribute, Func<byte[]> resourceFork) {
    if (!TryReadSize(attribute, out var size))
      throw new InvalidDataException("HFS+ decmpfs attribute has no valid header.");
    var method = BinaryPrimitives.ReadUInt32LittleEndian(attribute.AsSpan(4));
    var length = (int)size;
    var payload = attribute.AsSpan(HeaderSize);
    var result = method switch {
      3 => Zlib(payload, length),
      7 => LzvnChunk(payload, length),
      11 => LzfseChunk(payload, length),
      4 => ZlibResourceFork(resourceFork(), length),
      8 => ChunkTable(resourceFork(), length, LzvnChunk),
      12 => ChunkTable(resourceFork(), length, LzfseChunk),
      _ => throw new NotSupportedException($"HFS+ decmpfs method {method} is not supported."),
    };
    if (result.Length != length)
      throw new InvalidDataException($"HFS+ decmpfs method {method} decoded {result.Length} bytes, the header records {length}.");
    return result;
  }

  private delegate byte[] ChunkDecoder(ReadOnlySpan<byte> chunk, int length);

  private static byte[] Zlib(ReadOnlySpan<byte> chunk, int length) {
    if (chunk.Length > 0 && chunk[0] == 0xFF) return chunk[1..].ToArray();
    using var input = new ZLibStream(new MemoryStream(chunk.ToArray(), writable: false), CompressionMode.Decompress);
    var output = new byte[length];
    var read = input.ReadAtLeast(output, length, throwOnEndOfStream: false);
    return read == length ? output : output[..read];
  }

  private static byte[] LzfseChunk(ReadOnlySpan<byte> chunk, int length) {
    if (chunk.Length > 0 && chunk[0] == 0xFF) return chunk[1..].ToArray();
    using var output = new MemoryStream(length);
    LzfseStream.Decompress(new MemoryStream(chunk.ToArray(), writable: false), output);
    return output.ToArray();
  }

  /// <summary>
  /// A bare LZVN stream, decoded by framing it as the LZFSE container's LZVN block ("bvxn",
  /// raw length u32, payload length u32, payload, then "bvx$") — the same decoder, reached
  /// through its public stream interface.
  /// </summary>
  private static byte[] LzvnChunk(ReadOnlySpan<byte> chunk, int length) {
    if (chunk.Length > 0 && chunk[0] == 0x06) return chunk[1..].ToArray();
    var framed = new byte[12 + chunk.Length + 4];
    "bvxn"u8.CopyTo(framed);
    BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(4), length);
    BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(8), chunk.Length);
    chunk.CopyTo(framed.AsSpan(12));
    "bvx$"u8.CopyTo(framed.AsSpan(12 + chunk.Length));
    using var output = new MemoryStream(length);
    LzfseStream.Decompress(new MemoryStream(framed, writable: false), output);
    return output.ToArray();
  }

  private static byte[] ChunkTable(byte[] fork, int length, ChunkDecoder decode) {
    var chunks = (length + ChunkSize - 1) / ChunkSize;
    if (fork.Length < 4 * (chunks + 1))
      throw new InvalidDataException("HFS+ decmpfs chunk table is truncated.");
    var result = new byte[length];
    for (var i = 0; i < chunks; ++i) {
      var start = BinaryPrimitives.ReadUInt32LittleEndian(fork.AsSpan(4 * i));
      var end = BinaryPrimitives.ReadUInt32LittleEndian(fork.AsSpan(4 * i + 4));
      if (start > end || end > fork.Length)
        throw new InvalidDataException("HFS+ decmpfs chunk lies outside the resource fork.");
      Place(result, i, decode(fork.AsSpan((int)start, (int)(end - start)), Math.Min(ChunkSize, length - i * ChunkSize)));
    }
    return result;
  }

  private static byte[] ZlibResourceFork(byte[] fork, int length) {
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
      Place(result, i, Zlib(fork.AsSpan(tableAt + (int)offset, (int)size), Math.Min(ChunkSize, length - i * ChunkSize)));
    }
    return result;
  }

  private static void Place(byte[] result, int chunk, byte[] decoded) {
    var expected = Math.Min(ChunkSize, result.Length - chunk * ChunkSize);
    if (decoded.Length != expected)
      throw new InvalidDataException($"HFS+ decmpfs chunk {chunk} decoded {decoded.Length} bytes, expected {expected}.");
    decoded.CopyTo(result, chunk * ChunkSize);
  }
}
