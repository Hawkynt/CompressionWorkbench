#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Bsc;

internal enum BscSortingContexts : byte {
  Following = 1,
  Preceding = 2,
}

internal enum BscEntropyCoder : byte {
  Static = 1,
  Adaptive = 2,
  Fast = 3,
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

  /// <summary>Encodes using libbsc's default 25 MiB block size, following contexts and QLFC-static coder.</summary>
  public static void Compress(Stream input, Stream output)
    => Compress(input, output, DefaultBlockSize, BscSortingContexts.Following, BscEntropyCoder.Static);

  internal static void Compress(
      Stream input,
      Stream output,
      int blockSize,
      BscSortingContexts sortingContexts,
      BscEntropyCoder entropyCoder) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (blockSize is < MinimumBlockSize or > MaximumBlockSize)
      throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize,
        $"BSC block size must be between {MinimumBlockSize} and {MaximumBlockSize} bytes.");
    if (sortingContexts is not (BscSortingContexts.Following or BscSortingContexts.Preceding))
      throw new ArgumentOutOfRangeException(nameof(sortingContexts));
    if (entropyCoder is not (BscEntropyCoder.Static or BscEntropyCoder.Adaptive or BscEntropyCoder.Fast))
      throw new ArgumentOutOfRangeException(nameof(entropyCoder));

    var data = ReadRemaining(input);
    var blockCount = data.Length == 0 ? 0 : ((data.Length - 1) / blockSize) + 1;

    output.Write(Magic);
    Span<byte> countBytes = stackalloc byte[sizeof(int)];
    BinaryPrimitives.WriteInt32LittleEndian(countBytes, blockCount);
    output.Write(countBytes);

    var offset = 0;
    for (var block = 0; block < blockCount; ++block) {
      var length = Math.Min(blockSize, data.Length - offset);
      WriteBlock(data.AsSpan(offset, length), output, offset, sortingContexts, entropyCoder);
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
      var recordSize = (sbyte)fileHeader[8];
      var sortingContexts = (BscSortingContexts)fileHeader[9];

      if (blockOffset < 0)
        throw new InvalidDataException($"BSC: invalid negative block offset {blockOffset}");
      if (recordSize < 1)
        throw new InvalidDataException($"BSC: invalid record size {recordSize}");
      if (sortingContexts is not (BscSortingContexts.Following or BscSortingContexts.Preceding))
        throw new InvalidDataException($"BSC: invalid sorting-context order {(byte)sortingContexts}");

      input.ReadExactly(blockHeader);
      var encodedBlockSize = BinaryPrimitives.ReadInt32LittleEndian(blockHeader);
      if (encodedBlockSize is < InternalHeaderSize or > MaximumBlockSize + InternalHeaderSize)
        throw new InvalidDataException($"BSC: invalid block size {encodedBlockSize}");

      var payloadSize = encodedBlockSize - InternalHeaderSize;
      if (input.CanSeek && payloadSize > input.Length - input.Position)
        throw new InvalidDataException("BSC: truncated block payload");

      var payload = new byte[payloadSize];
      if (payloadSize != 0)
        input.ReadExactly(payload);

      var decoded = DecodeMemoryBlock(blockHeader, payload);
      if (blockOffset > long.MaxValue - decoded.Length)
        throw new InvalidDataException("BSC: block range overflows the output address space");
      if (sortingContexts == BscSortingContexts.Preceding)
        Array.Reverse(decoded);
      if (recordSize > 1)
        decoded = ReorderReverse(decoded, recordSize);

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
      BscSortingContexts sortingContexts,
      BscEntropyCoder entropyCoder) {
    var (block, contexts) = EncodeMemoryBlock(data, sortingContexts, entropyCoder);

    Span<byte> fileHeader = stackalloc byte[BscBlockHeaderSize];
    BinaryPrimitives.WriteInt64LittleEndian(fileHeader, blockOffset);
    fileHeader[8] = 1;
    fileHeader[9] = (byte)contexts;

    output.Write(fileHeader);
    output.Write(block);
  }

  /// <summary>
  /// One libbsc memory block: the 28-byte header <c>bsc_compress</c> writes, then its payload.
  /// Returns the sorting-context marker the bsc tool records beside it.
  /// </summary>
  internal static (byte[] Block, BscSortingContexts SortingContexts) EncodeMemoryBlock(
      ReadOnlySpan<byte> data,
      BscSortingContexts sortingContexts,
      BscEntropyCoder entropyCoder) {
    var encoded = data.IsEmpty
      ? new BscBlockCodec.EncodedBlock([], 0, 0, BscBlockCodec.Adler32([]), BscSortingContexts.Following)
      : BscBlockCodec.Encode(data, sortingContexts, entropyCoder);
    var block = new byte[InternalHeaderSize + encoded.Payload.Length];
    var header = block.AsSpan(0, InternalHeaderSize);
    BinaryPrimitives.WriteInt32LittleEndian(header, block.Length);
    BinaryPrimitives.WriteInt32LittleEndian(header[4..], data.Length);
    BinaryPrimitives.WriteInt32LittleEndian(header[8..], encoded.Mode);
    BinaryPrimitives.WriteInt32LittleEndian(header[12..], encoded.PrimaryIndex);
    BinaryPrimitives.WriteUInt32LittleEndian(header[16..], encoded.DataChecksum);
    BinaryPrimitives.WriteUInt32LittleEndian(header[20..], BscBlockCodec.Adler32(encoded.Payload));
    BinaryPrimitives.WriteUInt32LittleEndian(header[24..], BscBlockCodec.Adler32(header[..24]));
    encoded.Payload.CopyTo(block, InternalHeaderSize);
    return (block, encoded.SortingContexts);
  }

  /// <summary>
  /// Decodes one libbsc memory block (<c>bsc_decompress</c>): checks the three Adler-32 sums and
  /// returns the data in context order, before any preceding-context reversal.
  /// </summary>
  internal static byte[] DecodeMemoryBlock(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload) {
    if (header.Length < InternalHeaderSize)
      throw new InvalidDataException("BSC: truncated block header");

    var encodedBlockSize = BinaryPrimitives.ReadInt32LittleEndian(header);
    var dataSize = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
    var mode = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
    var primaryIndex = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
    var dataChecksum = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
    var compressedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
    var headerChecksum = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);

    if (BscBlockCodec.Adler32(header[..24]) != headerChecksum)
      throw new InvalidDataException("BSC: header checksum mismatch");
    if ((long)encodedBlockSize - InternalHeaderSize != payload.Length)
      throw new InvalidDataException($"BSC: block size {encodedBlockSize} does not match its payload");
    if (dataSize < 0 || dataSize > MaximumBlockSize)
      throw new InvalidDataException($"BSC: invalid data size {dataSize}");
    if (mode != 0 && payload.Length > dataSize)
      throw new InvalidDataException("BSC: compressed block is larger than its decoded data");
    if (BscBlockCodec.Adler32(payload) != compressedChecksum)
      throw new InvalidDataException("BSC: compressed payload checksum mismatch");

    var decoded = BscBlockCodec.Decode(payload, mode, primaryIndex, dataSize);
    if (BscBlockCodec.Adler32(decoded) != dataChecksum)
      throw new InvalidDataException("BSC: original data checksum mismatch");
    return decoded;
  }

  // bsc_reorder_reverse: the encoder (bsc -r) grouped byte j of every record into lane j; the
  // n % recordSize trailing bytes were left in place.
  private static byte[] ReorderReverse(byte[] data, int recordSize) {
    var result = (byte[])data.Clone();
    var chunk = data.Length / recordSize;
    for (var i = 0; i < chunk; ++i)
      for (var j = 0; j < recordSize; ++j)
        result[recordSize * i + j] = data[j * chunk + i];
    return result;
  }

  private static byte[] ReadRemaining(Stream stream) {
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }
}
