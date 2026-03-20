using Compression.Core.BitIO;
using Compression.Core.Dictionary.MatchFinders;
using Compression.Core.Entropy.Huffman;

namespace Compression.Core.Deflate;

/// <summary>
/// Compresses data in the Deflate64 (Enhanced Deflate) format.
/// Deflate64 extends DEFLATE with a 64 KB sliding window, distance codes 30-31
/// (up to 65536), and length code 285 representing lengths 3–65538 via 16 extra bits.
/// </summary>
public sealed class Deflate64Compressor {
  private readonly Stream _output;
  private readonly DeflateCompressionLevel _level;
  private readonly BitWriter<LsbBitOrder> _bitWriter;
  private readonly List<byte> _inputBuffer;
  private bool _finished;

  private const int MaxUncompressedBlockSize = 65535;
  private const int DefaultBlockSize = 32768;

  /// <summary>
  /// Initializes a new <see cref="Deflate64Compressor"/> for streaming compression.
  /// </summary>
  /// <param name="output">The stream to write compressed data to.</param>
  /// <param name="level">The compression level.</param>
  public Deflate64Compressor(Stream output, DeflateCompressionLevel level = DeflateCompressionLevel.Default) {
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
  /// <returns>The Deflate64 compressed data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data, DeflateCompressionLevel level = DeflateCompressionLevel.Default) {
    using var ms = new MemoryStream();
    var compressor = new Deflate64Compressor(ms, level);
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

    while (this._inputBuffer.Count >= Deflate64Compressor.DefaultBlockSize * 2) {
      this.EmitBlock(this._inputBuffer.GetRange(0, Deflate64Compressor.DefaultBlockSize), isFinal: false);
      this._inputBuffer.RemoveRange(0, Deflate64Compressor.DefaultBlockSize);
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
      this.EmitBlock([], isFinal: true);
    else {
      while (this._inputBuffer.Count > Deflate64Compressor.DefaultBlockSize) {
        this.EmitBlock(this._inputBuffer.GetRange(0, Deflate64Compressor.DefaultBlockSize), isFinal: false);
        this._inputBuffer.RemoveRange(0, Deflate64Compressor.DefaultBlockSize);
      }

      this.EmitBlock(this._inputBuffer, isFinal: true);
      this._inputBuffer.Clear();
    }

    this._bitWriter.FlushBits();
  }

  private void EmitBlock(List<byte> data, bool isFinal) {
    if (this._level == DeflateCompressionLevel.None)
      this.EmitUncompressedBlock(data, isFinal);
    else
      this.EmitCompressedBlock(data, isFinal);
  }

  private void EmitUncompressedBlock(List<byte> data, bool isFinal) {
    var offset = 0;
    while (offset < data.Count) {
      var chunkSize = Math.Min(data.Count - offset, Deflate64Compressor.MaxUncompressedBlockSize);
      var isLastChunk = (offset + chunkSize >= data.Count) && isFinal;

      this._bitWriter.WriteBits(isLastChunk ? 1u : 0u, 1);
      this._bitWriter.WriteBits(0, 2);
      this._bitWriter.FlushBits();

      var len = (ushort)chunkSize;
      var nlen = (ushort)(~len);
      this._bitWriter.WriteBits(len, 16);
      this._bitWriter.WriteBits(nlen, 16);

      for (var i = 0; i < chunkSize; ++i)
        this._bitWriter.WriteBits(data[offset + i], 8);

      offset += chunkSize;
    }

    if (data.Count != 0 || !isFinal)
      return;

    this._bitWriter.WriteBits(1, 1);
    this._bitWriter.WriteBits(0, 2);
    this._bitWriter.FlushBits();
    this._bitWriter.WriteBits(0, 16);
    this._bitWriter.WriteBits(0xFFFF, 16);
  }

  private void EmitCompressedBlock(List<byte> data, bool isFinal) {
    byte[] dataArray = [.. data];
    var tokens = this.FindMatches(dataArray);

    var litLenFreqs = new long[Deflate64Constants.LiteralLengthAlphabetSize];
    var distFreqs = new long[Deflate64Constants.DistanceAlphabetSize];

    foreach (var (isLiteral, literal, distance, length) in tokens)
      if (isLiteral)
        ++litLenFreqs[literal];
      else {
        var lenCode = Deflate64Constants.GetLengthCode(length);
        ++litLenFreqs[lenCode];
        var distCode = Deflate64Constants.GetDistanceCode(distance);
        ++distFreqs[distCode];
      }

    litLenFreqs[DeflateConstants.EndOfBlock] = 1;

    // Estimate uncompressed cost
    var numSubBlocks = Math.Max(1, (dataArray.Length + Deflate64Compressor.MaxUncompressedBlockSize - 1) / Deflate64Compressor.MaxUncompressedBlockSize);
    var uncompressedBits = 3 + numSubBlocks * 5 * 8 + dataArray.Length * 8;

    var dynamicSize = EstimateDynamicSize(litLenFreqs, distFreqs, tokens);

    if (uncompressedBits < dynamicSize)
      this.EmitUncompressedBlock(data, isFinal);
    else
      this.EmitDynamicHuffmanBlock(litLenFreqs, distFreqs, tokens, isFinal);
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

    // Deflate64 uses 64 KB window but match lengths up to 65538.
    // In practice, per-block we cap at the block size.
    var maxMatchLen = Math.Min(Deflate64Constants.MaxMatchLength, data.Length);
    var matcher = new HashChainMatchFinder(Deflate64Constants.WindowSize, chainDepth);
    var pos = 0;

    while (pos < data.Length) {
      var maxLen = Math.Min(maxMatchLen, data.Length - pos);
      var match = matcher.FindMatch(data, pos, Deflate64Constants.WindowSize, maxLen, 3);

      if (this._level == DeflateCompressionLevel.Best && match.Length > 0 && pos + 1 < data.Length) {
        var nextMaxLen = Math.Min(maxMatchLen, data.Length - pos - 1);
        var nextMatch = matcher.FindMatch(data, pos + 1, Deflate64Constants.WindowSize, nextMaxLen, 3);
        if (nextMatch.Length > match.Length + 1) {
          result.Add((true, data[pos], 0, 0));
          ++pos;
          match = matcher.FindMatch(data, pos, Deflate64Constants.WindowSize,
            Math.Min(maxMatchLen, data.Length - pos), 3);
        }
      }

      if (match.Length >= 3) {
        result.Add((false, 0, match.Distance, match.Length));
        for (var i = 1; i < match.Length; ++i)
          if (pos + i < data.Length)
            matcher.InsertPosition(data, pos + i);

        pos += match.Length;
      } else {
        result.Add((true, data[pos], 0, 0));
        ++pos;
      }
    }

    return result;
  }

  private void EmitDynamicHuffmanBlock(
    long[] litLenFreqs,
    long[] distFreqs,
    List<(bool IsLiteral, byte Literal, int Distance, int Length)> tokens,
    bool isFinal) {
    var hasDistCodes = distFreqs.Any(t => t > 0);
    if (!hasDistCodes)
      distFreqs[0] = 1;

    var litLenRoot = HuffmanTree.BuildFromFrequencies(litLenFreqs);
    var litLenLengths = HuffmanTree.GetCodeLengths(litLenRoot, Deflate64Constants.LiteralLengthAlphabetSize);
    HuffmanTree.LimitCodeLengths(litLenLengths, DeflateConstants.MaxBits);

    var distRoot = HuffmanTree.BuildFromFrequencies(distFreqs);
    var distLengths = HuffmanTree.GetCodeLengths(distRoot, Deflate64Constants.DistanceAlphabetSize);
    HuffmanTree.LimitCodeLengths(distLengths, DeflateConstants.MaxBits);

    var hlit = litLenLengths.Length;
    while (hlit > 257 && litLenLengths[hlit - 1] == 0)
      --hlit;

    var hdist = distLengths.Length;
    while (hdist > 1 && distLengths[hdist - 1] == 0)
      --hdist;

    var combinedLengths = new int[hlit + hdist];
    litLenLengths.AsSpan(0, hlit).CopyTo(combinedLengths);
    distLengths.AsSpan(0, hdist).CopyTo(combinedLengths.AsSpan(hlit));

    var rleSymbols = RunLengthEncode(combinedLengths);

    var clFreqs = new long[DeflateConstants.CodeLengthAlphabetSize];
    foreach (var (sym, _, _) in rleSymbols)
      ++clFreqs[sym];

    if (!clFreqs.Any(t => t > 0))
      clFreqs[0] = 1;

    var clRoot = HuffmanTree.BuildFromFrequencies(clFreqs);
    var clLengths = HuffmanTree.GetCodeLengths(clRoot, DeflateConstants.CodeLengthAlphabetSize);
    HuffmanTree.LimitCodeLengths(clLengths, DeflateConstants.MaxCodeLengthBits);

    var hclen = DeflateConstants.CodeLengthAlphabetSize;
    while (hclen > 4 && clLengths[DeflateConstants.CodeLengthOrder[hclen - 1]] == 0)
      --hclen;

    var clTable = new DeflateHuffmanTable(clLengths);

    this._bitWriter.WriteBits(isFinal ? 1u : 0u, 1);
    this._bitWriter.WriteBits(DeflateConstants.BlockTypeDynamicHuffman, 2);

    this._bitWriter.WriteBits((uint)(hlit - 257), 5);
    this._bitWriter.WriteBits((uint)(hdist - 1), 5);
    this._bitWriter.WriteBits((uint)(hclen - 4), 4);

    for (var i = 0; i < hclen; ++i)
      this._bitWriter.WriteBits((uint)clLengths[DeflateConstants.CodeLengthOrder[i]], 3);

    foreach (var (sym, extraBits, extraValue) in rleSymbols) {
      var (code, len) = clTable.GetCode(sym);
      this._bitWriter.WriteBits(code, len);
      if (extraBits > 0)
        this._bitWriter.WriteBits((uint)extraValue, extraBits);
    }

    var litLenTable = new DeflateHuffmanTable(litLenLengths[..hlit]);
    var distTable = new DeflateHuffmanTable(distLengths[..hdist]);

    this.WriteTokens(tokens, litLenTable, distTable);

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
        var lenCode = Deflate64Constants.GetLengthCode(length);
        var (lCode, lLen) = litLenTable.GetCode(lenCode);
        this._bitWriter.WriteBits(lCode, lLen);

        var lenIdx = lenCode - 257;
        var lenExtra = Deflate64Constants.LengthExtraBits[lenIdx];
        if (lenExtra > 0) {
          var lenExtraValue = length - Deflate64Constants.LengthBase[lenIdx];
          this._bitWriter.WriteBits((uint)lenExtraValue, lenExtra);
        }

        var distCode = Deflate64Constants.GetDistanceCode(distance);
        var (dCode, dLen) = distTable.GetCode(distCode);
        this._bitWriter.WriteBits(dCode, dLen);

        var distExtra = Deflate64Constants.DistanceExtraBits[distCode];
        if (distExtra <= 0)
          continue;

        var distExtraValue = distance - Deflate64Constants.DistanceBase[distCode];
        this._bitWriter.WriteBits((uint)distExtraValue, distExtra);
      }
  }

  private static int EstimateDynamicSize(
    long[] litLenFreqs,
    long[] distFreqs,
    List<(bool IsLiteral, byte Literal, int Distance, int Length)> tokens) {
    var litLenRoot = HuffmanTree.BuildFromFrequencies(litLenFreqs);
    var litLenLengths = HuffmanTree.GetCodeLengths(litLenRoot, Deflate64Constants.LiteralLengthAlphabetSize);
    HuffmanTree.LimitCodeLengths(litLenLengths, DeflateConstants.MaxBits);

    var adjustedDistFreqs = (long[])distFreqs.Clone();
    if (!adjustedDistFreqs.Any(t => t > 0))
      adjustedDistFreqs[0] = 1;

    var distRoot = HuffmanTree.BuildFromFrequencies(adjustedDistFreqs);
    var distLengths = HuffmanTree.GetCodeLengths(distRoot, Deflate64Constants.DistanceAlphabetSize);
    HuffmanTree.LimitCodeLengths(distLengths, DeflateConstants.MaxBits);

    var bits = 3 + 5 + 5 + 4;

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

    if (!clFreqs.Any(t => t > 0))
      clFreqs[0] = 1;

    var clRoot = HuffmanTree.BuildFromFrequencies(clFreqs);
    var clLengths = HuffmanTree.GetCodeLengths(clRoot, DeflateConstants.CodeLengthAlphabetSize);
    HuffmanTree.LimitCodeLengths(clLengths, DeflateConstants.MaxCodeLengthBits);

    var hclen = DeflateConstants.CodeLengthAlphabetSize;
    while (hclen > 4 && clLengths[DeflateConstants.CodeLengthOrder[hclen - 1]] == 0)
      --hclen;

    bits += hclen * 3;

    foreach (var (sym, extraBits, _) in rle)
      bits += clLengths[sym] + extraBits;

    foreach (var (isLiteral, literal, distance, length) in tokens)
      if (isLiteral)
        bits += litLenLengths[literal];
      else {
        var lenCode = Deflate64Constants.GetLengthCode(length);
        bits += litLenLengths[lenCode];
        bits += Deflate64Constants.LengthExtraBits[lenCode - 257];
        var distCode = Deflate64Constants.GetDistanceCode(distance);
        bits += distLengths[distCode];
        bits += Deflate64Constants.DistanceExtraBits[distCode];
      }

    bits += litLenLengths[DeflateConstants.EndOfBlock];
    return bits;
  }

  private static List<(int Symbol, int ExtraBits, int ExtraValue)> RunLengthEncode(int[] lengths) {
    var result = new List<(int, int, int)>();
    var i = 0;

    while (i < lengths.Length) {
      var value = lengths[i];

      if (value == 0) {
        var count = 1;
        while (i + count < lengths.Length && lengths[i + count] == 0)
          ++count;

        while (count > 0)
          switch (count) {
            case >= 11: {
              var run = Math.Min(count, 138);
              result.Add((18, 7, run - 11));
              count -= run;
              continue;
            }
            case >= 3:
              result.Add((17, 3, count - 3));
              count = 0;
              continue;
            default:
              result.Add((0, 0, 0));
              --count;
              continue;
          }

        i += lengths.Skip(i).TakeWhile(x => x == 0).Count();
      } else {
        result.Add((value, 0, 0));
        ++i;

        var count = 0;
        while (i + count < lengths.Length && lengths[i + count] == value)
          ++count;

        while (count >= 3) {
          var run = Math.Min(count, 6);
          result.Add((16, 2, run - 3));
          count -= run;
        }
        while (count > 0) {
          result.Add((value, 0, 0));
          --count;
        }
        i += lengths.Skip(i).TakeWhile(x => x == value).Count();
      }
    }

    return result;
  }
}
