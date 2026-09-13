using System.Buffers;
using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.MatchFinders;
using Compression.Core.Dictionary.Zstd;

namespace FileFormat.Zstd;

/// <summary>
/// Compresses data into Zstandard format and writes to a stream.
/// Buffers all input and writes the complete frame on <see cref="Finish"/>.
/// Uses hash-chain match finding with raw literal blocks and predefined FSE tables.
/// Optionally accepts a <see cref="ZstdDictionary"/> for dictionary-mode compression.
/// </summary>
internal sealed class ZstdCompressor {
  private readonly Stream _output;
  private readonly int _compressionLevel;
  private readonly MemoryStream _pendingData;
  private readonly ZstdDictionary? _dictionary;
  private bool _finished;

  /// <summary>
  /// Initializes a new <see cref="ZstdCompressor"/>.
  /// </summary>
  /// <param name="output">The stream to write compressed data to.</param>
  /// <param name="compressionLevel">The compression level (1-9). Default 3.</param>
  /// <param name="dictionary">Optional Zstd dictionary for prepopulating the match window.</param>
  public ZstdCompressor(Stream output, int compressionLevel = 3, ZstdDictionary? dictionary = null) {
    this._output = output;
    this._compressionLevel = compressionLevel;
    this._dictionary = dictionary;
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

    // Write frame header (include dictionary ID when a dictionary is supplied)
    var dictId = this._dictionary?.DictionaryId ?? 0u;
    var header = new ZstdFrameHeader(
      WindowSize: Math.Max(allData.Length, 1024),
      ContentSize: allData.Length,
      DictionaryId: dictId,
      ContentChecksum: true,
      SingleSegment: dictId == 0);
    header.Write(this._output);

    // Split data into blocks and compress
    if (allData.Length == 0)
      ZstdBlock.WriteBlockHeader(this._output, ZstdConstants.BlockTypeRaw, 0, true);
    else {
      // One match finder spans the WHOLE frame, so a block can reference data in
      // earlier blocks (the frame header already advertises a full-content window).
      // This is what lets long repeats collapse to a single sequence per block
      // instead of repeating the leading literals in every block.
      var maxChainDepth = this._compressionLevel switch { <= 1 => 4, <= 3 => 16, <= 6 => 64, _ => 128 };
      var matchFinder = new HashChainMatchFinder(Math.Max(allData.Length, 1024), maxChainDepth);

      // Repeat-offset history is maintained continuously across the frame's blocks.
      var frameRepeatOffsets = this._dictionary?.RepeatOffsets is { Length: >= 3 } r
        ? new[] { r[0], r[1], r[2] }
        : new[] { 1, 4, 8 };

      var offset = 0;
      while (offset < allData.Length) {
        var blockSize = Math.Min(ZstdConstants.MaxBlockSize, allData.Length - offset);
        var lastBlock = offset + blockSize >= allData.Length;

        WriteBlock(allData, offset, blockSize, lastBlock, matchFinder, frameRepeatOffsets);
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
  private void WriteBlock(ReadOnlySpan<byte> allData, int blockStart, int blockLen, bool lastBlock,
      HashChainMatchFinder matchFinder, int[] frameRepeatOffsets) {
    var blockData = allData.Slice(blockStart, blockLen);
    // Check for RLE block (all bytes the same). Raw/RLE blocks carry no sequences,
    // so they leave the repeat-offset history unchanged (matching the decoder).
    if (IsAllSameByte(blockData)) {
      // Still feed the run into the finder so later blocks can match against it.
      for (var i = 0; i < blockLen; ++i) matchFinder.InsertPosition(allData, blockStart + i);
      ZstdBlock.WriteBlockHeader(this._output, ZstdConstants.BlockTypeRle,
        blockData.Length, lastBlock);
      this._output.WriteByte(blockData[0]);
      return;
    }

    // Try to create a compressed block (matches may reference earlier blocks). The
    // evolved repeat-offset history is returned separately and only committed to the
    // frame state when the compressed block is actually emitted — a raw fallback must
    // leave the history untouched, or the decoder desyncs.
    var compressedBlock = TryCompressBlock(allData, blockStart, blockLen, matchFinder,
      frameRepeatOffsets, out var blockRepeatOffsets);

    if (compressedBlock != null && compressedBlock.Length < blockData.Length) {
      ZstdBlock.WriteBlockHeader(this._output, ZstdConstants.BlockTypeCompressed,
        compressedBlock.Length, lastBlock);
      this._output.Write(compressedBlock);
      Array.Copy(blockRepeatOffsets, frameRepeatOffsets, 3); // commit the evolved history
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
  /// When a dictionary is present, uses dictionary-derived repeat offsets for
  /// better sequence encoding.
  /// </summary>
  private byte[]? TryCompressBlock(ReadOnlySpan<byte> allData, int blockStart, int blockLen,
      HashChainMatchFinder matchFinder, int[] currentRepeatOffsets, out int[] blockRepeatOffsets) {
    // Default: history unchanged (used by every early/raw return).
    blockRepeatOffsets = currentRepeatOffsets;
    if (blockLen < ZstdConstants.MinMatch) {
      for (var i = 0; i < blockLen; ++i) matchFinder.InsertPosition(allData, blockStart + i);
      return null;
    }

    // Zstd encodes match lengths far beyond Deflate's 258 cap (ML codes reach
    // baseline 65536 + 16 extra bits = 131071), so let a single sequence cover a
    // long run instead of fragmenting it into thousands of 258-byte matches.
    const int MaxMatch = ZstdConstants.MaxBlockSize - 1; // 131071, the largest encodable ML
    var blockEnd = blockStart + blockLen;
    var sequences = new List<ZstdSequence>();
    var litStart = blockStart;
    var pos = blockStart;

    while (pos < blockEnd) {
      if (pos + ZstdConstants.MinMatch > blockEnd) {
        matchFinder.InsertPosition(allData, pos);
        ++pos;
        continue;
      }

      // A match's length is bounded by this block (its decoded size is fixed),
      // but its offset may reach back into earlier blocks within the frame window.
      var maxLen = Math.Min(MaxMatch, blockEnd - pos);
      var match = matchFinder.FindMatch(allData, pos, pos, maxLen, ZstdConstants.MinMatch);

      if (match.Length >= ZstdConstants.MinMatch) {
        sequences.Add(new ZstdSequence(pos - litStart, match.Length, match.Distance));
        var end = pos + match.Length;
        for (; pos < end; ++pos)
          matchFinder.InsertPosition(allData, pos);
        litStart = pos;
      }
      else {
        matchFinder.InsertPosition(allData, pos);
        ++pos;
      }
    }

    if (sequences.Count == 0)
      return null;

    // Collect all literal bytes (absolute coordinates within the frame buffer).
    var allLiterals = new MemoryStream();
    var litRunStart = blockStart;
    foreach (var seq in sequences) {
      if (seq.LiteralLength > 0)
        allLiterals.Write(allData.Slice(litRunStart, seq.LiteralLength));
      litRunStart += seq.LiteralLength + seq.MatchLength;
    }

    var trailingLiterals = blockEnd - litRunStart;
    if (trailingLiterals > 0)
      allLiterals.Write(allData.Slice(litRunStart, trailingLiterals));

    var allLiteralBytes = allLiterals.ToArray();

    // Output buffer
    var outputLen = blockLen * 2 + 1024;
    var output = ArrayPool<byte>.Shared.Rent(outputLen);
    try {
      var outputPos = 0;

      // Write literals section (Raw encoding)
      outputPos += ZstdLiterals.CompressLiterals(allLiteralBytes, output, outputPos);

      // Write sequences section. Work on a copy of the frame's repeat-offset history
      // (EncodeSequences evolves it in place); the caller commits it only if this
      // compressed block is actually used.
      var repeatOffsets = new[] { currentRepeatOffsets[0], currentRepeatOffsets[1], currentRepeatOffsets[2] };
      outputPos += ZstdSequences.EncodeSequences(sequences.ToArray(), output, outputPos, repeatOffsets);
      blockRepeatOffsets = repeatOffsets;

      return output.AsSpan(0, outputPos).ToArray();
    } finally {
      ArrayPool<byte>.Shared.Return(output);
    }
  }
}
