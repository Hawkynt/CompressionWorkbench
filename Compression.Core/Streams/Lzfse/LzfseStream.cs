using System.Buffers.Binary;

namespace FileFormat.Lzfse;

/// <summary>
/// Provides static methods for compressing and decompressing data using Apple's LZFSE block format
/// with LZVN as the encoder's sub-algorithm.
/// </summary>
/// <remarks>
/// LZFSE is a block-based compression format developed by Apple. Each block starts with a 4-byte
/// little-endian magic number identifying the block type. Decoding supports Apple's entropy-coded
/// <c>bvx1</c>/<c>bvx2</c> blocks, LZVN <c>bvxn</c> blocks, uncompressed <c>bvx-</c> blocks and
/// <c>bvx$</c> end markers. Compression authors the entropy-coded <c>bvx2</c> block, the LZVN
/// <c>bvxn</c> block or the stored <c>bvx-</c> block, choosing per block whichever is smallest;
/// <c>bvx1</c> is read but never written, as Apple's own compressor does not emit it either.
/// </remarks>
public static class LzfseStream {
  private const uint MagicEndOfStream = 0x24787662;
  private const uint MagicUncompressed = 0x2D787662;
  private const uint MagicLzfseV1 = 0x31787662;
  private const uint MagicLzfseV2 = 0x32787662;
  private const uint MagicLzvn = 0x6E787662;
  private const int BlockSize = LzfseFseEncoder.MaxBlockRawBytes;

  // Hash-chain candidates the bvx2 match search tries at each position. Four is
  // Apple's own default trade-off between parse quality and encode time.
  private const int MatchSearchDepth = 4;

  /// <summary>
  /// Compresses data from <paramref name="input"/> and writes an LZFSE-format stream to <paramref name="output"/>.
  /// </summary>
  public static void Compress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var rawBuffer = new byte[BlockSize];
    Span<byte> header = stackalloc byte[12];

    while (true) {
      var bytesRead = ReadFully(input, rawBuffer, 0, rawBuffer.Length);
      if (bytesRead == 0)
        break;

      var rawSpan = rawBuffer.AsSpan(0, bytesRead);
      var lzvn = Lzvn.Compress(rawSpan);
      var lzvnWins = lzvn.Length < bytesRead;
      var bestFallbackBytes = lzvnWins ? 12 + lzvn.Length : 8 + bytesRead;

      // The entropy-coded block is taken only when it beats the block this writer
      // would otherwise have emitted, so no input ever grows because of it.
      var entropy = LzfseFseEncoder.EncodeBlock(rawSpan, MatchSearchDepth);
      if (entropy is { Length: > 0 } && entropy.Length < bestFallbackBytes) {
        output.Write(entropy);
        continue;
      }

      if (lzvnWins) {
        BinaryPrimitives.WriteUInt32LittleEndian(header, MagicLzvn);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)bytesRead);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)lzvn.Length);
        output.Write(header[..12]);
        output.Write(lzvn);
      } else {
        BinaryPrimitives.WriteUInt32LittleEndian(header, MagicUncompressed);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)bytesRead);
        output.Write(header[..8]);
        output.Write(rawSpan);
      }
    }

    BinaryPrimitives.WriteUInt32LittleEndian(header, MagicEndOfStream);
    output.Write(header[..4]);
  }

  /// <summary>
  /// Decompresses an Apple LZFSE stream from <paramref name="input"/> to <paramref name="output"/>.
  /// </summary>
  /// <exception cref="InvalidDataException">The stream is truncated, malformed, or uses an unknown block magic.</exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> magicBuf = stackalloc byte[4];
    Span<byte> headerBuf = stackalloc byte[8];
    byte[] history = [];

    while (true) {
      var bytesRead = ReadFully(input, magicBuf);
      if (bytesRead == 0)
        break;
      if (bytesRead < 4)
        throw new InvalidDataException("Unexpected end of LZFSE stream: incomplete block magic.");

      var magic = BinaryPrimitives.ReadUInt32LittleEndian(magicBuf);
      switch (magic) {
        case MagicEndOfStream:
          return;

        case MagicUncompressed: {
          input.ReadExactly(headerBuf[..4]);
          var rawBytes = CheckedLength(BinaryPrimitives.ReadUInt32LittleEndian(headerBuf), "uncompressed block");
          CopyExactly(input, output, rawBytes, ref history);
          break;
        }

        case MagicLzvn: {
          input.ReadExactly(headerBuf[..8]);
          var rawBytes = CheckedLength(BinaryPrimitives.ReadUInt32LittleEndian(headerBuf), "LZVN output");
          var payloadBytes = CheckedLength(BinaryPrimitives.ReadUInt32LittleEndian(headerBuf[4..]), "LZVN payload");
          var payload = new byte[payloadBytes];
          input.ReadExactly(payload);

          var decoded = new byte[rawBytes];
          var actualDecoded = Lzvn.Decompress(payload, decoded);
          if (actualDecoded != rawBytes)
            throw new InvalidDataException($"LZVN block decoded {actualDecoded} bytes but header specified {rawBytes}.");

          output.Write(decoded, 0, actualDecoded);
          AppendHistory(ref history, decoded.AsSpan(0, actualDecoded));
          break;
        }

        case MagicLzfseV1:
        case MagicLzfseV2: {
          var decoded = LzfseFseDecoder.DecodeBlock(input, magic, history);
          output.Write(decoded);
          AppendHistory(ref history, decoded);
          break;
        }

        default:
          throw new InvalidDataException($"Unknown LZFSE block magic: 0x{magic:X8}.");
      }
    }
  }

  private static int CheckedLength(uint value, string field) {
    if (value > int.MaxValue)
      throw new InvalidDataException($"LZFSE {field} length {value} exceeds the managed buffer limit.");
    return checked((int)value);
  }

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

  private static void CopyExactly(Stream source, Stream destination, int count, ref byte[] history) {
    var buffer = new byte[Math.Min(count, 8192)];
    var remaining = count;
    while (remaining > 0) {
      var toRead = Math.Min(remaining, buffer.Length);
      source.ReadExactly(buffer.AsSpan(0, toRead));
      destination.Write(buffer, 0, toRead);
      AppendHistory(ref history, buffer.AsSpan(0, toRead));
      remaining -= toRead;
    }
  }

  private static void AppendHistory(ref byte[] history, ReadOnlySpan<byte> data) {
    const int maximum = LzfseFseDecoder.MaxMatchDistance;
    if (data.IsEmpty)
      return;
    if (data.Length >= maximum) {
      history = data[^maximum..].ToArray();
      return;
    }

    var keep = Math.Min(history.Length, maximum - data.Length);
    var next = new byte[keep + data.Length];
    history.AsSpan(history.Length - keep, keep).CopyTo(next);
    data.CopyTo(next.AsSpan(keep));
    history = next;
  }
}
