using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.MatchFinders;

namespace FileFormat.Zstd;

/// <summary>
/// Compresses data into Zstandard format and writes to a stream.
/// Buffers all input and writes the complete frame on <see cref="Finish"/>.
/// Uses hash-chain match finding with raw literal blocks and predefined FSE tables.
/// </summary>
internal sealed class ZstdCompressor {
  private readonly Stream _output;
  private readonly int _compressionLevel;
  private readonly MemoryStream _pendingData;
  private bool _finished;

  /// <summary>
  /// Initializes a new <see cref="ZstdCompressor"/>.
  /// </summary>
  /// <param name="output">The stream to write compressed data to.</param>
  /// <param name="compressionLevel">The compression level (1-9). Default 3.</param>
  public ZstdCompressor(Stream output, int compressionLevel = 3) {
    this._output = output;
    this._compressionLevel = compressionLevel;
    this._pendingData = new MemoryStream();
  }

  /// <summary>
  /// Buffers data for compression. The data is compressed when <see cref="Finish"/> is called.
  /// </summary>
  /// <param name="data">The data to write.</param>
  public void Write(ReadOnlySpan<byte> data) {
    if (data.Length > 0)
      this._pendingData.Write(data);
  }

  /// <summary>
  /// Finishes compression by writing the complete Zstandard frame.
  /// </summary>
  public void Finish() {
    if (this._finished) return;
    this._finished = true;

    var allData = this._pendingData.ToArray();

    // Compute content checksum (XXH64 lower 32 bits)
    var hash = XxHash64.Compute(allData);
    var contentChecksum = (uint)(hash & 0xFFFFFFFF);

    // Write frame header
    var header = new ZstdFrameHeader(
      WindowSize: Math.Max(allData.Length, 1024),
      ContentSize: allData.Length,
      DictionaryId: 0,
      ContentChecksum: true,
      SingleSegment: true);
    header.Write(this._output);

    // Split data into blocks and compress
    if (allData.Length == 0)
      ZstdBlock.WriteBlockHeader(this._output, ZstdConstants.BlockTypeRaw, 0, true);
    else {
      var offset = 0;
      while (offset < allData.Length) {
        var blockSize = Math.Min(ZstdConstants.MaxBlockSize, allData.Length - offset);
        var lastBlock = offset + blockSize >= allData.Length;
        ReadOnlySpan<byte> blockData = allData.AsSpan(offset, blockSize);

        WriteBlock(blockData, lastBlock);
        offset += blockSize;
      }
    }

    // Write content checksum (4 bytes, little-endian)
    Span<byte> checksumBuf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(checksumBuf, contentChecksum);
    this._output.Write(checksumBuf);

    this._output.Flush();
  }

  /// <summary>
  /// Writes a single block, choosing between compressed, RLE, and raw format.
  /// </summary>
  private void WriteBlock(ReadOnlySpan<byte> blockData, bool lastBlock) {
    // Check for RLE block (all bytes the same)
    if (IsAllSameByte(blockData)) {
      ZstdBlock.WriteBlockHeader(this._output, ZstdConstants.BlockTypeRle,
        blockData.Length, lastBlock);
      this._output.WriteByte(blockData[0]);
      return;
    }

    // Try to create a compressed block
    var compressedBlock = TryCompressBlock(blockData);

    if (compressedBlock != null && compressedBlock.Length < blockData.Length) {
      ZstdBlock.WriteBlockHeader(this._output, ZstdConstants.BlockTypeCompressed,
        compressedBlock.Length, lastBlock);
      this._output.Write(compressedBlock);
    }
    else {
      ZstdBlock.WriteBlockHeader(this._output, ZstdConstants.BlockTypeRaw,
        blockData.Length, lastBlock);
      this._output.Write(blockData);
    }
  }

  /// <summary>
  /// Checks whether all bytes in the span are the same value.
  /// </summary>
  private static bool IsAllSameByte(ReadOnlySpan<byte> data) {
    if (data.Length <= 1) return true;
    var first = data[0];
    for (var i = 1; i < data.Length; ++i) {
      if (data[i] != first) return false;
    }

    return true;
  }

  /// <summary>
  /// Attempts to compress a block using LZ matching and sequence encoding.
  /// Returns null if the block cannot be compressed effectively.
  /// </summary>
  private byte[]? TryCompressBlock(ReadOnlySpan<byte> blockData) {
    if (blockData.Length < ZstdConstants.MinMatch)
      return null;

    // Find matches using hash chain
    var maxChainDepth = this._compressionLevel switch {
      <= 1 => 4,
      <= 3 => 16,
      <= 6 => 64,
      _ => 128
    };

    var windowSize = blockData.Length;
    var matchFinder = new HashChainMatchFinder(
      Math.Max(windowSize, 1024), maxChainDepth);

    var sequences = new List<ZstdSequence>();
    var litStart = 0;
    var pos = 0;

    while (pos < blockData.Length) {
      if (pos + ZstdConstants.MinMatch > blockData.Length) {
        ++pos;
        continue;
      }

      var match = matchFinder.FindMatch(blockData, pos, pos, 258, ZstdConstants.MinMatch);

      if (match.Length >= ZstdConstants.MinMatch) {
        var litLength = pos - litStart;
        sequences.Add(new ZstdSequence(litLength, match.Length, match.Distance));

        for (var i = 1; i < match.Length && pos + i + 2 < blockData.Length; ++i)
          matchFinder.InsertPosition(blockData, pos + i);

        pos += match.Length;
        litStart = pos;
      }
      else
        ++pos;
    }

    if (sequences.Count == 0)
      return null;

    // Collect all literal bytes
    var allLiterals = new MemoryStream();
    var litRunStart = 0;
    foreach (var seq in sequences) {
      if (seq.LiteralLength > 0)
        allLiterals.Write(blockData.Slice(litRunStart, seq.LiteralLength));
      litRunStart += seq.LiteralLength + seq.MatchLength;
    }

    var trailingLiterals = blockData.Length - litRunStart;
    if (trailingLiterals > 0)
      allLiterals.Write(blockData.Slice(litRunStart, trailingLiterals));

    var allLiteralBytes = allLiterals.ToArray();

    // Output buffer
    var output = new byte[blockData.Length * 2 + 1024];
    var outputPos = 0;

    // Write literals section (Raw encoding)
    outputPos += ZstdLiterals.CompressLiterals(allLiteralBytes, output, outputPos);

    // Write sequences section
    int[] repeatOffsets = [1, 4, 8];
    outputPos += ZstdSequences.EncodeSequences(sequences.ToArray(), output, outputPos, repeatOffsets);

    var result = new byte[outputPos];
    output.AsSpan(0, outputPos).CopyTo(result);
    return result;
  }
}
