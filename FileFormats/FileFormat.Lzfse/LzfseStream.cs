using System.Buffers.Binary;

namespace FileFormat.Lzfse;

/// <summary>
/// Selects which LZFSE block encoder is preferred for each input block.
/// </summary>
public enum LzfseBlockMode {
  /// <summary>Try LZFSE and LZVN and emit the smallest representation per block.</summary>
  Auto,
  /// <summary>Prefer entropy-coded LZFSE <c>bvx2</c> blocks, falling back to stored blocks when needed.</summary>
  Lzfse,
  /// <summary>Prefer LZVN <c>bvxn</c> blocks, falling back to stored blocks when needed.</summary>
  Lzvn,
}

/// <summary>
/// Controls the depth of the managed LZFSE match search.
/// </summary>
public enum LzfseCompressionLevel {
  /// <summary>Probe one hash-chain candidate per position.</summary>
  Fast,
  /// <summary>Probe four hash-chain candidates per position.</summary>
  Balanced,
  /// <summary>Probe eight hash-chain candidates per position.</summary>
  Maximum,
}

/// <summary>
/// Provides static methods for compressing and decompressing Apple's LZFSE block format.
/// </summary>
/// <remarks>
/// The decoder accepts stored (<c>bvx-</c>), LZVN (<c>bvxn</c>), LZFSE V1 (<c>bvx1</c>)
/// and LZFSE V2 (<c>bvx2</c>) blocks. The writer emits V2 for entropy-coded blocks because
/// V2 carries the same FSE model as V1 in a substantially smaller variable header.
/// </remarks>
public static class LzfseStream {
  private const uint MagicEndOfStream = 0x24787662;
  private const uint MagicUncompressed = 0x2D787662;
  private const uint MagicLzvn = 0x6E787662;
  private const int LzvnHeaderSize = 12;
  private const int StoredHeaderSize = 8;
  private const int MaximumV2HeaderSize = 4096;

  /// <summary>The default raw block size used by the managed writer.</summary>
  public const int DefaultBlockSize = LzfseCompressedBlock.MaximumRawBlockSize;

  /// <summary>
  /// Compresses data with the default adaptive encoder.
  /// </summary>
  public static void Compress(Stream input, Stream output) =>
    Compress(input, output, LzfseBlockMode.Auto, LzfseCompressionLevel.Balanced, DefaultBlockSize);

  /// <summary>
  /// Compresses data using the selected LZFSE block policy.
  /// </summary>
  public static void Compress(
      Stream input,
      Stream output,
      LzfseBlockMode mode,
      LzfseCompressionLevel level,
      int blockSize = DefaultBlockSize) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (blockSize is < 256 or > LzfseCompressedBlock.MaximumRawBlockSize)
      throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize,
        $"LZFSE block size must be between 256 and {LzfseCompressedBlock.MaximumRawBlockSize} bytes.");

    var rawBuffer = new byte[blockSize];
    while (true) {
      var bytesRead = ReadFully(input, rawBuffer);
      if (bytesRead == 0)
        break;

      var raw = rawBuffer.AsSpan(0, bytesRead);
      var block = SelectBlock(raw, mode, level);
      output.Write(block);
    }

    Span<byte> end = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(end, MagicEndOfStream);
    output.Write(end);
  }

  /// <summary>
  /// Decompresses an LZFSE block stream.
  /// </summary>
  /// <exception cref="InvalidDataException">The input is truncated or contains malformed block data.</exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> magicBytes = stackalloc byte[4];
    var history = new LzfseCompressedBlock.History();

    while (true) {
      var read = ReadFully(input, magicBytes);
      if (read == 0)
        return;
      if (read != magicBytes.Length)
        throw new InvalidDataException("Unexpected end of LZFSE stream: incomplete block magic.");

      var magic = BinaryPrimitives.ReadUInt32LittleEndian(magicBytes);
      switch (magic) {
        case MagicEndOfStream:
          return;

        case MagicUncompressed:
          DecodeStoredBlock(input, output, history);
          break;

        case MagicLzvn:
          DecodeLzvnBlock(input, output, history);
          break;

        case LzfseCompressedBlock.MagicV1:
          DecodeV1Block(input, output, history, magicBytes);
          break;

        case LzfseCompressedBlock.MagicV2:
          DecodeV2Block(input, output, history, magicBytes);
          break;

        default:
          throw new InvalidDataException($"Unknown LZFSE block magic: 0x{magic:X8}.");
      }
    }
  }

  private static byte[] SelectBlock(ReadOnlySpan<byte> raw, LzfseBlockMode mode, LzfseCompressionLevel level) {
    var storedLength = checked(StoredHeaderSize + raw.Length);
    byte[]? best = null;
    var bestLength = storedLength;

    if (mode is LzfseBlockMode.Auto or LzfseBlockMode.Lzvn) {
      var lzvnPayload = Lzvn.Compress(raw);
      var length = checked(LzvnHeaderSize + lzvnPayload.Length);
      if (length < bestLength) {
        best = WrapLzvn(raw.Length, lzvnPayload);
        bestLength = length;
      }
    }

    if (mode is LzfseBlockMode.Auto or LzfseBlockMode.Lzfse) {
      var parser = level switch {
        LzfseCompressionLevel.Fast => LzfseCompressedBlock.ParserStrength.Fast,
        LzfseCompressionLevel.Maximum => LzfseCompressedBlock.ParserStrength.Maximum,
        _ => LzfseCompressedBlock.ParserStrength.Balanced,
      };
      var compressed = LzfseCompressedBlock.EncodeV2(raw, parser);
      if (compressed is not null && compressed.Length < bestLength) {
        best = compressed;
        bestLength = compressed.Length;
      }
    }

    return best ?? WrapStored(raw);
  }

  private static byte[] WrapStored(ReadOnlySpan<byte> raw) {
    var result = new byte[checked(StoredHeaderSize + raw.Length)];
    BinaryPrimitives.WriteUInt32LittleEndian(result, MagicUncompressed);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), checked((uint)raw.Length));
    raw.CopyTo(result.AsSpan(StoredHeaderSize));
    return result;
  }

  private static byte[] WrapLzvn(int rawLength, ReadOnlySpan<byte> payload) {
    var result = new byte[checked(LzvnHeaderSize + payload.Length)];
    BinaryPrimitives.WriteUInt32LittleEndian(result, MagicLzvn);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), checked((uint)rawLength));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), checked((uint)payload.Length));
    payload.CopyTo(result.AsSpan(LzvnHeaderSize));
    return result;
  }

  private static void DecodeStoredBlock(Stream input, Stream output, LzfseCompressedBlock.History history) {
    Span<byte> lengthBytes = stackalloc byte[4];
    input.ReadExactly(lengthBytes);
    var rawLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
    var remaining = checked((int)rawLength);
    var buffer = new byte[Math.Min(Math.Max(remaining, 1), 8192)];

    while (remaining > 0) {
      var count = Math.Min(remaining, buffer.Length);
      input.ReadExactly(buffer.AsSpan(0, count));
      output.Write(buffer, 0, count);
      for (var i = 0; i < count; ++i)
        history.Append(buffer[i]);
      remaining -= count;
    }
  }

  private static void DecodeLzvnBlock(Stream input, Stream output, LzfseCompressedBlock.History history) {
    Span<byte> header = stackalloc byte[8];
    input.ReadExactly(header);
    var rawLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header));
    var payloadLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[4..]));
    var payload = new byte[payloadLength];
    input.ReadExactly(payload);

    var decoded = new byte[rawLength];
    var actual = Lzvn.Decompress(payload, decoded);
    if (actual != rawLength)
      throw new InvalidDataException($"LZVN block decoded {actual} bytes but header specified {rawLength}.");

    output.Write(decoded);
    foreach (var value in decoded)
      history.Append(value);
  }

  private static void DecodeV1Block(
      Stream input,
      Stream output,
      LzfseCompressedBlock.History history,
      ReadOnlySpan<byte> magicBytes) {
    var headerBytes = new byte[LzfseCompressedBlock.V1HeaderSize];
    magicBytes.CopyTo(headerBytes);
    input.ReadExactly(headerBytes.AsSpan(4));
    var header = LzfseCompressedBlock.ReadV1Header(headerBytes);
    var payload = new byte[header.PayloadBytes];
    input.ReadExactly(payload);
    LzfseCompressedBlock.Decode(header, payload, output, history);
  }

  private static void DecodeV2Block(
      Stream input,
      Stream output,
      LzfseCompressedBlock.History history,
      ReadOnlySpan<byte> magicBytes) {
    var fixedHeader = new byte[LzfseCompressedBlock.V2MinimumHeaderSize];
    magicBytes.CopyTo(fixedHeader);
    input.ReadExactly(fixedHeader.AsSpan(4));

    var headerSize = LzfseCompressedBlock.ReadV2HeaderSize(fixedHeader);
    if (headerSize is < LzfseCompressedBlock.V2MinimumHeaderSize or > MaximumV2HeaderSize)
      throw new InvalidDataException($"LZFSE bvx2 header size {headerSize} is outside the supported format bounds.");

    var headerBytes = new byte[headerSize];
    fixedHeader.CopyTo(headerBytes, 0);
    if (headerSize > fixedHeader.Length)
      input.ReadExactly(headerBytes.AsSpan(fixedHeader.Length));

    var header = LzfseCompressedBlock.ReadV2Header(headerBytes);
    var payload = new byte[header.PayloadBytes];
    input.ReadExactly(payload);
    LzfseCompressedBlock.Decode(header, payload, output, history);
  }

  private static int ReadFully(Stream source, Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var read = source.Read(buffer[total..]);
      if (read == 0)
        break;
      total += read;
    }
    return total;
  }
}
