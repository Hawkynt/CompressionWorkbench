using System.Buffers.Binary;

namespace FileFormat.Lzfse;

/// <summary>
/// Provides static methods for compressing and decompressing data using Apple's LZFSE block format
/// with LZVN as the sub-algorithm.
/// </summary>
/// <remarks>
/// LZFSE is a block-based compression format developed by Apple. Each block starts with a 4-byte
/// little-endian magic number identifying the block type. This implementation supports LZVN
/// compressed blocks (<c>bvxn</c>), uncompressed blocks (<c>bvx-</c>), and end-of-stream markers
/// (<c>bvx$</c>). LZFSE V1/V2 blocks (which require FSE/tANS entropy coding) are not currently
/// supported for decoding.
/// </remarks>
public static class LzfseStream {

  /// <summary>Magic for end-of-stream block: <c>bvx$</c> as LE uint32.</summary>
  private const uint MagicEndOfStream = 0x24787662;

  /// <summary>Magic for uncompressed block: <c>bvx-</c> as LE uint32.</summary>
  private const uint MagicUncompressed = 0x2D787662;

  /// <summary>Magic for LZFSE V1 block: <c>bvx1</c> as LE uint32.</summary>
  private const uint MagicLzfseV1 = 0x31787662;

  /// <summary>Magic for LZFSE V2 block: <c>bvx2</c> as LE uint32.</summary>
  private const uint MagicLzfseV2 = 0x32787662;

  /// <summary>Magic for LZVN block: <c>bvxn</c> as LE uint32.</summary>
  private const uint MagicLzvn = 0x6E787662;

  /// <summary>Maximum size for a single LZVN block's uncompressed data.</summary>
  private const int LzvnBlockSize = 65536;

  /// <summary>
  /// Compresses data from <paramref name="input"/> and writes an LZFSE-format stream to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The stream containing uncompressed data.</param>
  /// <param name="output">The stream to which the compressed LZFSE data is written.</param>
  /// <remarks>
  /// Data is compressed using LZVN blocks. If a block does not compress well (compressed size
  /// is not smaller than raw size), an uncompressed block is emitted instead. The stream is
  /// terminated with a <c>bvx$</c> end-of-stream marker.
  /// </remarks>
  public static void Compress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var rawBuffer = new byte[LzvnBlockSize];
    Span<byte> header = stackalloc byte[12];

    while (true) {
      var bytesRead = ReadFully(input, rawBuffer, 0, rawBuffer.Length);
      if (bytesRead == 0)
        break;

      var rawSpan = rawBuffer.AsSpan(0, bytesRead);

      // Attempt LZVN compression.
      var compressed = Lzvn.Compress(rawSpan);

      if (compressed.Length < bytesRead) {
        // Write LZVN block header.
        BinaryPrimitives.WriteUInt32LittleEndian(header, MagicLzvn);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)bytesRead);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)compressed.Length);
        output.Write(header[..12]);
        output.Write(compressed);
      } else {
        // Write uncompressed block header.
        BinaryPrimitives.WriteUInt32LittleEndian(header, MagicUncompressed);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)bytesRead);
        output.Write(header[..8]);
        output.Write(rawSpan);
      }
    }

    // Write end-of-stream block.
    BinaryPrimitives.WriteUInt32LittleEndian(header, MagicEndOfStream);
    output.Write(header[..4]);
  }

  /// <summary>
  /// Decompresses an LZFSE-format stream from <paramref name="input"/> and writes the result to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The stream containing LZFSE-compressed data.</param>
  /// <param name="output">The stream to which the decompressed data is written.</param>
  /// <exception cref="InvalidDataException">
  /// Thrown when an unknown block magic is encountered or block data is malformed.
  /// </exception>
  /// <exception cref="NotSupportedException">
  /// Thrown when an LZFSE V1 or V2 block is encountered, as FSE/tANS decoding is not implemented.
  /// </exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> magicBuf = stackalloc byte[4];
    Span<byte> headerBuf = stackalloc byte[8]; // max additional header bytes

    while (true) {
      var bytesRead = ReadFully(input, magicBuf);
      if (bytesRead == 0)
        break; // graceful end if no more data

      if (bytesRead < 4)
        throw new InvalidDataException("Unexpected end of LZFSE stream: incomplete block magic.");

      var magic = BinaryPrimitives.ReadUInt32LittleEndian(magicBuf);

      switch (magic) {
        case MagicEndOfStream:
          return;

        case MagicUncompressed: {
          input.ReadExactly(headerBuf[..4]);
          var rawBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(headerBuf);
          CopyExactly(input, output, rawBytes);
          break;
        }

        case MagicLzvn: {
          input.ReadExactly(headerBuf[..8]);
          var rawBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(headerBuf);
          var payloadBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(headerBuf[4..]);

          var payload = new byte[payloadBytes];
          input.ReadExactly(payload);

          var decoded = new byte[rawBytes];
          var actualDecoded = Lzvn.Decompress(payload, decoded);
          if (actualDecoded != rawBytes)
            throw new InvalidDataException($"LZVN block decoded {actualDecoded} bytes but header specified {rawBytes}.");

          output.Write(decoded, 0, actualDecoded);
          break;
        }

        case MagicLzfseV1:
          throw new NotSupportedException("LZFSE V1 blocks (FSE/tANS) are not yet supported; only LZVN blocks are supported.");

        case MagicLzfseV2:
          throw new NotSupportedException("LZFSE V2 blocks (FSE/tANS) are not yet supported; only LZVN blocks are supported.");

        default:
          throw new InvalidDataException($"Unknown LZFSE block magic: 0x{magic:X8}.");
      }
    }
  }

  /// <summary>
  /// Reads bytes from <paramref name="source"/> into a span,
  /// returning the number actually read (which may be less at EOF).
  /// </summary>
  private static int ReadFully(Stream source, Span<byte> buffer) {
    var totalRead = 0;
    while (totalRead < buffer.Length) {
      var n = source.Read(buffer[totalRead..]);
      if (n == 0)
        break;
      totalRead += n;
    }

    return totalRead;
  }

  /// <summary>
  /// Reads up to <paramref name="count"/> bytes from <paramref name="source"/> into
  /// <paramref name="buffer"/>, returning the number actually read.
  /// </summary>
  private static int ReadFully(Stream source, byte[] buffer, int offset, int count) {
    var totalRead = 0;
    while (totalRead < count) {
      var n = source.Read(buffer, offset + totalRead, count - totalRead);
      if (n == 0)
        break;
      totalRead += n;
    }

    return totalRead;
  }

  /// <summary>
  /// Copies exactly <paramref name="count"/> bytes from <paramref name="source"/> to
  /// <paramref name="destination"/>.
  /// </summary>
  private static void CopyExactly(Stream source, Stream destination, int count) {
    var buffer = new byte[Math.Min(count, 8192)];
    var remaining = count;
    while (remaining > 0) {
      var toRead = Math.Min(remaining, buffer.Length);
      source.ReadExactly(buffer.AsSpan(0, toRead));
      destination.Write(buffer, 0, toRead);
      remaining -= toRead;
    }
  }
}
