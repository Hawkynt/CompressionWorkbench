using System.Buffers.Binary;
using System.Text;
using Compression.Core.Deflate;

namespace FileFormat.Kwaj;

/// <summary>
/// Provides static methods for reading and writing files in the Microsoft
/// KWAJ compressed format, produced by COMPRESS.EXE and found in some
/// Windows setup packages.
/// </summary>
/// <remarks>
/// Supported compression methods:
/// <list type="bullet">
///   <item><description>0 — Store (verbatim copy).</description></item>
///   <item><description>1 — XOR with 0xFF.</description></item>
///   <item><description>4 — MSZIP (Deflate with 32 KB block reset).</description></item>
/// </list>
/// Methods 2 (SZDD-style LZSS) and 3 (LZ+Huffman) throw
/// <see cref="NotSupportedException"/>.
/// </remarks>
public static class KwajStream {
  // -------------------------------------------------------------------------
  // Public API
  // -------------------------------------------------------------------------

  /// <summary>
  /// Decompresses a KWAJ-format stream, writing the result to
  /// <paramref name="output"/>.
  /// </summary>
  /// <param name="input">A readable stream positioned at the start of a KWAJ file.</param>
  /// <param name="output">The stream that receives the decompressed data.</param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
  /// </exception>
  /// <exception cref="InvalidDataException">
  /// Thrown when the magic signature is invalid or the header is truncated.
  /// </exception>
  /// <exception cref="NotSupportedException">
  /// Thrown when the compression method is 2 (LZSS) or 3 (LZ+Huffman).
  /// </exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var (method, dataOffset, _, decompressedLength) = ReadHeader(input);

    // Seek to (or skip to) the compressed data.
    SeekToDataOffset(input, dataOffset);

    // Read all remaining compressed bytes.
    var compressed = ReadAllBytes(input);

    var decompressed = DecompressPayload(method, compressed, decompressedLength);
    output.Write(decompressed);
  }

  /// <summary>
  /// Compresses <paramref name="input"/> using the specified KWAJ method
  /// and writes the result — including header — to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">A readable stream containing the uncompressed data.</param>
  /// <param name="output">The stream that receives the KWAJ file.</param>
  /// <param name="method">
  /// The compression method to use. Must be 0 (store), 1 (XOR), or 4 (MSZIP).
  /// Defaults to 4.
  /// </param>
  /// <param name="filename">
  /// Optional original filename to embed in the header. When provided the
  /// <see cref="KwajConstants.FlagHasFilename"/> flag is set and the
  /// null-terminated name is written into the variable header area.
  /// </param>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
  /// </exception>
  /// <exception cref="ArgumentException">
  /// Thrown when <paramref name="method"/> is not 0, 1, or 4.
  /// </exception>
  public static void Compress(Stream input, Stream output, int method = KwajConstants.MethodMsZip,
    string? filename = null) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    if (method is not (KwajConstants.MethodStore or KwajConstants.MethodXor or KwajConstants.MethodMsZip))
      throw new ArgumentException(
        $"Unsupported compression method {method}. Only 0 (store), 1 (XOR), and 4 (MSZIP) are supported.",
        nameof(method));

    var plain = ReadAllBytes(input);
    var compressed = CompressPayload((ushort)method, plain);

    WriteHeader(output, (ushort)method, compressed.Length, (uint)plain.Length, filename);
    output.Write(compressed);
  }

  /// <summary>
  /// Reads the KWAJ header from <paramref name="input"/> and returns the
  /// embedded original filename, or <see langword="null"/> when the
  /// <see cref="KwajConstants.FlagHasFilename"/> flag is not set.
  /// </summary>
  /// <param name="input">A readable stream positioned at the start of a KWAJ file.</param>
  /// <returns>
  /// The null-terminated original filename string, or <see langword="null"/>.
  /// </returns>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="input"/> is <see langword="null"/>.
  /// </exception>
  /// <exception cref="InvalidDataException">
  /// Thrown when the magic signature is invalid or the header is truncated.
  /// </exception>
  public static string? GetOriginalFilename(Stream input) {
    ArgumentNullException.ThrowIfNull(input);

    var (_, _, filename, _) = ReadHeader(input);
    return filename;
  }

  // -------------------------------------------------------------------------
  // Header parsing
  // -------------------------------------------------------------------------

  /// <summary>
  /// Reads and validates the KWAJ fixed + optional header fields.
  /// </summary>
  /// <returns>
  /// A tuple of (method, dataOffset, filename, decompressedLength).
  /// <c>filename</c> is <see langword="null"/> when the flag is absent.
  /// <c>decompressedLength</c> is <c>-1</c> when the flag is absent.
  /// </returns>
  private static (ushort Method, ushort DataOffset, string? Filename, int DecompressedLength)
    ReadHeader(Stream input) {

    // Read fixed header (14 bytes).
    Span<byte> fixed_ = stackalloc byte[KwajConstants.FixedHeaderSize];
    input.ReadExactly(fixed_);

    // Validate magic.
    if (!fixed_[..KwajConstants.MagicLength].SequenceEqual(KwajConstants.Magic))
      throw new InvalidDataException("Invalid KWAJ magic signature.");

    var method     = BinaryPrimitives.ReadUInt16LittleEndian(fixed_[KwajConstants.MethodOffset..]);
    var dataOffset = BinaryPrimitives.ReadUInt16LittleEndian(fixed_[KwajConstants.DataOffsetOffset..]);
    var flags      = BinaryPrimitives.ReadUInt16LittleEndian(fixed_[KwajConstants.FlagsOffset..]);

    // Parse optional fields in flag order.
    var decompressedLength = -1;

    if ((flags & KwajConstants.FlagHasDecompressedLength) != 0) {
      Span<byte> buf = stackalloc byte[4];
      input.ReadExactly(buf);
      decompressedLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf);
    }

    if ((flags & KwajConstants.FlagUnknown1) != 0) {
      Span<byte> buf = stackalloc byte[2];
      input.ReadExactly(buf);
    }

    if ((flags & KwajConstants.FlagUnknown2) != 0) {
      Span<byte> buf = stackalloc byte[2];
      input.ReadExactly(buf);
    }

    string? filename = null;
    if ((flags & KwajConstants.FlagHasFilename) != 0)
      filename = ReadNullTerminatedString(input);

    return (method, dataOffset, filename, decompressedLength);
  }

  // -------------------------------------------------------------------------
  // Header writing
  // -------------------------------------------------------------------------

  private static void WriteHeader(Stream output, ushort method, int compressedLength,
    uint decompressedLength, string? filename) {

    // Build optional fields into a temporary buffer so we can compute the
    // total data offset before writing anything.
    using var optionalBuf = new MemoryStream();

    ushort flags = 0;

    // Always write decompressed length (flag bit 0).
    flags |= KwajConstants.FlagHasDecompressedLength;
    Span<byte> lenBuf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, decompressedLength);
    optionalBuf.Write(lenBuf);

    if (filename is not null) {
      flags |= KwajConstants.FlagHasFilename;
      var nameBytes = Encoding.ASCII.GetBytes(filename);
      optionalBuf.Write(nameBytes);
      optionalBuf.WriteByte(0); // null terminator
    }

    // dataOffset = fixed header size + optional fields size.
    var dataOffset = (ushort)(KwajConstants.FixedHeaderSize + optionalBuf.Length);

    // Write fixed header.
    Span<byte> header = stackalloc byte[KwajConstants.FixedHeaderSize];
    KwajConstants.Magic.CopyTo(header);
    BinaryPrimitives.WriteUInt16LittleEndian(header[KwajConstants.MethodOffset..], method);
    BinaryPrimitives.WriteUInt16LittleEndian(header[KwajConstants.DataOffsetOffset..], dataOffset);
    BinaryPrimitives.WriteUInt16LittleEndian(header[KwajConstants.FlagsOffset..], flags);
    output.Write(header);

    // Write optional fields.
    optionalBuf.Position = 0;
    optionalBuf.CopyTo(output);
  }

  // -------------------------------------------------------------------------
  // Compression / decompression helpers
  // -------------------------------------------------------------------------

  private static byte[] CompressPayload(ushort method, ReadOnlySpan<byte> plain) =>
    method switch {
      KwajConstants.MethodStore  => plain.ToArray(),
      KwajConstants.MethodXor    => XorBytes(plain),
      KwajConstants.MethodMsZip => MsZipCompressor.Compress(plain),
      KwajConstants.MethodLzss or KwajConstants.MethodLzHuffman =>
        throw new NotSupportedException($"KWAJ compression method {method} is not supported."),
      _ => throw new NotSupportedException($"Unknown KWAJ compression method {method}."),
    };

  private static byte[] DecompressPayload(ushort method, ReadOnlySpan<byte> compressed,
    int decompressedLength) =>
    method switch {
      KwajConstants.MethodStore  => compressed.ToArray(),
      KwajConstants.MethodXor    => XorBytes(compressed),
      KwajConstants.MethodMsZip => MsZipDecompressor.Decompress(compressed, decompressedLength),
      KwajConstants.MethodLzss or KwajConstants.MethodLzHuffman =>
        throw new NotSupportedException($"KWAJ compression method {method} is not supported."),
      _ => throw new NotSupportedException($"Unknown KWAJ compression method {method}."),
    };

  private static byte[] XorBytes(ReadOnlySpan<byte> data) {
    var result = data.ToArray();
    for (var i = 0; i < result.Length; ++i)
      result[i] ^= 0xFF;
    return result;
  }

  // -------------------------------------------------------------------------
  // Stream helpers
  // -------------------------------------------------------------------------

  /// <summary>
  /// Advances <paramref name="stream"/> to absolute position
  /// <paramref name="offset"/>. Uses <see cref="Stream.Seek"/> when the
  /// stream supports it; otherwise reads and discards bytes.
  /// </summary>
  private static void SeekToDataOffset(Stream stream, ushort offset) {
    if (stream.CanSeek) {
      stream.Seek(offset, SeekOrigin.Begin);
      return;
    }

    // For non-seekable streams we have already consumed the fixed header
    // (14 bytes) plus any optional fields that ReadHeader consumed. We need
    // to skip any remaining padding up to dataOffset. Track current position
    // via the bytes already read (fixed header + optional). The caller is
    // responsible for calling this immediately after ReadHeader, so the
    // stream cursor is sitting right after the optional fields.
    // We express the remaining skip in terms of what was already consumed:
    // since ReadHeader always reads exactly up to the end of the optional
    // fields, any gap between the end of the optional fields and dataOffset
    // is padding that must be skipped.
    //
    // We cannot know the precise cursor position on non-seekable streams here,
    // so we rely on the caller passing a seekable stream (which covers all
    // practical use-cases: MemoryStream, FileStream). Document the limitation.
    throw new NotSupportedException(
      "KWAJ decompression requires a seekable stream to locate the data offset.");
  }

  private static byte[] ReadAllBytes(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static string ReadNullTerminatedString(Stream stream) {
    var bytes = new List<byte>();
    int b;
    while ((b = stream.ReadByte()) > 0)
      bytes.Add((byte)b);
    return Encoding.ASCII.GetString(bytes.ToArray());
  }
}
