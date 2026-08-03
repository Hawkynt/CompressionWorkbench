using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Lzfse;

/// <summary>
/// Reads and writes the length-prefixed sub-streams (FSE-coded symbol blocks and
/// their overflow tables) that make up an LZFSE-inspired container.
/// </summary>
internal static class LzfseValueStream {
  /// <summary>Bucket-encodes, FSE-compresses, and writes a stream of values with its overflow table.</summary>
  public static void WriteValues(List<byte> output, IReadOnlyList<int> values) {
    var overflow = new List<int>();
    var symbols = ValueBucket.Encode(values, overflow);
    WriteBlock(output, FseByteCodec.Encode(symbols));

    WriteInt(output, overflow.Count);
    foreach (var v in overflow)
      WriteInt(output, v);
  }

  /// <summary>Reads a stream of values written by <see cref="WriteValues"/>.</summary>
  public static int[] ReadValues(ReadOnlySpan<byte> data, ref int pos, int count) {
    var encoded = ReadBlock(data, ref pos);
    var symbols = FseByteCodec.Decode(encoded, count);

    var overflowCount = ReadInt(data, ref pos);
    var overflow = new int[overflowCount];
    for (var i = 0; i < overflowCount; ++i)
      overflow[i] = ReadInt(data, ref pos);

    return ValueBucket.Decode(symbols, overflow);
  }

  /// <summary>Writes a length-prefixed opaque byte block.</summary>
  public static void WriteBlock(List<byte> output, byte[] data) {
    WriteInt(output, data.Length);
    output.AddRange(data);
  }

  /// <summary>Reads a length-prefixed opaque byte block.</summary>
  public static byte[] ReadBlock(ReadOnlySpan<byte> data, ref int pos) {
    var length = ReadInt(data, ref pos);
    if (length < 0 || pos + length > data.Length)
      throw new InvalidDataException("LZFSE stream block is truncated.");
    var slice = data.Slice(pos, length).ToArray();
    pos += length;
    return slice;
  }

  /// <summary>Writes a 4-byte little-endian signed integer.</summary>
  public static void WriteInt(List<byte> output, int value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
    output.Add(bytes[0]);
    output.Add(bytes[1]);
    output.Add(bytes[2]);
    output.Add(bytes[3]);
  }

  /// <summary>Reads a 4-byte little-endian signed integer.</summary>
  public static int ReadInt(ReadOnlySpan<byte> data, ref int pos) {
    if (pos + 4 > data.Length)
      throw new InvalidDataException("LZFSE stream is truncated at an integer field.");
    var value = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
    pos += 4;
    return value;
  }
}
