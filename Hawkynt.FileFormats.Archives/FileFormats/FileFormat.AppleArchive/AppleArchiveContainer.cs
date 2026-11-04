using System.Buffers.Binary;
using Compression.Core.Dictionary.Lz4;
using Compression.Core.Dictionary.Lzbitmap;
using FileFormat.Lzfse;
using FileFormat.Xz;
using FileFormat.Zlib;

namespace FileFormat.AppleArchive;

/// <summary>
/// The "pbz?" stream an Apple Archive is wrapped in when compressed: <c>"pbz" + algorithm</c>, a
/// big-endian u64 block size, then blocks of <c>u64 raw length, u64 stored length, data</c>
/// (big-endian). Algorithms: e LZFSE, z zlib, x xz, 4 LZ4 (Apple "bv41" framing), b LZBITMAP.
/// A block whose stored length equals its raw length is stored uncompressed.
/// </summary>
internal static class AppleArchiveContainer {
  public const int DefaultBlockSize = 4 << 20;

  public static bool IsContainer(ReadOnlySpan<byte> head) =>
    head.Length >= 4 && head[0] == 'p' && head[1] == 'b' && head[2] == 'z';

  public static char Algorithm(ReadOnlySpan<byte> head) => (char)head[3];

  /// <summary>Decodes a pbz stream into the plain AA01 item stream.</summary>
  public static byte[] Decode(ReadOnlySpan<byte> file) {
    if (file.Length < 12 || !IsContainer(file))
      throw new InvalidDataException("Not a pbz-compressed Apple Archive.");
    var algorithm = (char)file[3];
    if ("ezx4b".IndexOf(algorithm) < 0)
      throw new NotSupportedException($"Apple Archive compression 'pbz{algorithm}' is not supported.");

    using var output = new MemoryStream();
    var position = 12;
    while (position < file.Length) {
      if (file.Length - position < 16)
        throw new InvalidDataException("Truncated Apple Archive block header.");
      var raw = BinaryPrimitives.ReadUInt64BigEndian(file[position..]);
      var stored = BinaryPrimitives.ReadUInt64BigEndian(file[(position + 8)..]);
      position += 16;
      if (stored > (ulong)(file.Length - position) || raw > int.MaxValue)
        throw new InvalidDataException("Apple Archive block runs past the end of the file.");
      var data = file.Slice(position, (int)stored);
      position += (int)stored;

      var block = stored == raw ? data.ToArray() : DecodeBlock(algorithm, data, (int)raw);
      if (block.Length != (int)raw)
        throw new InvalidDataException($"Apple Archive block decoded to {block.Length} bytes, not {raw}.");
      output.Write(block);
    }

    return output.ToArray();
  }

  private static byte[] DecodeBlock(char algorithm, ReadOnlySpan<byte> data, int raw) => algorithm switch {
    'e' => Lzfse(data, decode: true),
    'z' => ZlibStream.Decompress(data),
    'b' => Lzbitmap.Decompress(data),
    '4' => DecodeAppleLz4(data, raw),
    _ => DecodeXz(data),
  };

  private static byte[] Lzfse(ReadOnlySpan<byte> data, bool decode) {
    using var input = new MemoryStream(data.ToArray());
    using var output = new MemoryStream();
    if (decode)
      LzfseStream.Decompress(input, output);
    else
      LzfseStream.Compress(input, output);
    return output.ToArray();
  }

  private static byte[] DecodeXz(ReadOnlySpan<byte> data) {
    using var input = new MemoryStream(data.ToArray());
    using var xz = new XzStream(input, Compression.Core.Streams.CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    xz.CopyTo(output);
    return output.ToArray();
  }

  // libcompression's LZ4 framing: "bv41" u32 raw, u32 stored, LZ4 block (matches may reach into
  // earlier blocks of the same stream); "bv4-" u32 raw, raw bytes; "bv4$" ends the stream.
  private static byte[] DecodeAppleLz4(ReadOnlySpan<byte> data, int raw) {
    var window = new byte[raw];
    var produced = 0;
    var position = 0;
    while (true) {
      if (data.Length - position < 4)
        throw new InvalidDataException("Truncated Apple LZ4 stream.");
      var tag = data.Slice(position, 4);
      position += 4;
      if (tag.SequenceEqual("bv4$"u8))
        break;

      if (data.Length - position < 4)
        throw new InvalidDataException("Truncated Apple LZ4 block header.");
      var length = BinaryPrimitives.ReadInt32LittleEndian(data[position..]);
      position += 4;
      if (length < 0 || length > raw - produced)
        throw new InvalidDataException("Apple LZ4 block is longer than the archive block.");

      if (tag.SequenceEqual("bv4-"u8)) {
        if (data.Length - position < length)
          throw new InvalidDataException("Truncated Apple LZ4 raw block.");
        data.Slice(position, length).CopyTo(window.AsSpan(produced));
        position += length;
        produced += length;
        continue;
      }

      if (!tag.SequenceEqual("bv41"u8) || data.Length - position < 4)
        throw new InvalidDataException("Unknown Apple LZ4 block tag.");
      var stored = BinaryPrimitives.ReadInt32LittleEndian(data[position..]);
      position += 4;
      if (stored < 0 || stored > data.Length - position)
        throw new InvalidDataException("Truncated Apple LZ4 compressed block.");
      var written = Lz4BlockDecompressor.DecompressAfterPrefix(data.Slice(position, stored), window.AsSpan(0, produced + length), produced);
      if (written != length)
        throw new InvalidDataException("Apple LZ4 block decoded to the wrong length.");
      position += stored;
      produced += length;
    }

    return produced == raw ? window : window[..produced];
  }

  /// <summary>Wraps a plain item stream: 'e' LZFSE or 'z' zlib, blocks stored when they do not shrink.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> plain, char algorithm, int blockSize = DefaultBlockSize) {
    if (algorithm != 'e' && algorithm != 'z')
      throw new NotSupportedException($"Writing Apple Archive compression 'pbz{algorithm}' is not supported.");
    using var output = new MemoryStream();
    Span<byte> word = stackalloc byte[8];
    output.Write([(byte)'p', (byte)'b', (byte)'z', (byte)algorithm]);
    BinaryPrimitives.WriteUInt64BigEndian(word, (ulong)blockSize);
    output.Write(word);
    for (var start = 0; start < plain.Length; start += blockSize) {
      var block = plain.Slice(start, Math.Min(blockSize, plain.Length - start));
      var packed = algorithm == 'e' ? Lzfse(block, decode: false) : ZlibStream.Compress(block);
      var body = packed.Length < block.Length ? packed : block.ToArray();
      BinaryPrimitives.WriteUInt64BigEndian(word, (ulong)block.Length);
      output.Write(word);
      BinaryPrimitives.WriteUInt64BigEndian(word, (ulong)body.Length);
      output.Write(word);
      output.Write(body);
    }

    return output.ToArray();
  }
}
