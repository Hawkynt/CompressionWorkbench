#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.FirmwareCommon;

/// <summary>
/// Recognises and unpacks a compressed payload embedded in a firmware image by the magic of its
/// stream format, using this repository's own stream codecs through the registry. A payload is
/// only reported as decompressed when the codec decodes it without error, so nothing is ever
/// guessed into existence.
/// </summary>
public static class EmbeddedPayload {

  /// <summary>Stream formats a kernel or firmware payload is commonly compressed with: registry id, file extension, magic.</summary>
  private static readonly (string Id, string Extension, byte[] Magic)[] Known = [
    ("Gzip", "gz", [0x1F, 0x8B, 0x08]),
    ("Xz", "xz", [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]),
    ("Bzip2", "bz2", [0x42, 0x5A, 0x68]),
    ("Zstd", "zst", [0x28, 0xB5, 0x2F, 0xFD]),
    ("Lz4", "lz4", [0x02, 0x21, 0x4C, 0x18]),
    ("Lzop", "lzo", [0x89, 0x4C, 0x5A, 0x4F, 0x00, 0x0D, 0x0A, 0x1A, 0x0A]),
    ("Lzma", "lzma", [0x5D, 0x00, 0x00]),
  ];

  /// <summary>The stream format whose magic starts <paramref name="data"/>, or null.</summary>
  public static (string Id, string Extension)? Identify(ReadOnlySpan<byte> data) {
    foreach (var (id, extension, magic) in Known)
      if (data.StartsWith(magic)) return (id, extension);
    return null;
  }

  /// <summary>
  /// Decodes <paramref name="data"/> with the codec <paramref name="formatId"/> names, giving up
  /// past <paramref name="limit"/> output bytes. Null when the codec is not registered or rejects
  /// the stream.
  /// </summary>
  public static byte[]? TryDecompress(string formatId, ReadOnlyMemory<byte> data, long limit = 1L << 30) {
    if (FormatRegistry.GetStreamOps(formatId) is not { } ops) return null;
    // A stray 5D 00 00 is no LZMA stream: refuse property bytes past lc/lp/pb's range and dictionaries
    // a kernel compressor never uses, before the decoder allocates a window for them.
    if (formatId == "Lzma") {
      var span = data.Span;
      if (span.Length < 13 || span[0] >= 9 * 5 * 5 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span[1..]) > 1u << 27)
        return null;
    }
    try {
      using var input = new MemoryStream(data.ToArray(), writable: false);
      using var output = new CappedStream(limit);
      ops.Decompress(input, output);
      return output.Length == 0 ? null : output.ToArray();
    } catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or InvalidOperationException
                                 or IndexOutOfRangeException or NotSupportedException or OverflowException or EndOfStreamException) {
      return null;
    }
  }

  /// <summary>
  /// Scans <paramref name="data"/> from <paramref name="start"/> for the first known stream magic
  /// whose codec decodes and whose output satisfies <paramref name="accept"/>.
  /// </summary>
  public static (int Offset, string Id, string Extension, byte[] Decoded)? Find(ReadOnlyMemory<byte> data, int start, Func<byte[], bool> accept, int maxCandidates = 64) {
    var span = data.Span;
    var tried = 0;
    for (var i = Math.Max(0, start); i < span.Length - 4 && tried < maxCandidates; ++i) {
      if (Identify(span[i..]) is not { } kind) continue;
      ++tried;
      if (TryDecompress(kind.Id, data[i..]) is { } decoded && accept(decoded)) return (i, kind.Id, kind.Extension, decoded);
    }
    return null;
  }

  /// <summary>A memory stream that refuses to grow past a cap, so a hostile stream cannot exhaust memory.</summary>
  private sealed class CappedStream(long cap) : MemoryStream {
    public override void Write(byte[] buffer, int offset, int count) {
      if (this.Length + count > cap) throw new InvalidDataException($"decoded payload exceeds {cap} bytes");
      base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer) {
      if (this.Length + buffer.Length > cap) throw new InvalidDataException($"decoded payload exceeds {cap} bytes");
      base.Write(buffer);
    }

    public override void WriteByte(byte value) {
      if (this.Length + 1 > cap) throw new InvalidDataException($"decoded payload exceeds {cap} bytes");
      base.WriteByte(value);
    }
  }
}
