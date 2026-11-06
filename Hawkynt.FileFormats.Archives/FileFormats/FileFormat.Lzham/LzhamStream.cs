#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Lzham;

/// <summary>
/// The LZHAM file layout of LZHAM's own command-line tool (<c>lzhamtest c</c>): the id
/// <c>LZH0</c>, one byte with the log2 of the dictionary size, the decompressed size as 8 bytes
/// little-endian, then one raw LZHAM stream.
/// </summary>
/// <remarks>
/// The layout does not record LZHAM's Huffman table update rate, which the decoder must know; it is
/// LZHAM's default (8) unless the caller says otherwise, as with <c>lzhamtest -h#</c>.
/// </remarks>
public static class LzhamStream {
  private static readonly byte[] Magic = "LZH0"u8.ToArray();
  internal const int HeaderSize = 13;

  /// <summary>Compresses with the smallest dictionary that covers the input and the default table update rate.</summary>
  public static void Compress(Stream input, Stream output) => Compress(input, output, dictSizeLog2: 0);

  /// <summary>
  /// Compresses with the given dictionary size (log2, 15..29; 0 = smallest that covers the input),
  /// table update rate, level and parser. The bytes are those <c>lzhamtest -t0</c> writes with the
  /// same <c>-d</c>, <c>-h</c>, <c>-m</c> and <c>-x</c>.
  /// </summary>
  public static void Compress(Stream input, Stream output, int dictSizeLog2, int tableUpdateRate = LzhamTables.DefaultTableUpdateRate,
    LzhamCompressionLevel level = LzhamCompressionLevel.Uber, bool extremeParsing = false) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    using var buffer = new MemoryStream();
    input.CopyTo(buffer);
    output.Write(CompressFile(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), dictSizeLog2, tableUpdateRate, level, extremeParsing));
  }

  /// <summary>Decompresses, assuming LZHAM's default table update rate.</summary>
  public static void Decompress(Stream input, Stream output) => Decompress(input, output, LzhamTables.DefaultTableUpdateRate);

  /// <summary>Decompresses with the table update rate the file was written with (<c>lzhamtest -h#</c>).</summary>
  public static void Decompress(Stream input, Stream output, int tableUpdateRate) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    using var buffer = new MemoryStream();
    input.CopyTo(buffer);
    output.Write(DecompressFile(buffer.ToArray(), tableUpdateRate));
  }

  /// <summary>The smallest dictionary (log2, at least 15) that covers <paramref name="length"/> bytes.</summary>
  internal static int FittingDictSizeLog2(int length) {
    var log2 = LzhamTables.MinDictSizeLog2;
    while (log2 < LzhamTables.MaxDictSizeLog2 && 1L << log2 < length)
      ++log2;
    return log2;
  }

  internal static byte[] CompressFile(ReadOnlySpan<byte> data, int dictSizeLog2, int tableUpdateRate,
    LzhamCompressionLevel level = LzhamCompressionLevel.Uber, bool extremeParsing = false) {
    if (dictSizeLog2 == 0)
      dictSizeLog2 = FittingDictSizeLog2(data.Length);
    var stream = LzhamEncoder.Encode(data, dictSizeLog2, tableUpdateRate, level, extremeParsing);
    var file = new byte[HeaderSize + stream.Length];
    Magic.CopyTo(file, 0);
    file[4] = (byte)dictSizeLog2;
    BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(5), (ulong)data.Length);
    stream.CopyTo(file, HeaderSize);
    return file;
  }

  internal static byte[] DecompressFile(byte[] file, int tableUpdateRate) {
    if (file.Length < HeaderSize || !file.AsSpan(0, 4).SequenceEqual(Magic))
      throw new InvalidDataException("Not an LZHAM file (no LZH0 header).");
    var dictSizeLog2 = file[4];
    var size = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(5));
    if (size > int.MaxValue - 64)
      throw new NotSupportedException($"LZHAM: a {size}-byte payload is larger than this decoder handles in memory.");
    return LzhamDecoder.Decode(file[HeaderSize..], (int)size, dictSizeLog2, tableUpdateRate);
  }
}
