#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;

namespace FileFormat.Cso;

/// <summary>
/// Writes a PSP CSO v1 ("CISO") compressed-ISO container from scratch (WORM).
///
/// <para>Layout (24-byte header + (N+1)·uint32 index + N compressed blocks):
/// <list type="bullet">
///   <item>$00..$03: magic "CISO"</item>
///   <item>$04..$07: uint32 LE header_size = 24</item>
///   <item>$08..$0F: uint64 LE uncompressed_size</item>
///   <item>$10..$13: uint32 LE block_size (this writer uses 2048 = ISO 9660 sector)</item>
///   <item>$14: uint8 version = 1</item>
///   <item>$15: uint8 align (left-shift applied to index offsets; this writer uses 0)</item>
///   <item>$16..$17: uint16 reserved = 0</item>
///   <item>$18..(header+4·(N+1)): index table. bit 31 set = stored uncompressed.</item>
///   <item>Each block: raw DEFLATE bytes (no zlib header) of one block_size-aligned slab,
///         OR the slab verbatim when the compressed output is not smaller than the slab.</item>
/// </list>
/// </para>
///
/// <para>ZSO (LZ4) and CSO v2 are out of scope; see <see cref="CsoFormatDescriptor"/>.</para>
/// </summary>
public sealed class CsoWriter {

  /// <summary>Default block size — 2048 = one ISO 9660 cooked sector.</summary>
  public const int DefaultBlockSize = 2048;

  /// <summary>CSO v1 header length in bytes.</summary>
  internal const int HeaderSize = 24;

  /// <summary>Bit 31 of an index entry: when set, the block is stored uncompressed.</summary>
  internal const uint IndexUncompressedFlag = 0x8000_0000u;

  /// <summary>Mask for the offset portion of an index entry.</summary>
  internal const uint IndexOffsetMask = 0x7FFF_FFFFu;

  /// <summary>
  /// Builds a CSO v1 stream that, when fully decompressed, yields
  /// <paramref name="uncompressedData"/>. Blocks of <paramref name="blockSize"/>
  /// bytes are each DEFLATE-compressed; if the compressed output is not smaller
  /// than the original slab, the slab is stored verbatim and its index entry
  /// gets the <see cref="IndexUncompressedFlag"/> bit set.
  /// </summary>
  public static byte[] Build(ReadOnlySpan<byte> uncompressedData, int blockSize = DefaultBlockSize) {
    if (blockSize <= 0)
      throw new ArgumentOutOfRangeException(nameof(blockSize), "block_size must be positive.");

    var uncompressedSize = uncompressedData.Length;
    var blockCount = (uncompressedSize + blockSize - 1) / blockSize;
    var indexCount = blockCount + 1; // trailing sentinel = EOF offset.

    var output = new MemoryStream();
    // Reserve space for header + index; we'll patch the index after writing blocks.
    output.Write(new byte[HeaderSize + indexCount * 4]);

    var index = new uint[indexCount];
    for (var i = 0; i < blockCount; ++i) {
      var rawOffset = i * blockSize;
      var rawLen = Math.Min(blockSize, uncompressedSize - rawOffset);

      // If the last block is partial, pad it to blockSize with zero bytes so the
      // decoder yields blockSize bytes — its caller truncates by uncompressed_size.
      var slab = new byte[blockSize];
      uncompressedData.Slice(rawOffset, rawLen).CopyTo(slab);

      var blockOffset = checked((uint)output.Position);
      if (blockOffset > IndexOffsetMask)
        throw new InvalidOperationException("CSO output exceeds 2 GiB — would overflow a 31-bit offset.");
      index[i] = blockOffset;

      var deflated = Deflate(slab);
      if (deflated.Length < blockSize) {
        output.Write(deflated, 0, deflated.Length);
      } else {
        // Compressed payload is not smaller than the slab — store verbatim and
        // flag the index entry so the reader knows not to inflate.
        output.Write(slab, 0, blockSize);
        index[i] |= IndexUncompressedFlag;
      }
    }

    // Sentinel index entry = end-of-stream offset (used by the reader to
    // compute the last real block's compressed length via nextOffset-offset).
    var eofOffset = checked((uint)output.Position);
    if (eofOffset > IndexOffsetMask)
      throw new InvalidOperationException("CSO output exceeds 2 GiB — would overflow a 31-bit offset.");
    index[blockCount] = eofOffset;

    // Now patch the header + index in place.
    var bytes = output.ToArray();
    WriteHeader(bytes, uncompressedSize, blockSize);
    for (var i = 0; i < indexCount; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(HeaderSize + i * 4, 4), index[i]);

    return bytes;
  }

  /// <summary>
  /// Writes the fixed-layout CSO v1 header into the first 24 bytes of
  /// <paramref name="target"/>.
  /// </summary>
  internal static void WriteHeader(Span<byte> target, long uncompressedSize, int blockSize) {
    target[0] = (byte)'C';
    target[1] = (byte)'I';
    target[2] = (byte)'S';
    target[3] = (byte)'O';
    BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(4, 4), HeaderSize);
    BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(8, 8), (ulong)uncompressedSize);
    BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(16, 4), (uint)blockSize);
    target[20] = 1; // version
    target[21] = 0; // align
    target[22] = 0; // reserved
    target[23] = 0; // reserved
  }

  /// <summary>Raw-DEFLATE encode <paramref name="data"/> (no zlib header).</summary>
  internal static byte[] Deflate(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();
    using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
      ds.Write(data);
    return ms.ToArray();
  }

  /// <summary>Raw-DEFLATE decode <paramref name="data"/> back to its original bytes.</summary>
  internal static byte[] Inflate(ReadOnlySpan<byte> data, int expectedSize) {
    using var input = new MemoryStream(data.ToArray());
    using var ds = new DeflateStream(input, CompressionMode.Decompress);
    var output = new byte[expectedSize];
    var written = 0;
    while (written < expectedSize) {
      var n = ds.Read(output, written, expectedSize - written);
      if (n <= 0) break;
      written += n;
    }
    if (written < expectedSize)
      Array.Resize(ref output, written);
    return output;
  }
}
