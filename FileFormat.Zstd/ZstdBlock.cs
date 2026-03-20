namespace FileFormat.Zstd;

/// <summary>
/// Reads and writes Zstandard block headers (3-byte little-endian).
/// Block header layout: bit 0 = lastBlock, bits 1-2 = blockType, bits 3-23 = blockSize.
/// </summary>
internal static class ZstdBlock {
  /// <summary>
  /// Reads a 3-byte block header from the stream.
  /// </summary>
  /// <param name="stream">The stream to read from.</param>
  /// <returns>The block type, block size, and whether this is the last block.</returns>
  /// <exception cref="InvalidDataException">The stream is truncated.</exception>
  public static (byte BlockType, int BlockSize, bool LastBlock) ReadBlockHeader(Stream stream) {
    Span<byte> buf = stackalloc byte[3];
    var read = 0;
    while (read < 3) {
      int b = stream.ReadByte();
      if (b < 0)
        throw new InvalidDataException("Truncated Zstandard block header.");
      buf[read++] = (byte)b;
    }

    int header = buf[0] | (buf[1] << 8) | (buf[2] << 16);

    bool lastBlock = (header & 1) != 0;
    byte blockType = (byte)((header >> 1) & 3);
    int blockSize = header >> 3;

    return (blockType, blockSize, lastBlock);
  }

  /// <summary>
  /// Writes a 3-byte block header to the stream.
  /// </summary>
  /// <param name="stream">The stream to write to.</param>
  /// <param name="blockType">The block type (0=Raw, 1=RLE, 2=Compressed).</param>
  /// <param name="blockSize">The block size in bytes (up to 21 bits).</param>
  /// <param name="lastBlock">Whether this is the last block in the frame.</param>
  public static void WriteBlockHeader(Stream stream, byte blockType, int blockSize, bool lastBlock) {
    int header = (lastBlock ? 1 : 0)
          | ((blockType & 3) << 1)
          | (blockSize << 3);

    Span<byte> buf = stackalloc byte[3];
    buf[0] = (byte)(header & 0xFF);
    buf[1] = (byte)((header >> 8) & 0xFF);
    buf[2] = (byte)((header >> 16) & 0xFF);
    stream.Write(buf);
  }
}
