using Compression.Core.BitIO;
using Compression.Core.Dictionary.MatchFinders;
using Compression.Core.Entropy.Huffman;

namespace Compression.Core.Deflate;

/// <summary>
/// Compresses data in the DEFLATE format (RFC 1951).
/// </summary>
public sealed class DeflateCompressor {
  private readonly Stream _output;
  private readonly DeflateCompressionLevel _level;
  private readonly BitWriter<LsbBitOrder> _bitWriter;
  private readonly List<byte> _inputBuffer;
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
  }

  /// <summary>
  /// Compresses data in one shot.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="level">The compression level.</param>
  /// <returns>The DEFLATE compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data, DeflateCompressionLevel level = DeflateCompressionLevel.Default) {
    using var ms = new MemoryStream();
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

    foreach (var value in data)
      this._inputBuffer.Add(value);

    // Zopfli decides where the block boundaries go by searching for them, so at Maximum
    // level nothing is emitted until the whole input is in hand; cutting it into fixed
    // chunks first would throw that search away.
    if (this._level == DeflateCompressionLevel.Maximum)
      return;

    // Emit blocks when buffer gets large
    while (this._inputBuffer.Count >= DeflateCompressor.DefaultBlockSize * 2) {
      this.EmitBlock(this._inputBuffer.GetRange(0, DeflateCompressor.DefaultBlockSize), isFinal: false);
      this._inputBuffer.RemoveRange(0, DeflateCompressor.DefaultBlockSize);
    }
  }

  /// <summary>
  /// Writes the final block and flushes all remaining data.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;

    if (this._inputBuffer.Count == 0)
      // Emit empty final block
      this.EmitBlock([], isFinal: true);
    else {
      // Emit remaining data as final block
      while (this._level != DeflateCompressionLevel.Maximum
             && this._inputBuffer.Count > DeflateCompressor.DefaultBlockSize) {
        this.EmitBlock(this._inputBuffer.GetRange(0, DeflateCompressor.DefaultBlockSize), isFinal: false);
        this._inputBuffer.RemoveRange(0, DeflateCompressor.DefaultBlockSize);
      }

      this.EmitBlock(this._inputBuffer, isFinal: true);
      this._inputBuffer.Clear();
    }

    this._bitWriter.FlushBits();
  }

  private void EmitBlock(List<byte> data, bool isFinal) {
    switch (this._level) {
      case DeflateCompressionLevel.None: this.EmitUncompressedBlock(data, isFinal); break;
      case DeflateCompressionLevel.Maximum: this.EmitOptimalBlocks(data, isFinal); break;

      case DeflateCompressionLevel.Fast:
      case DeflateCompressionLevel.Default:
      case DeflateCompressionLevel.Best:
      default: this.EmitCompressedBlock(data, isFinal); 
        break;
    }
  }

  private void EmitUncompressedBlock(List<byte> data, bool isFinal) {
    // Uncompressed blocks have max 65535 bytes
    var offset = 0;
    while (offset < data.Count) {
      var chunkSize = Math.Min(data.Count - offset, DeflateCompressor.MaxBlockSize);
      var isLastChunk = (offset + chunkSize >= data.Count) && isFinal;

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
    if (data.Count != 0 || !isFinal)
      return;

    this._bitWriter.WriteBits(1, 1); // BFINAL
    this._bitWriter.WriteBits(0, 2); // BTYPE=00
    this._bitWriter.FlushBits();
    this._bitWriter.WriteBits(0, 16); // LEN=0
    this._bitWriter.WriteBits(0xFFFF, 16); // NLEN=0xFFFF
  }

  private void EmitCompressedBlock(List<byte> data, bool isFinal) {
    byte[] dataArray = [.. data];

    // Run LZ77 to find matches
    var tokens = this.FindMatches(dataArray);

    // Collect symbol frequencies
    var litLenFreqs = new long[DeflateConstants.LiteralLengthAlphabetSize];
    var distFreqs = new long[DeflateConstants.DistanceAlphabetSize];

    foreach (var (isLiteral, literal, distance, length) in tokens)
      if (isLiteral)
        ++litLenFreqs[literal];
      else {
        var lenCode = DeflateConstants.GetLengthCode(length);
        ++litLenFreqs[lenCode];
        var distCode = DeflateConstants.GetDistanceCode(distance);
        ++distFreqs[distCode];
      }

    litLenFreqs[DeflateConstants.EndOfBlock] = 1; // EOB

    // Estimate uncompressed block cost: 3 header bits + 5-byte per sub-block header + raw bytes.
    // Taken in 64-bit. A 32-bit product would wrap for a 2^28-byte block, and the
    // wrapped negative estimate would make an uncompressed block look cheaper than
    // any Huffman-coded one. Write and Finish never hand this method more than
    // DefaultBlockSize (32768) bytes — the Maximum level uses the larger block but
    // routes to EmitOptimalBlocks instead — so the wrap is not reachable today; the
    // width keeps it that way if the block size changes.
    var numSubBlocks = Math.Max(1, (dataArray.Length + DeflateCompressor.MaxBlockSize - 1) / DeflateCompressor.MaxBlockSize);
    var uncompressedBits = 3L + (long)numSubBlocks * 5 * 8 + (long)dataArray.Length * 8;

    if (this._level == DeflateCompressionLevel.Fast) {
      // Compare static Huffman vs uncompressed
      var staticSize = EstimateStaticSize(tokens);
      if (uncompressedBits < staticSize)
        this.EmitUncompressedBlock(data, isFinal);
      else
        this.EmitStaticHuffmanBlock(tokens, isFinal);
    }
    else {
      // Try static, dynamic, and uncompressed — pick smallest
      var staticSize = EstimateStaticSize(tokens);
      var dynamicSize = EstimateDynamicSize(litLenFreqs, distFreqs, tokens);
      var bestCompressed = Math.Min(staticSize, dynamicSize);

      if (uncompressedBits < bestCompressed)
        this.EmitUncompressedBlock(data, isFinal);
      else if (staticSize <= dynamicSize)
        this.EmitStaticHuffmanBlock(tokens, isFinal);
      else
        this.EmitDynamicHuffmanBlock(litLenFreqs, distFreqs, tokens, isFinal);
    }
  }

  private List<(bool IsLiteral, byte Literal, int Distance, int Length)> FindMatches(byte[] data) {
    var result = new List<(bool, byte, int, int)>();
    if (data.Length == 0)
      return result;

    var chainDepth = this._level switch {
      DeflateCompressionLevel.Fast => 4,
      DeflateCompressionLevel.Best => 4096,
      _ => 128
    };

    var matcher = new HashChainMatchFinder(DeflateConstants.WindowSize, chainDepth);
    var pos = 0;

    while (pos < data.Length) {
      var match = matcher.FindMatch(data, pos, DeflateConstants.WindowSize, 258, 3);

      if (this._level == DeflateCompressionLevel.Best && match.Length > 0 && pos + 1 < data.Length) {
        // Lazy matching: check if position+1 has a better match
        var nextMatch = matcher.FindMatch(data, pos + 1, DeflateConstants.WindowSize, 258, 3);
        if (nextMatch.Length > match.Length + 1) {
          // Emit current byte as literal, use next match
          result.Add((true, data[pos], 0, 0));
          ++pos;
          match = nextMatch;
        }
      }

      if (match.Length >= 3) {
        result.Add((false, 0, match.Distance, match.Length));
        // Insert skipped positions into hash chain
        for (var i = 1; i < match.Length; ++i)
          if (pos + i < data.Length)
            matcher.InsertPosition(data, pos + i);

        pos += match.Length;
      }
      else {
        result.Add((true, data[pos], 0, 0));
        ++pos;
      }
    }

    return result;
  }

  /// <summary>
  /// Derives length-limited Huffman code lengths for a block's alphabet.
  /// </summary>
  /// <remarks>
  /// Zopfli measures each candidate parse by the exact size of the block it produces, so
  /// the lengths it costs with and the lengths it emits have to come from one builder, and
  /// that builder is the deterministic one whose tie-break among equally likely symbols is
  /// written down rather than inherited from a heap's internals. The other levels keep the
  /// builder their output has always been pinned against.
  /// </remarks>
  private int[] BuildCodeLengths(long[] frequencies, int alphabetSize, int maxBits) {
    if (this._level == DeflateCompressionLevel.Maximum)
      return ZopfliBlockCost.BuildCodeLengths(frequencies.AsSpan(0, alphabetSize), maxBits);

    var root = HuffmanTree.BuildFromFrequencies(frequencies);
    var lengths = HuffmanTree.GetCodeLengths(root, alphabetSize);
    HuffmanTree.LimitCodeLengths(lengths, maxBits);
    return lengths;
  }

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

  private static int EstimateStaticSize(
    List<(bool IsLiteral, byte Literal, int Distance, int Length)> tokens) {
    var bits = 3; // block header
    var staticLitLenLengths = DeflateConstants.GetStaticLiteralLengths();
    var staticDistLengths = DeflateConstants.GetStaticDistanceLengths();

    foreach (var (isLiteral, literal, distance, length) in tokens)
      if (isLiteral)
        bits += staticLitLenLengths[literal];
      else {
        var lenCode = DeflateConstants.GetLengthCode(length);
        bits += staticLitLenLengths[lenCode];
        bits += DeflateConstants.LengthExtraBits[lenCode - 257];
        var distCode = DeflateConstants.GetDistanceCode(distance);
        bits += staticDistLengths[distCode];
        bits += DeflateConstants.DistanceExtraBits[distCode];
      }

    bits += staticLitLenLengths[DeflateConstants.EndOfBlock]; // EOB
    return bits;
  }

  private static int EstimateDynamicSize(
    long[] litLenFreqs,
    long[] distFreqs,
    List<(bool IsLiteral, byte Literal, int Distance, int Length)> tokens) {

    // Build Huffman trees to get code lengths
    var litLenRoot = HuffmanTree.BuildFromFrequencies(litLenFreqs);
    var litLenLengths = HuffmanTree.GetCodeLengths(litLenRoot, DeflateConstants.LiteralLengthAlphabetSize);
    HuffmanTree.LimitCodeLengths(litLenLengths, DeflateConstants.MaxBits);

    // Need at least one distance code
    var hasDistCodes = distFreqs.Any(t => t > 0);

    var adjustedDistFreqs = (long[])distFreqs.Clone();
    if (!hasDistCodes)
      adjustedDistFreqs[0] = 1;

    var distRoot = HuffmanTree.BuildFromFrequencies(adjustedDistFreqs);
    var distLengths = HuffmanTree.GetCodeLengths(distRoot, DeflateConstants.DistanceAlphabetSize);
    HuffmanTree.LimitCodeLengths(distLengths, DeflateConstants.MaxBits);

    var bits = 3 + 5 + 5 + 4; // block header + HLIT + HDIST + HCLEN

    // Estimate code-length table overhead
    var hlit = litLenLengths.Length;
    while (hlit > 257 && litLenLengths[hlit - 1] == 0)
      --hlit;

    var hdist = distLengths.Length;
    while (hdist > 1 && distLengths[hdist - 1] == 0)
      --hdist;

    var combinedLengths = new int[hlit + hdist];
    litLenLengths.AsSpan(0, hlit).CopyTo(combinedLengths);
    distLengths.AsSpan(0, hdist).CopyTo(combinedLengths.AsSpan(hlit));

    var rle = RunLengthEncode(combinedLengths);

    var clFreqs = new long[DeflateConstants.CodeLengthAlphabetSize];
    foreach (var (sym, _, _) in rle)
      ++clFreqs[sym];

    var hasCl = clFreqs.Any(t => t > 0);
    if (!hasCl) 
      clFreqs[0] = 1;

    var clRoot = HuffmanTree.BuildFromFrequencies(clFreqs);
    var clLengths = HuffmanTree.GetCodeLengths(clRoot, DeflateConstants.CodeLengthAlphabetSize);
    HuffmanTree.LimitCodeLengths(clLengths, DeflateConstants.MaxCodeLengthBits);

    var hclen = DeflateConstants.CodeLengthAlphabetSize;
    while (hclen > 4 && clLengths[DeflateConstants.CodeLengthOrder[hclen - 1]] == 0)
      --hclen;

    bits += hclen * 3; // code-length code lengths

    foreach (var (sym, extraBits, _) in rle)
      bits += clLengths[sym] + extraBits;

    // Token bits
    foreach (var (isLiteral, literal, distance, length) in tokens)
      if (isLiteral)
        bits += litLenLengths[literal];
      else {
        var lenCode = DeflateConstants.GetLengthCode(length);
        bits += litLenLengths[lenCode];
        bits += DeflateConstants.LengthExtraBits[lenCode - 257];
        var distCode = DeflateConstants.GetDistanceCode(distance);
        bits += distLengths[distCode];
        bits += DeflateConstants.DistanceExtraBits[distCode];
      }

    bits += litLenLengths[DeflateConstants.EndOfBlock]; // EOB
    return bits;
  }

  private void EmitOptimalBlocks(List<byte> data, bool isFinal) {
    byte[] dataArray = [.. data];
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
          this.EmitUncompressedBlock(data.GetRange(start, end - start), isLastBlock);
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

  private static List<(int Symbol, int ExtraBits, int ExtraValue)> RunLengthEncode(int[] lengths) {
    var result = new List<(int, int, int)>();
    foreach (var (symbol, extraBits, extraValue) in DeflateCodeLengthRuns.Encode(lengths))
      result.Add((symbol, extraBits, extraValue));

    return result;
  }
}
