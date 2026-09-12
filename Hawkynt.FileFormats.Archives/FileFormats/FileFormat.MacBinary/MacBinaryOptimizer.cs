using System.Buffers;
using System.Buffers.Binary;

namespace FileFormat.MacBinary;

/// <summary>
/// Canonicalizes MacBinary I/II/III files without changing their represented Macintosh file.
/// </summary>
/// <remarks>
/// MacBinary is a container rather than a compressor, so there is no alternate entropy coding to
/// search. Optimization therefore removes bytes outside the declared container, canonicalizes
/// required zero padding and reserved header fields, and repairs the standard version/minimum-version
/// pair plus header CRC. Data, resource, secondary-header and Get Info payload bytes are preserved.
/// </remarks>
public static class MacBinaryOptimizer {
  private const int CopyBufferSize = 64 * 1024;

  /// <summary>
  /// Writes a canonical, semantically equivalent MacBinary representation of <paramref name="input"/>
  /// to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">Readable stream positioned at the MacBinary header.</param>
  /// <param name="output">Writable stream that receives the optimized file.</param>
  /// <exception cref="InvalidDataException">The input is malformed or truncated.</exception>
  /// <exception cref="NotSupportedException">The file declares a newer MacBinary version.</exception>
  public static void Optimize(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (!input.CanRead)
      throw new ArgumentException("Input stream must be readable.", nameof(input));
    if (!output.CanWrite)
      throw new ArgumentException("Output stream must be writable.", nameof(output));
    if (ReferenceEquals(input, output))
      throw new ArgumentException("MacBinary optimization requires distinct input and output streams.", nameof(output));

    Span<byte> header = stackalloc byte[MacBinaryConstants.HeaderSize];
    ReadExactly(input, header, "MacBinary header");

    if (header[0] != 0)
      throw new InvalidDataException("Invalid MacBinary header: byte 0 must be zero.");

    var nameLength = header[1];
    if (nameLength is < 1 or > 63)
      throw new InvalidDataException($"Invalid MacBinary filename length: {nameLength}.");

    var version = DetermineVersion(header);
    if (version >= MacBinaryConstants.Version2)
      VerifyHeaderCrc(header);

    var dataLength = BinaryPrimitives.ReadUInt32BigEndian(header[83..87]);
    var resourceLength = BinaryPrimitives.ReadUInt32BigEndian(header[87..91]);
    var commentLength = version >= MacBinaryConstants.Version2
      ? BinaryPrimitives.ReadUInt16BigEndian(header[99..101])
      : (ushort)0;
    var secondaryHeaderLength = version >= MacBinaryConstants.Version2
      ? BinaryPrimitives.ReadUInt16BigEndian(header[120..122])
      : (ushort)0;

    CanonicalizeHeader(header, version, nameLength);
    output.Write(header);

    CopySection(input, output, secondaryHeaderLength, "secondary header");
    CopySection(input, output, dataLength, "data fork");
    CopySection(input, output, resourceLength, "resource fork");
    CopySection(input, output, commentLength, "Get Info comment");

    // Bytes after the final declared, padded section are not part of the MacBinary container.
    // Deliberately do not copy them: trimming such transport/file-system garbage is the only
    // possible size reduction for an otherwise canonical, uncompressed MacBinary representation.
  }

  private static byte DetermineVersion(ReadOnlySpan<byte> header) {
    var writerVersion = header[122];
    var signature = BinaryPrimitives.ReadUInt32BigEndian(header[MacBinaryConstants.SignatureOffset..]);

    if (writerVersion == MacBinaryConstants.Version1)
      return MacBinaryConstants.Version1;
    if (writerVersion == MacBinaryConstants.Version2)
      return MacBinaryConstants.Version2;
    if (writerVersion == MacBinaryConstants.Version3) {
      if (signature != MacBinaryConstants.Signature)
        throw new InvalidDataException("MacBinary III header is missing the mBIN signature.");
      return MacBinaryConstants.Version3;
    }
    if (writerVersion > MacBinaryConstants.Version3)
      throw new NotSupportedException($"MacBinary version {writerVersion} is newer than the supported MacBinary III format.");

    throw new InvalidDataException($"Unsupported MacBinary writer version {writerVersion}.");
  }

  private static void VerifyHeaderCrc(ReadOnlySpan<byte> header) {
    var stored = BinaryPrimitives.ReadUInt16BigEndian(header[MacBinaryConstants.CrcOffset..]);
    var computed = MacBinaryReader.ComputeCrcCcitt(header[..MacBinaryConstants.CrcOffset]);
    if (stored != computed)
      throw new InvalidDataException(
        $"MacBinary header CRC mismatch: stored 0x{stored:X4}, computed 0x{computed:X4}.");
  }

  private static void CanonicalizeHeader(Span<byte> header, byte version, int nameLength) {
    // The filename occupies a fixed 63-byte field. Unused bytes are specified as zero.
    header[(2 + nameLength)..65].Clear();
    header[74] = 0;
    header[82] = 0;

    switch (version) {
      case MacBinaryConstants.Version1:
        // MacBinary I predates all fields from Get Info length through the header CRC.
        header[99..128].Clear();
        break;

      case MacBinaryConstants.Version2:
        // Bytes 102-115 are unused until MacBinary III introduces its extended Finder fields.
        header[102..116].Clear();
        header[122] = MacBinaryConstants.Version2;
        header[123] = MacBinaryConstants.Version2;
        header[126] = 0;
        header[127] = 0;
        WriteHeaderCrc(header);
        break;

      case MacBinaryConstants.Version3:
        BinaryPrimitives.WriteUInt32BigEndian(
          header[MacBinaryConstants.SignatureOffset..], MacBinaryConstants.Signature);
        header[108..116].Clear();
        header[122] = MacBinaryConstants.Version3;
        // MacBinary III deliberately remains readable by a MacBinary II implementation.
        header[123] = MacBinaryConstants.Version2;
        header[126] = 0;
        header[127] = 0;
        WriteHeaderCrc(header);
        break;
    }
  }

  private static void WriteHeaderCrc(Span<byte> header) {
    var crc = MacBinaryReader.ComputeCrcCcitt(header[..MacBinaryConstants.CrcOffset]);
    BinaryPrimitives.WriteUInt16BigEndian(header[MacBinaryConstants.CrcOffset..], crc);
  }

  private static void CopySection(Stream input, Stream output, long length, string sectionName) {
    if (length > 0)
      CopyExactly(input, output, length, sectionName);

    var paddingLength = PaddingFor(length);
    if (paddingLength == 0)
      return;

    Span<byte> discardedPadding = stackalloc byte[MacBinaryConstants.PaddingAlignment];
    ReadExactly(input, discardedPadding[..paddingLength], $"{sectionName} padding");

    Span<byte> zeroPadding = stackalloc byte[MacBinaryConstants.PaddingAlignment];
    zeroPadding.Clear();
    output.Write(zeroPadding[..paddingLength]);
  }

  private static int PaddingFor(long length) {
    var remainder = (int)(length % MacBinaryConstants.PaddingAlignment);
    return remainder == 0 ? 0 : MacBinaryConstants.PaddingAlignment - remainder;
  }

  private static void CopyExactly(Stream input, Stream output, long length, string sectionName) {
    var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
    try {
      while (length > 0) {
        var requested = (int)Math.Min(length, buffer.Length);
        var read = input.Read(buffer, 0, requested);
        if (read == 0)
          throw new InvalidDataException($"Stream is truncated in the MacBinary {sectionName}.");
        output.Write(buffer, 0, read);
        length -= read;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  private static void ReadExactly(Stream input, Span<byte> destination, string sectionName) {
    try {
      input.ReadExactly(destination);
    } catch (EndOfStreamException ex) {
      throw new InvalidDataException($"Stream is truncated in the {sectionName}.", ex);
    }
  }
}
