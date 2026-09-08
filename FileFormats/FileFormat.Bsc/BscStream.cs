#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Bsc;

internal enum BscSortingContexts : byte {
  Following = 1,
  Preceding = 2,
}

/// <summary>
/// BSC (libbsc) file-stream framing around <see cref="BscBlockCodec"/>.
/// </summary>
public static class BscStream {
  private static readonly byte[] Magic = [0x62, 0x73, 0x63, 0x31]; // "bsc1"
  private const int BscBlockHeaderSize = 10;
  private const int InternalHeaderSize = 28;
  private const int MinimumEncodedBlockSize = BscBlockHeaderSize + InternalHeaderSize;

  internal const int MinimumBlockSize = 10_000;
  internal const int DefaultBlockSize = 25 * 1024 * 1024;
  internal const int MaximumBlockSize = 2047 * 1024 * 1024;

  /// <summary>Encodes using libbsc's default 25 MiB file block size.</summary>
  public static void Compress(Stream input, Stream output)
    => Compress(input, output, DefaultBlockSize, BscSortingContexts.Following);

  internal static void Compress(Stream input, Stream output, int blockSize, BscSortingContexts sortingContexts) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (blockSize is < MinimumBlockSize or > MaximumBlockSize)
      throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize,
        $"BSC block size must be between {MinimumBlockSize} and {MaximumBlockSize} bytes.");
    if (sortingContexts is not (BscSortingContexts.Following or BscSortingContexts.Preceding))
      throw new ArgumentOutOfRangeException(nameof(sortingContexts));

    var data = ReadRemaining(input);
    var blockCount = data.Length == 0 ? 0 : ((data.Length - 1) / blockSize) + 1;

    output.Write(Magic);
    Span<byte> countBytes = stackalloc byte[sizeof(int)];
    BinaryPrimitives.WriteInt32LittleEndian(countBytes, blockCount);
    output.Write(countBytes);

    var offset = 0;
    for (var block = 0; block < blockCount; ++block) {
      var length = Math.Min(blockSize, data.Length - offset);
      WriteBlock(data.AsSpan(offset, length), output, offset, sortingContexts);
      offset += length;
    }
  }

  /// <summary>Decodes a bsc1 stream.</summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> magic = stackalloc byte[4];
    input.ReadExactly(magic);
    if (!magic.SequenceEqual(Magic))
      throw new InvalidDataException("Not a BSC stream: invalid magic bytes");

    Span<byte> countBytes = stackalloc byte[sizeof(int)];
    input.ReadExactly(countBytes);
    var blockCount = BinaryPrimitives.ReadInt32LittleEndian(countBytes);
    if (blockCount < 0)
      throw new InvalidDataException($"BSC: invalid block count {blockCount}");

    if (input.CanSeek) {
      var remaining = input.Length - input.Position;
      if ((long)blockCount * MinimumEncodedBlockSize > remaining)
        throw new InvalidDataException($"BSC: block count {blockCount} exceeds the available stream data");
    }

    Span<byte> fileHeader = stackalloc byte[BscBlockHeaderSize];
    Span<byte> blockHeader = stackalloc byte[InternalHeaderSize];
    long expectedSequentialOffset = 0;

    for (var block = 0; block < blockCount; ++block) {
      input.ReadExactly(fileHeader);
      var blockOffset = BinaryPrimitives.ReadInt64LittleEndian(fileHeader);
      var recordSize = fileHeader[8];
      var sortingContexts = (BscSortingContexts)fileHeader[9];

      if (blockOffset < 0)
        throw new InvalidDataException($"BSC: invalid negative block offset {blockOffset}");
      if (recordSize != 1)
        throw new NotSupportedException($"BSC: record reordering (record size {recordSize}) is not supported");
      if (sortingContexts is not (BscSortingContexts.Following or BscSortingContexts.Preceding))
        throw new InvalidDataException($"BSC: invalid sorting-context order {(byte)sortingContexts}");

      input.ReadExactly(blockHeader);
      var encodedBlockSize = BinaryPrimitives.ReadInt32LittleEndian(blockHeader);
      var dataSize = BinaryPrimitives.ReadInt32LittleEndian(blockHeader[4..]);
      var mode = BinaryPrimitives.ReadInt32LittleEndian(blockHeader[8..]);
      var primaryIndex = BinaryPrimitives.ReadInt32LittleEndian(blockHeader[12..]);
      var dataChecksum = (uint)BinaryPrimitives.ReadInt32LittleEndian(blockHeader[16..]);
      var compressedChecksum = (uint)BinaryPrimitives.ReadInt32LittleEndian(blockHeader[20..]);
      var headerChecksum = (uint)BinaryPrimitives.ReadInt32LittleEndian(blockHeader[24..]);

      if (BscBlockCodec.Adler32(blockHeader[..24]) != headerChecksum)
        throw new InvalidDataException("BSC: header checksum mismatch");
      if (encodedBlockSize is < InternalHeaderSize or > MaximumBlockSize + InternalHeaderSize)
        throw new InvalidDataException($"BSC: invalid block size {encodedBlockSize}");
      if (dataSize < 0 || dataSize > MaximumBlockSize)
        throw new InvalidDataException($"BSC: invalid data size {dataSize}");
      if (blockOffset > long.MaxValue - dataSize)
        throw new InvalidDataException("BSC: block range overflows the output address space");

      var payloadSize = encodedBlockSize - InternalHeaderSize;
      if (mode != 0 && payloadSize > dataSize)
        throw new InvalidDataException("BSC: compressed block is larger than its decoded data");
      if (input.CanSeek && payloadSize > input.Length - input.Position)
        throw new InvalidDataException("BSC: truncated block payload");

      var payload = new byte[payloadSize];
      if (payloadSize != 0)
        input.ReadExactly(payload);
      if (BscBlockCodec.Adler32(payload) != compressedChecksum)
        throw new InvalidDataException("BSC: compressed payload checksum mismatch");

      var decoded = BscBlockCodec.Decode(payload, mode, primaryIndex, dataSize);
      // libbsc checks adler32_data before the outer preceding-context reversal.
      if (BscBlockCodec.Adler32(decoded) != dataChecksum)
        throw new InvalidDataException("BSC: original data checksum mismatch");
      if (sortingContexts == BscSortingContexts.Preceding)
        Array.Reverse(decoded);

      if (output.CanSeek)
        output.Position = blockOffset;
      else if (blockOffset != expectedSequentialOffset)
        throw new NotSupportedException("BSC: out-of-order blocks require a seekable output stream");

      output.Write(decoded);
      expectedSequentialOffset = blockOffset + decoded.LongLength;
    }

    if (output.CanSeek)
      output.Position = output.Length;
  }

  private static void WriteBlock(
      ReadOnlySpan<byte> data,
      Stream output,
      long blockOffset,
      BscSortingContexts sortingContexts) {
    var encoded = BscBlockCodec.Encode(data, sortingContexts);
    var compressedChecksum = BscBlockCodec.Adler32(encoded.Payload);

    Span<byte> blockHeader = stackalloc byte[InternalHeaderSize];
    BinaryPrimitives.WriteInt32LittleEndian(blockHeader, checked(InternalHeaderSize + encoded.Payload.Length));
    BinaryPrimitives.WriteInt32LittleEndian(blockHeader[4..], data.Length);
    BinaryPrimitives.WriteInt32LittleEndian(blockHeader[8..], encoded.Mode);
    BinaryPrimitives.WriteInt32LittleEndian(blockHeader[12..], encoded.PrimaryIndex);
    BinaryPrimitives.WriteInt32LittleEndian(blockHeader[16..], (int)encoded.DataChecksum);
    BinaryPrimitives.WriteInt32LittleEndian(blockHeader[20..], (int)compressedChecksum);
    BinaryPrimitives.WriteInt32LittleEndian(blockHeader[24..], (int)BscBlockCodec.Adler32(blockHeader[..24]));

    Span<byte> fileHeader = stackalloc byte[BscBlockHeaderSize];
    BinaryPrimitives.WriteInt64LittleEndian(fileHeader, blockOffset);
    fileHeader[8] = 1;
    fileHeader[9] = (byte)encoded.SortingContexts;

    output.Write(fileHeader);
    output.Write(blockHeader);
    output.Write(encoded.Payload);
  }

  private static byte[] ReadRemaining(Stream stream) {
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }
}
