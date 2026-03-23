using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.DataStructures;
using Compression.Core.Dictionary.Zstd;
using Compression.Core.Entropy.Fse;

namespace FileFormat.Zstd;

/// <summary>
/// Decompresses Zstandard format data from a stream.
/// Supports raw, RLE, and compressed block types with full sequence decoding.
/// </summary>
internal sealed class ZstdDecompressor {
  private readonly Stream _input;
  private byte[] _outputBuffer;
  private int _outputPos;
  private int _outputAvailable;
  private bool _finished;
  private bool _lastBlockSeen;
  private readonly bool _verifyChecksum;

  private readonly SlidingWindow _window;
  private readonly XxHash64 _hasher;
  private int[] _repeatOffsets;
  private int[]? _huffmanWeights;
  private FseTable? _prevLlTable;
  private FseTable? _prevOfTable;
  private FseTable? _prevMlTable;

  /// <summary>
  /// Initializes a new <see cref="ZstdDecompressor"/> by reading the frame header.
  /// </summary>
  /// <param name="input">The stream to read compressed data from.</param>
  /// <param name="dictionary">Optional Zstd dictionary for prepopulating the window.</param>
  /// <exception cref="InvalidDataException">The frame header is invalid.</exception>
  public ZstdDecompressor(Stream input, ZstdDictionary? dictionary = null) {
    this._input = input;

    // Read frame magic
    Span<byte> magicBuf = stackalloc byte[4];
    ReadExact(magicBuf);
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(magicBuf);
    if (magic != ZstdConstants.FrameMagic)
      throw new InvalidDataException($"Invalid Zstandard frame magic: 0x{magic:X8}");

    // Read frame header
    var header = ZstdFrameHeader.Read(input, out _);

    var windowSize = Math.Max(header.WindowSize, 1024);
    this._verifyChecksum = header.ContentChecksum;

    this._window = new SlidingWindow(windowSize);
    this._hasher = new XxHash64();
    this._repeatOffsets = [1, 4, 8];
    this._outputBuffer = [];
    this._outputPos = 0;
    this._outputAvailable = 0;

    // Apply dictionary: prepopulate window and set repeat offsets
    if (dictionary != null) {
      foreach (var b in dictionary.Content)
        this._window.WriteByte(b);
      if (dictionary.RepeatOffsets.Length >= 3)
        this._repeatOffsets = [dictionary.RepeatOffsets[0],
                               dictionary.RepeatOffsets[1],
                               dictionary.RepeatOffsets[2]];
    }
  }

  /// <summary>
  /// Gets whether decompression is finished.
  /// </summary>
  public bool IsFinished => this._finished;

  /// <summary>
  /// Reads decompressed data into the provided buffer.
  /// </summary>
  /// <param name="buffer">The buffer to read into.</param>
  /// <param name="offset">The offset in the buffer.</param>
  /// <param name="count">The maximum number of bytes to read.</param>
  /// <returns>The number of bytes read, or 0 if finished.</returns>
  public int Read(byte[] buffer, int offset, int count) {
    if (this._finished)
      return 0;

    var totalRead = 0;

    while (totalRead < count) {
      // Serve from buffer first
      if (this._outputPos < this._outputAvailable) {
        var toCopy = Math.Min(count - totalRead, this._outputAvailable - this._outputPos);
        this._outputBuffer.AsSpan(this._outputPos, toCopy).CopyTo(buffer.AsSpan(offset + totalRead));
        this._outputPos += toCopy;
        totalRead += toCopy;
        continue;
      }

      // If we've seen the last block and the buffer is exhausted, we're done
      if (this._lastBlockSeen) {
        this._finished = true;
        break;
      }

      // Need to decode the next block
      DecodeNextBlock();
    }

    return totalRead;
  }

  /// <summary>
  /// Decodes the next block from the input stream.
  /// </summary>
  private void DecodeNextBlock() {
    var (blockType, blockSize, lastBlock) = ZstdBlock.ReadBlockHeader(this._input);

    byte[] blockOutput;
    switch (blockType) {
      case ZstdConstants.BlockTypeRaw:
        blockOutput = new byte[blockSize];
        ReadExact(blockOutput);
        // Add raw block bytes to window
        for (var i = 0; i < blockOutput.Length; ++i)
          this._window.WriteByte(blockOutput[i]);
        break;

      case ZstdConstants.BlockTypeRle: {
        var b = this._input.ReadByte();
        if (b < 0)
          throw new InvalidDataException("Truncated RLE block data.");
        blockOutput = new byte[blockSize];
        blockOutput.AsSpan().Fill((byte)b);
        // Add RLE block bytes to window
        for (var i = 0; i < blockOutput.Length; ++i)
          this._window.WriteByte(blockOutput[i]);
        break;
      }

      case ZstdConstants.BlockTypeCompressed: {
        var compressedBlock = new byte[blockSize];
        ReadExact(compressedBlock);
        blockOutput = DecompressBlock(compressedBlock);
        break;
      }

      default:
        throw new InvalidDataException($"Reserved block type: {blockType}");
    }

    // Update checksum with decompressed output
    this._hasher.Update(blockOutput);

    this._outputBuffer = blockOutput;
    this._outputPos = 0;
    this._outputAvailable = blockOutput.Length;

    if (lastBlock) {
      this._lastBlockSeen = true;

      // Read and verify content checksum if present
      if (this._verifyChecksum) {
        Span<byte> checksumBuf = stackalloc byte[4];
        ReadExact(checksumBuf);
        var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(checksumBuf);
        var actualChecksum = (uint)(this._hasher.Value & 0xFFFFFFFF);

        if (expectedChecksum != actualChecksum)
          throw new InvalidDataException(
            $"Content checksum mismatch: expected 0x{expectedChecksum:X8}, got 0x{actualChecksum:X8}");
      }
    }
  }

  /// <summary>
  /// Decompresses a compressed block by decoding literals and sequences.
  /// </summary>
  private byte[] DecompressBlock(byte[] compressedBlock) {
    ReadOnlySpan<byte> blockData = compressedBlock;
    var pos = 0;

    // Decode literals section
    var literals = ZstdLiterals.DecompressLiterals(blockData, ref pos, ref this._huffmanWeights);

    // Decode sequences section
    var remainingSize = compressedBlock.Length - pos;
    var sequences = ZstdSequences.DecodeSequences(blockData, ref pos,
      remainingSize, this._repeatOffsets,
      ref this._prevLlTable, ref this._prevOfTable, ref this._prevMlTable);

    if (sequences.Length == 0) {
      // No sequences -- output is just literals
      for (var i = 0; i < literals.Length; ++i)
        this._window.WriteByte(literals[i]);
      return literals;
    }

    // Execute sequences to produce output
    return ExecuteSequences(literals, sequences);
  }

  /// <summary>
  /// Executes decoded sequences to produce the decompressed output.
  /// </summary>
  private byte[] ExecuteSequences(byte[] literals, ZstdSequence[] sequences) {
    // Calculate total output size
    var litConsumed = 0;
    var totalSize = 0;

    foreach (var seq in sequences) {
      totalSize += seq.LiteralLength + seq.MatchLength;
      litConsumed += seq.LiteralLength;
    }

    // Add remaining literals after the last sequence
    var remainingLiterals = literals.Length - litConsumed;
    if (remainingLiterals > 0)
      totalSize += remainingLiterals;

    var output = new byte[totalSize];
    var outPos = 0;
    var litPos = 0;

    foreach (var seq in sequences) {
      // Copy literal bytes
      if (seq.LiteralLength > 0) {
        literals.AsSpan(litPos, seq.LiteralLength).CopyTo(output.AsSpan(outPos));
        for (var i = 0; i < seq.LiteralLength; ++i)
          this._window.WriteByte(literals[litPos + i]);
        litPos += seq.LiteralLength;
        outPos += seq.LiteralLength;
      }

      // Execute match copy from window
      if (seq.MatchLength > 0 && seq.Offset > 0) {
        var matchOutput = output.AsSpan(outPos, seq.MatchLength);
        this._window.CopyFromWindow(seq.Offset, seq.MatchLength, matchOutput);
        outPos += seq.MatchLength;
      }
    }

    // Copy remaining literals
    if (litPos < literals.Length) {
      var remaining = literals.Length - litPos;
      literals.AsSpan(litPos, remaining).CopyTo(output.AsSpan(outPos));
      for (var i = 0; i < remaining; ++i)
        this._window.WriteByte(literals[litPos + i]);
    }

    return output;
  }

  /// <summary>
  /// Reads exactly the specified number of bytes from the input stream.
  /// </summary>
  private void ReadExact(Span<byte> buffer) {
    var offset = 0;
    while (offset < buffer.Length) {
      var b = this._input.ReadByte();
      if (b < 0)
        throw new InvalidDataException("Unexpected end of Zstandard stream.");
      buffer[offset++] = (byte)b;
    }
  }
}
