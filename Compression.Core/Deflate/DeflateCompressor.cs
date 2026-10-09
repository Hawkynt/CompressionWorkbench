using Compression.Core.BitIO;

namespace Compression.Core.Deflate;

/// <summary>
/// Compresses data in the DEFLATE format (RFC 1951).
/// </summary>
/// <remarks>
/// <see cref="DeflateCompressionLevel.Fast"/>, <see cref="DeflateCompressionLevel.Default"/> and
/// <see cref="DeflateCompressionLevel.Best"/> — and any other value from 1 to 9, read as the
/// zlib level of that number — stream through a sliding-window hash-chain encoder that keeps
/// only a 64 KB window in memory. <see cref="DeflateCompressionLevel.None"/> emits stored
/// blocks; <see cref="DeflateCompressionLevel.Maximum"/> buffers the whole input for the
/// Zopfli-style optimal parse.
/// </remarks>
public sealed class DeflateCompressor {
  private readonly Stream _output;
  private readonly DeflateCompressionLevel _level;
  private readonly BitWriter<LsbBitOrder> _bitWriter;
  private readonly DeflateEncoder? _encoder;

  // Pending input lives in [_bufferStart, _bufferEnd) of _inputBuffer. Emitting a block
  // only advances _bufferStart: the previous List.RemoveRange(0, blockSize) copied every
  // byte still buffered, once per block, which made a single Write() of n bytes cost
  // O(n^2) — 6 s at 64 MB, 20 s at 128 MB, 84 s at 256 MB.
  private byte[] _inputBuffer;
  private int _bufferStart;
  private int _bufferEnd;
  private bool _finished;

  private const int MaxBlockSize = 65535; // max for uncompressed blocks
  private const int DefaultBlockSize = 32768;

  /// <summary>
  /// Initializes a new <see cref="DeflateCompressor"/> for streaming compression.
  /// </summary>
  /// <param name="output">The stream to write compressed data to.</param>
  /// <param name="level">The compression level.</param>
  public DeflateCompressor(Stream output, DeflateCompressionLevel level = DeflateCompressionLevel.Default) {
    this._output = output;
    this._level = level;
    this._bitWriter = new(output);
    this._inputBuffer = [];
    this._bufferStart = 0;
    this._bufferEnd = 0;

    var zlibLevel = level switch {
      DeflateCompressionLevel.None or DeflateCompressionLevel.Maximum => 0,
      DeflateCompressionLevel.Fast => 1,
      DeflateCompressionLevel.Default => 6,
      DeflateCompressionLevel.Best => 9,
      _ => Math.Clamp((int)level, 1, 9),
    };

    if (zlibLevel > 0)
      this._encoder = new(output, zlibLevel);
  }

  /// <summary>
  /// Compresses data in one shot.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="level">The compression level.</param>
  /// <returns>The DEFLATE compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data, DeflateCompressionLevel level = DeflateCompressionLevel.Default) {
    // Half the input holds most compressible data in one go; a MemoryStream growing from empty
    // reallocates and copies the output at every doubling before the final copy out.
    using var ms = new MemoryStream((int)Math.Min(data.Length / 2L + 64, Array.MaxLength));
    var compressor = new DeflateCompressor(ms, level);
    compressor.Write(data);
    compressor.Finish();
    return ms.ToArray();
  }

  /// <summary>
  /// Buffers input data for compression. Emits blocks when the buffer is full.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  public void Write(ReadOnlySpan<byte> data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot write after Finish() has been called.");

    if (this._encoder is not null) {
      this._encoder.Write(data);
      return;
    }

    this.Append(data);

    // Zopfli decides where the block boundaries go by searching for them, so at Maximum
    // level nothing is emitted until the whole input is in hand; cutting it into fixed
    // chunks first would throw that search away.
    if (this._level == DeflateCompressionLevel.Maximum)
      return;

    // Emit blocks when buffer gets large
    while (this.Pending >= DeflateCompressor.DefaultBlockSize * 2) {
      this.EmitBlock(this._inputBuffer.AsSpan(this._bufferStart, DeflateCompressor.DefaultBlockSize), isFinal: false);
      this._bufferStart += DeflateCompressor.DefaultBlockSize;
    }
  }

  /// <summary>
  /// Gets the number of buffered bytes not yet written to a block.
  /// </summary>
  private int Pending => this._bufferEnd - this._bufferStart;

  /// <summary>
  /// Copies data onto the end of the pending region, reclaiming the already-emitted
  /// prefix before growing the backing array.
  /// </summary>
  private void Append(ReadOnlySpan<byte> data) {
    if (data.IsEmpty)
      return;

    if (this._inputBuffer.Length - this._bufferEnd < data.Length) {
      var pending = this.Pending;
      var required = (long)pending + data.Length;

      if (required > this._inputBuffer.Length) {
        var capacity = Math.Max((long)this._inputBuffer.Length, 1024);
        while (capacity < required)
          capacity *= 2;

        var grown = new byte[(int)Math.Min(capacity, Array.MaxLength)];
        this._inputBuffer.AsSpan(this._bufferStart, pending).CopyTo(grown);
        this._inputBuffer = grown;
      } else
        this._inputBuffer.AsSpan(this._bufferStart, pending).CopyTo(this._inputBuffer);

      this._bufferStart = 0;
      this._bufferEnd = pending;
    }

    data.CopyTo(this._inputBuffer.AsSpan(this._bufferEnd));
    this._bufferEnd += data.Length;
  }

  /// <summary>
  /// Writes the final block and flushes all remaining data.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;

    if (this._encoder is not null) {
      this._encoder.Finish();
      return;
    }

    if (this.Pending == 0)
      // Emit empty final block
      this.EmitBlock([], isFinal: true);
    else {
      // Emit remaining data as final block
      while (this._level != DeflateCompressionLevel.Maximum
             && this.Pending > DeflateCompressor.DefaultBlockSize) {
        this.EmitBlock(this._inputBuffer.AsSpan(this._bufferStart, DeflateCompressor.DefaultBlockSize), isFinal: false);
        this._bufferStart += DeflateCompressor.DefaultBlockSize;
      }

      this.EmitBlock(this._inputBuffer.AsSpan(this._bufferStart, this.Pending), isFinal: true);
      this._bufferStart = this._bufferEnd = 0;
    }

    this._bitWriter.FlushBits();
  }

  private void EmitBlock(ReadOnlySpan<byte> data, bool isFinal) {
    switch (this._level) {
      case DeflateCompressionLevel.None: this.EmitUncompressedBlock(data, isFinal); break;
      case DeflateCompressionLevel.Maximum: this.EmitOptimalBlocks(data, isFinal); break;

      default: throw new InvalidOperationException("Hash-chain levels are encoded by DeflateEncoder.");
    }
  }

  private void EmitUncompressedBlock(ReadOnlySpan<byte> data, bool isFinal) {
    // Uncompressed blocks have max 65535 bytes
    var offset = 0;
    while (offset < data.Length) {
      var chunkSize = Math.Min(data.Length - offset, DeflateCompressor.MaxBlockSize);
      var isLastChunk = (offset + chunkSize >= data.Length) && isFinal;

      this._bitWriter.WriteBits(isLastChunk ? 1u : 0u, 1); // BFINAL
      this._bitWriter.WriteBits(0, 2); // BTYPE=00
      this._bitWriter.FlushBits(); // Align to byte

      var len = (ushort)chunkSize;
      var nlen = (ushort)(~len);
      this._bitWriter.WriteBits(len, 16);
      this._bitWriter.WriteBits(nlen, 16);

      for (var i = 0; i < chunkSize; ++i)
        this._bitWriter.WriteBits(data[offset + i], 8);

      offset += chunkSize;
    }

    // Handle empty data case
    if (data.Length != 0 || !isFinal)
      return;

    this._bitWriter.WriteBits(1, 1); // BFINAL
    this._bitWriter.WriteBits(0, 2); // BTYPE=00
    this._bitWriter.FlushBits();
    this._bitWriter.WriteBits(0, 16); // LEN=0
    this._bitWriter.WriteBits(0xFFFF, 16); // NLEN=0xFFFF
  }

  /// <summary>
  /// Derives length-limited Huffman code lengths for a block's alphabet.
  /// </summary>
  /// <remarks>
  /// Zopfli measures each candidate parse by the exact size of the block it produces, so the
  /// lengths it costs with and the lengths it emits have to come from one builder. Every level
  /// now uses that builder, whose tie-break among equally likely symbols is written down rather
  /// than inherited from a heap's internals.
  /// </remarks>
  private int[] BuildCodeLengths(long[] frequencies, int alphabetSize, int maxBits)
    => ZopfliBlockCost.BuildCodeLengths(frequencies.AsSpan(0, alphabetSize), maxBits);

  private void EmitStaticHuffmanBlock(
    List<(bool IsLiteral, byte Literal, int Distance, int Length)> tokens,
    bool isFinal) {
    var litLenTable = DeflateHuffmanTable.CreateStaticLiteralTable();
    var distTable = DeflateHuffmanTable.CreateStaticDistanceTable();

    this._bitWriter.WriteBits(isFinal ? 1u : 0u, 1); // BFINAL
    this._bitWriter.WriteBits(DeflateConstants.BlockTypeStaticHuffman, 2); // BTYPE=01

    this.WriteTokens(tokens, litLenTable, distTable);

    // Write EOB
    var (eobCode, eobLen) = litLenTable.GetCode(DeflateConstants.EndOfBlock);
    this._bitWriter.WriteBits(eobCode, eobLen);
  }

  private void EmitDynamicHuffmanBlock(
    long[] litLenFreqs,
    long[] distFreqs,
    List<(bool IsLiteral, byte Literal, int Distance, int Length)> tokens,
    bool isFinal) {
    // Build Huffman trees and get code lengths. At Maximum level the trees are the ones
    // the block's measured cost was based on, which may be the run-friendly variant, and
    // which already invents the distance code a block without back-references needs.
    int[] litLenLengths, distLengths;
    if (this._level == DeflateCompressionLevel.Maximum) {
      var chosen = ZopfliBlockCost.BuildDynamicBlock(litLenFreqs, distFreqs);
      litLenLengths = chosen.LitLenLengths;
      distLengths = chosen.DistLengths;
    } else {
      // Need at least one distance code for a valid table
      ZopfliBlockCost.EnsureDistanceCode(distFreqs);
      litLenLengths = this.BuildCodeLengths(litLenFreqs, DeflateConstants.LiteralLengthAlphabetSize, DeflateConstants.MaxBits);
      distLengths = this.BuildCodeLengths(distFreqs, DeflateConstants.DistanceAlphabetSize, DeflateConstants.MaxBits);
    }

    // Determine HLIT and HDIST (trim trailing zeros)
    var (hlit, hdist) = ZopfliBlockCost.TrimTrees(litLenLengths, distLengths);

    // RLE encode combined code lengths
    var combinedLengths = new int[hlit + hdist];
    litLenLengths.AsSpan(0, hlit).CopyTo(combinedLengths);
    distLengths.AsSpan(0, hdist).CopyTo(combinedLengths.AsSpan(hlit));

    var rleSymbols = DeflateCodeLengthRuns.Encode(combinedLengths);

    // Build code-length Huffman table
    var clFreqs = new long[DeflateConstants.CodeLengthAlphabetSize];
    foreach (var run in rleSymbols)
      ++clFreqs[run.Symbol];

    // Ensure at least one non-zero frequency
    var hasClCodes = clFreqs.Any(t => t > 0);

    if (!hasClCodes)
      clFreqs[0] = 1;

    var clLengths = this.BuildCodeLengths(clFreqs, DeflateConstants.CodeLengthAlphabetSize, DeflateConstants.MaxCodeLengthBits);

    // Determine HCLEN (trim trailing zeros in permuted order)
    var hclen = DeflateConstants.CodeLengthAlphabetSize;
    while (hclen > 4 && clLengths[DeflateConstants.CodeLengthOrder[hclen - 1]] == 0)
      --hclen;

    var clTable = new DeflateHuffmanTable(clLengths);

    // Write block header
    this._bitWriter.WriteBits(isFinal ? 1u : 0u, 1); // BFINAL
    this._bitWriter.WriteBits(DeflateConstants.BlockTypeDynamicHuffman, 2); // BTYPE=10

    this._bitWriter.WriteBits((uint)(hlit - 257), 5); // HLIT
    this._bitWriter.WriteBits((uint)(hdist - 1), 5); // HDIST
    this._bitWriter.WriteBits((uint)(hclen - 4), 4); // HCLEN

    // Write code-length code lengths in permuted order
    for (var i = 0; i < hclen; ++i)
      this._bitWriter.WriteBits((uint)clLengths[DeflateConstants.CodeLengthOrder[i]], 3);

    // Write RLE-encoded code lengths
    foreach (var (symbol, extraBits, extraValue) in rleSymbols) {
      var (code, len) = clTable.GetCode(symbol);
      this._bitWriter.WriteBits(code, len);
      if (extraBits > 0)
        this._bitWriter.WriteBits((uint)extraValue, extraBits);
    }

    // Build final tables and write tokens
    var litLenTable = new DeflateHuffmanTable(litLenLengths[..hlit]);
    var distTable = new DeflateHuffmanTable(distLengths[..hdist]);

    this.WriteTokens(tokens, litLenTable, distTable);

    // Write EOB
    var (eobCode, eobLen) = litLenTable.GetCode(DeflateConstants.EndOfBlock);
    this._bitWriter.WriteBits(eobCode, eobLen);
  }

  private void WriteTokens(
    List<(bool IsLiteral, byte Literal, int Distance, int Length)> tokens,
    DeflateHuffmanTable litLenTable,
    DeflateHuffmanTable distTable) {
    foreach (var (isLiteral, literal, distance, length) in tokens)
      if (isLiteral) {
        var (code, len) = litLenTable.GetCode(literal);
        this._bitWriter.WriteBits(code, len);
      } else {
        // Length code
        var lenCode = DeflateConstants.GetLengthCode(length);
        var (lCode, lLen) = litLenTable.GetCode(lenCode);
        this._bitWriter.WriteBits(lCode, lLen);

        // Length extra bits
        var lenIdx = lenCode - 257;
        var lenExtra = DeflateConstants.LengthExtraBits[lenIdx];
        if (lenExtra > 0) {
          var lenExtraValue = length - DeflateConstants.LengthBase[lenIdx];
          this._bitWriter.WriteBits((uint)lenExtraValue, lenExtra);
        }

        // Distance code
        var distCode = DeflateConstants.GetDistanceCode(distance);
        var (dCode, dLen) = distTable.GetCode(distCode);
        this._bitWriter.WriteBits(dCode, dLen);

        // Distance extra bits
        var distExtra = DeflateConstants.DistanceExtraBits[distCode];
        if (distExtra <= 0)
          continue;

        var distExtraValue = distance - DeflateConstants.DistanceBase[distCode];
        this._bitWriter.WriteBits((uint)distExtraValue, distExtra);
      }
  }

  private void EmitOptimalBlocks(ReadOnlySpan<byte> data, bool isFinal) {
    var dataArray = data.ToArray();
    var blocks = ZopfliDeflate.CompressOptimal(dataArray);

    for (var i = 0; i < blocks.Count; ++i) {
      var (start, end, symbols) = blocks[i];
      var isLastBlock = isFinal && (i == blocks.Count - 1);

      // Convert LzSymbol[] to token format
      var tokens = new List<(bool IsLiteral, byte Literal, int Distance, int Length)>();
      var litLenFreqs = new long[DeflateConstants.LiteralLengthAlphabetSize];
      var distFreqs = new long[DeflateConstants.DistanceAlphabetSize];

      foreach (var sym in symbols)
        if (sym.IsLiteral) {
          tokens.Add((true, (byte)sym.LitLen, 0, 0));
          ++litLenFreqs[sym.LitLen];
        }
        else {
          tokens.Add((false, 0, sym.Distance, sym.LitLen));
          var lenCode = DeflateConstants.GetLengthCode(sym.LitLen);
          ++litLenFreqs[lenCode];
          var distCode = DeflateConstants.GetDistanceCode(sym.Distance);
          ++distFreqs[distCode];
        }

      litLenFreqs[DeflateConstants.EndOfBlock] = 1;

      // Data that will not compress must still be handed on unharmed: without the stored
      // block type an incompressible block grows by roughly a byte per hundred instead of
      // by five bytes per 64 KB.
      var (blockType, _) = ZopfliBlockCost.Cheapest(litLenFreqs, distFreqs, end - start);
      switch (blockType) {
        case DeflateConstants.BlockTypeUncompressed:
          this.EmitUncompressedBlock(data.Slice(start, end - start), isLastBlock);
          break;
        case DeflateConstants.BlockTypeStaticHuffman:
          this.EmitStaticHuffmanBlock(tokens, isLastBlock);
          break;
        default:
          this.EmitDynamicHuffmanBlock(litLenFreqs, distFreqs, tokens, isLastBlock);
          break;
      }
    }
  }
}
