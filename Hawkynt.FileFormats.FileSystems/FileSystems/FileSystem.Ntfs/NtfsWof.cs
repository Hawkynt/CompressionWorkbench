using System.Buffers.Binary;
using Compression.Core.Dictionary.Lzx;
using Compression.Core.Dictionary.Xpress;

namespace FileSystem.Ntfs;

/// <summary>
/// Windows Overlay Filter "system compression" (<c>compact.exe /exe:…</c>): the file
/// carries an <c>IO_REPARSE_TAG_WOF</c> reparse point, its unnamed <c>$DATA</c> is
/// empty or sparse with the uncompressed length, and the bytes live compressed in
/// the named stream <c>WofCompressedData</c>.
/// </summary>
/// <remarks>
/// <para>Reparse buffer (Windows SDK <c>wofapi.h</c>): <c>WOF_EXTERNAL_INFO</c>
/// {Version = 1, Provider} followed, for <c>WOF_PROVIDER_FILE</c> (2), by
/// <c>FILE_PROVIDER_EXTERNAL_INFO_V1</c> {Version = 1, Algorithm}: 0 XPRESS4K,
/// 1 LZX, 2 XPRESS8K, 3 XPRESS16K. <c>WOF_PROVIDER_WIM</c> (1) points into a WIM
/// file outside the volume and is refused.</para>
/// <para>Stream layout: the file is cut into chunks of 4/8/16 KiB (XPRESS) or
/// 32 KiB (LZX), each compressed on its own. A table of chunk-start offsets for
/// chunks 1..n-1 comes first — 4 bytes each, 8 when the uncompressed length
/// exceeds 4 GiB — offsets relative to the end of the table; chunk 0 starts there.
/// A chunk whose stored length equals its uncompressed length is stored verbatim.
/// XPRESS chunks are MS-XCA XPRESS Huffman; LZX chunks are the WIM flavour of LZX
/// (32 KiB window, E8 translation with the WIM magic file size).</para>
/// </remarks>
internal static class NtfsWof {
  public const uint ReparseTag = 0x80000017;
  public const string StreamName = "WofCompressedData";
  private const uint ProviderWim = 1;
  private const uint ProviderFile = 2;

  /// <summary>Parses the WOF reparse data (after the 8-byte reparse header).</summary>
  /// <returns>The algorithm, or a refusal reason.</returns>
  public static (uint? Algorithm, string? Refusal) ParseReparseData(ReadOnlySpan<byte> data) {
    if (data.Length < 8) return (null, "WOF reparse data is truncated.");
    var provider = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
    if (provider == ProviderWim)
      return (null, "WOF file is backed by an external WIM image, which is not on this volume.");
    if (provider != ProviderFile)
      return (null, $"WOF provider {provider} is not supported.");
    if (data.Length < 16) return (null, "WOF file-provider data is truncated.");
    var algorithm = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
    return algorithm <= 3 ? (algorithm, null) : (null, $"WOF compression algorithm {algorithm} is not supported.");
  }

  public static int ChunkSize(uint algorithm) => algorithm switch {
    0 => 4096,
    1 => 32768,
    2 => 8192,
    3 => 16384,
    _ => throw new NotSupportedException($"WOF compression algorithm {algorithm} is not supported."),
  };

  /// <summary>Decodes a <c>WofCompressedData</c> stream to exactly <paramref name="size" /> bytes.</summary>
  public static byte[] Decode(ReadOnlySpan<byte> stream, long size, uint algorithm) {
    if (size < 0 || size > Array.MaxLength)
      throw new NotSupportedException($"WOF: a {size}-byte file is beyond the buffered extraction limit.");
    if (size == 0) return [];
    var chunk = ChunkSize(algorithm);
    var chunks = checked((int)((size + chunk - 1) / chunk));
    var entryWidth = size > uint.MaxValue ? 8 : 4;
    var tableBytes = checked((long)(chunks - 1) * entryWidth);
    if (tableBytes > stream.Length)
      throw new InvalidDataException("WOF: chunk table is longer than the compressed stream.");
    var data = stream[(int)tableBytes..];

    var output = new byte[size];
    for (var i = 0; i < chunks; i++) {
      var start = i == 0 ? 0 : ReadOffset(stream, i - 1, entryWidth);
      var end = i == chunks - 1 ? data.Length : ReadOffset(stream, i, entryWidth);
      if (start < 0 || end < start || end > data.Length)
        throw new InvalidDataException($"WOF: chunk {i} spans [{start}, {end}) outside the stream.");
      var expected = (int)Math.Min(chunk, size - (long)i * chunk);
      var stored = data[(int)start..(int)end];
      var target = output.AsSpan(i * chunk, expected);
      if (stored.Length == expected) {
        stored.CopyTo(target);
        continue;
      }
      var decoded = algorithm == 1 ? DecodeLzx(stored, expected) : XpressHuffmanDecompressor.Decompress(stored, expected);
      if (decoded.Length < expected)
        throw new InvalidDataException($"WOF: chunk {i} decoded to {decoded.Length} bytes, expected {expected}.");
      decoded.AsSpan(0, expected).CopyTo(target);
    }
    return output;
  }

  private static long ReadOffset(ReadOnlySpan<byte> table, int index, int width)
    => width == 8
      ? (long)BinaryPrimitives.ReadUInt64LittleEndian(table[(index * 8)..])
      : BinaryPrimitives.ReadUInt32LittleEndian(table[(index * 4)..]);

  private static byte[] DecodeLzx(ReadOnlySpan<byte> stored, int expected) {
    using var ms = new MemoryStream(stored.ToArray(), writable: false);
    var chunk = new LzxDecompressor(ms, 15).Decompress(expected);
    LzxWimE8Filter.Postprocess(chunk);
    return chunk;
  }
}
