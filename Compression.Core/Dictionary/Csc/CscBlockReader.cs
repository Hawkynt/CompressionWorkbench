namespace Compression.Core.Dictionary.Csc;

/// <summary>
/// Reads the tagged blocks written by <see cref="CscBlockWriter"/> back into their two channels.
/// A block of the channel not currently asked for is queued until that channel wants it.
/// </summary>
internal sealed class CscBlockReader(Stream input, uint blockSize) {
  private readonly Queue<byte[]> _rangeCoderBlocks = new();
  private readonly Queue<byte[]> _bitCoderBlocks = new();

  /// <summary>Returns the next block of range-coder bytes.</summary>
  /// <exception cref="InvalidDataException">The stream ends or a block header is invalid.</exception>
  public byte[] ReadRangeCoderBlock() => this.ReadBlock(rangeCoder: true);

  /// <summary>Returns the next block of bit-coder bytes.</summary>
  /// <exception cref="InvalidDataException">The stream ends or a block header is invalid.</exception>
  public byte[] ReadBitCoderBlock() => this.ReadBlock(rangeCoder: false);

  private byte[] ReadBlock(bool rangeCoder) {
    var wanted = rangeCoder ? this._rangeCoderBlocks : this._bitCoderBlocks;
    if (wanted.TryDequeue(out var queued))
      return queued;

    Span<byte> header = stackalloc byte[3];
    for (;;) {
      var flag = input.ReadByte();
      if (flag < 0)
        throw new InvalidDataException("CSC stream ended before its end-of-stream marker.");

      uint size;
      if ((flag & 0x40) != 0)
        size = blockSize;
      else {
        if (input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length)
          throw new InvalidDataException("CSC block header is truncated.");
        size = ((uint)header[0] << 16) | ((uint)header[1] << 8) | header[2];
      }

      if (size > blockSize)
        throw new InvalidDataException($"CSC block of {size} bytes exceeds the stream's block size {blockSize}.");

      var block = new byte[size];
      if (input.ReadAtLeast(block, block.Length, throwOnEndOfStream: false) != block.Length)
        throw new InvalidDataException("CSC block is truncated.");

      // libcsc refuses empty blocks; skipping them instead is the tolerant reading of the same stream.
      if (size == 0)
        continue;

      if (((flag & 0x80) != 0) == rangeCoder)
        return block;

      (rangeCoder ? this._bitCoderBlocks : this._rangeCoderBlocks).Enqueue(block);
    }
  }
}
