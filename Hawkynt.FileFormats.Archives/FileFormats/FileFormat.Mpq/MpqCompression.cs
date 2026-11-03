using Compression.Core.Dictionary.PkwareDcl;

namespace FileFormat.Mpq;

/// <summary>
/// The MPQ compression mask: the first byte of every compressed sector names the methods that
/// were applied, and the reader undoes them in a fixed order.
/// </summary>
/// <remarks>
/// Order, masks and the version split follow StormLib <c>SCompression.cpp</c> (MIT; see
/// <c>THIRD-PARTY-NOTICE.StormLib.txt</c>): archives of format version 1 may combine any of the
/// seven methods and are decoded bzip2 → PKWARE → zlib → Huffman → ADPCM stereo → ADPCM mono →
/// sparse; archives of version 2 and later accept only the fixed set 0x02, 0x08, 0x10, 0x12
/// (LZMA), 0x20, 0x22, 0x30, 0x41 and 0x81.
/// </remarks>
public static class MpqCompression {
  /// <summary>Huffman (WAVE sectors, after ADPCM).</summary>
  public const byte Huffman = 0x01;
  /// <summary>zlib (Warcraft III onwards).</summary>
  public const byte Zlib = 0x02;
  /// <summary>PKWARE Data Compression Library implode (Diablo, StarCraft).</summary>
  public const byte Pkware = 0x08;
  /// <summary>bzip2 (Warcraft III onwards).</summary>
  public const byte Bzip2 = 0x10;
  /// <summary>LZMA (StarCraft II); not a combination of bits, version 2+ archives only.</summary>
  public const byte Lzma = 0x12;
  /// <summary>Sparse zero-run coding (StarCraft II).</summary>
  public const byte Sparse = 0x20;
  /// <summary>IMA ADPCM, one channel (lossy).</summary>
  public const byte AdpcmMono = 0x40;
  /// <summary>IMA ADPCM, two channels (lossy).</summary>
  public const byte AdpcmStereo = 0x80;

  private const int LzmaHeaderSize = 1 + 5 + 8;

  /// <summary>Decodes one compressed sector (mask byte first) to exactly <paramref name="expectedSize"/> bytes.</summary>
  /// <param name="data">The sector as stored, starting with the mask byte.</param>
  /// <param name="expectedSize">The sector's uncompressed size.</param>
  /// <param name="formatVersion">The archive's header format version (0 = v1).</param>
  /// <exception cref="NotSupportedException">The mask names a method or combination MPQ does not define for this version.</exception>
  /// <exception cref="InvalidDataException">The data is damaged or decodes to the wrong size.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> data, int expectedSize, int formatVersion) {
    if (data.Length == 0)
      throw new InvalidDataException("MPQ sector is empty.");
    var mask = data[0];
    var payload = data[1..];
    byte[] result;

    if (formatVersion >= 1) {
      result = mask switch {
        Zlib => DecodeZlib(payload, expectedSize),
        Pkware => PkwareDclDecoder.Decompress(payload, expectedSize),
        Bzip2 => DecodeBzip2(payload),
        Lzma => DecodeLzma(payload, expectedSize),
        Sparse => MpqSparse.Decompress(payload, expectedSize),
        Sparse | Zlib => MpqSparse.Decompress(DecodeZlib(payload, expectedSize), expectedSize),
        Sparse | Bzip2 => MpqSparse.Decompress(DecodeBzip2(payload), expectedSize),
        AdpcmMono | Huffman => MpqAdpcm.Decompress(MpqHuffman.Decompress(payload, expectedSize), expectedSize, 1),
        AdpcmStereo | Huffman => MpqAdpcm.Decompress(MpqHuffman.Decompress(payload, expectedSize), expectedSize, 2),
        _ => throw new NotSupportedException($"MPQ compression mask 0x{mask:X2} is not defined for format version {formatVersion + 1} archives."),
      };
    } else {
      const byte known = Bzip2 | Pkware | Zlib | Huffman | AdpcmStereo | AdpcmMono | Sparse;
      if (mask == 0 || (mask & ~known) != 0)
        throw new NotSupportedException($"MPQ compression mask 0x{mask:X2} names an unknown method.");
      var current = payload.ToArray();
      if ((mask & Bzip2) != 0) current = Stage(DecodeBzip2(current), expectedSize, "bzip2");
      if ((mask & Pkware) != 0) current = Stage(PkwareDclDecoder.Decompress(current), expectedSize, "PKWARE DCL");
      if ((mask & Zlib) != 0) current = Stage(DecodeZlib(current, expectedSize), expectedSize, "zlib");
      if ((mask & Huffman) != 0) current = Stage(MpqHuffman.Decompress(current, expectedSize), expectedSize, "Huffman");
      if ((mask & AdpcmStereo) != 0) current = Stage(MpqAdpcm.Decompress(current, expectedSize, 2), expectedSize, "ADPCM");
      if ((mask & AdpcmMono) != 0) current = Stage(MpqAdpcm.Decompress(current, expectedSize, 1), expectedSize, "ADPCM");
      if ((mask & Sparse) != 0) current = Stage(MpqSparse.Decompress(current, expectedSize), expectedSize, "sparse");
      result = current;
    }

    if (result.Length != expectedSize)
      throw new InvalidDataException($"MPQ sector decoded to {result.Length} bytes; {expectedSize} were expected.");
    return result;
  }

  /// <summary>
  /// Compresses one sector with <paramref name="mask"/>, applying the methods in StormLib's order
  /// (sparse, ADPCM, Huffman, zlib, PKWARE, bzip2). A method that does not save at least two bytes
  /// is skipped; when none helps, the sector is returned unchanged (stored).
  /// </summary>
  /// <param name="data">The sector's bytes.</param>
  /// <param name="mask">One of the masks <see cref="Decompress"/> accepts for the target version.</param>
  /// <returns>The sector as it is stored: mask byte plus payload, or the input when nothing compressed.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data, byte mask) {
    if (data.Length == 0)
      return [];
    if (mask == Lzma) {
      var lzma = EncodeLzma(data);
      return lzma.Length > data.Length - 2 ? data.ToArray() : [Lzma, .. lzma];
    }

    var current = data.ToArray();
    byte applied = 0;
    var huffmanType = 0;
    void Try(byte bit, Func<byte[], byte[]?> encode) {
      if ((mask & bit) == 0)
        return;
      var encoded = encode(current);
      if (encoded == null || encoded.Length > current.Length - 2)
        return;
      current = encoded;
      applied |= bit;
    }

    Try(Sparse, d => MpqSparse.Compress(d));
    Try(AdpcmMono, d => { huffmanType = 7; return MpqAdpcm.Compress(d, 1, 5); });
    Try(AdpcmStereo, d => { huffmanType = 7; return MpqAdpcm.Compress(d, 2, 5); });
    Try(Huffman, d => MpqHuffman.Compress(d, huffmanType));
    Try(Zlib, d => FileFormat.Zlib.ZlibStream.Compress(d));
    Try(Pkware, d => PkwareDclEncoder.Compress(d));
    Try(Bzip2, EncodeBzip2);

    return applied == 0 ? data.ToArray() : [applied, .. current];
  }

  private static byte[] Stage(byte[] decoded, int expectedSize, string method)
    => decoded.Length == 0 || decoded.Length > expectedSize
      ? throw new InvalidDataException($"MPQ {method} stage decoded {decoded.Length} bytes for a {expectedSize}-byte sector.")
      : decoded;

  private static byte[] DecodeZlib(ReadOnlySpan<byte> payload, int expectedSize) {
    try {
      return FileFormat.Zlib.ZlibStream.Decompress(payload);
    } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) {
      throw new InvalidDataException("MPQ zlib sector is damaged.", ex);
    }
  }

  private static byte[] DecodeBzip2(ReadOnlySpan<byte> payload) {
    using var ms = new MemoryStream(payload.ToArray(), writable: false);
    using var bz = new FileFormat.Bzip2.Bzip2Stream(ms, Compression.Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true);
    using var decompressed = new MemoryStream();
    bz.CopyTo(decompressed);
    return decompressed.ToArray();
  }

  private static byte[] EncodeBzip2(byte[] data) {
    using var output = new MemoryStream();
    using (var bz = new FileFormat.Bzip2.Bzip2Stream(output, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true))
      bz.Write(data);
    return output.ToArray();
  }

  /// <summary>StormLib's LZMA framing: a zero filter byte, the 5 property bytes, the 64-bit size, the stream (no end marker).</summary>
  private static byte[] DecodeLzma(ReadOnlySpan<byte> payload, int expectedSize) {
    if (payload.Length <= LzmaHeaderSize)
      throw new InvalidDataException("MPQ LZMA sector is shorter than its header.");
    if (payload[0] != 0)
      throw new NotSupportedException("MPQ LZMA sectors with a filter are not defined.");
    var properties = payload.Slice(1, 5).ToArray();
    using var input = new MemoryStream(payload[LzmaHeaderSize..].ToArray(), writable: false);
    return new Compression.Core.Dictionary.Lzma.LzmaDecoder(input, properties, expectedSize).Decode();
  }

  private static byte[] EncodeLzma(ReadOnlySpan<byte> data) {
    var encoder = new Compression.Core.Dictionary.Lzma.LzmaEncoder(dictionarySize: 1 << 16);
    using var output = new MemoryStream();
    output.WriteByte(0);
    output.Write(encoder.Properties);
    Span<byte> size = stackalloc byte[8];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(size, (ulong)data.Length);
    output.Write(size);
    encoder.Encode(output, data, writeEndMarker: false);
    return output.ToArray();
  }
}
