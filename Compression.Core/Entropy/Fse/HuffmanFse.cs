using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Compression.Core.Entropy.Huffman;

namespace Compression.Core.Entropy.Fse;

/// <summary>
/// Huffman coding as used by Zstandard, with weight tables optionally transmitted via FSE.
/// Weights represent the number of bits for each symbol (0 = unused). A weight w means
/// 2^(w-1) occurrences in the Huffman table for w &gt; 0.
/// </summary>
public static class HuffmanFse {
  /// <summary>Maximum Huffman weight value (bit length).</summary>
  private const int MaxHuffmanBits = 12;

  /// <summary>
  /// Compresses data using Huffman coding with direct-encoded weights.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="maxSymbol">The maximum symbol value in the data (default 255).</param>
  /// <returns>The compressed data including the weight table header.</returns>
  public static byte[] CompressHuffman(ReadOnlySpan<byte> data, int maxSymbol = 255) {
    if (data.Length == 0)
      return [];

    // Build weights from data frequencies
    var weights = BuildWeights(data);

    // Find actual max symbol with non-zero weight
    var actualMax = 0;
    for (var i = maxSymbol; i >= 0; --i)
      if (weights[i] > 0) {
        actualMax = i;
        break;
      }

    // Build canonical Huffman codes from bit lengths
    var maxBitLen = 0;
    for (var i = 0; i <= actualMax; ++i)
      if (weights[i] > maxBitLen)
        maxBitLen = weights[i];

    // Assign canonical codes (MSB-first)
    var nextCode = new uint[maxBitLen + 1];
    CanonicalCodeAssigner.ComputeNextCodes(weights.AsSpan(0, actualMax + 1), maxBitLen, nextCode);

    // Store both MSB-first code and bit-reversed code for encoding
    var reversedCodes = new uint[actualMax + 1];
    var codeLengths = new int[actualMax + 1];
    for (var symbol = 0; symbol <= actualMax; ++symbol) {
      var len = weights[symbol];
      if (len <= 0)
        continue;

      var msbCode = nextCode[len]++;
      reversedCodes[symbol] = ReverseBits(msbCode, len);
      codeLengths[symbol] = len;
    }

    // Output buffer
    var output = new byte[data.Length + 256];
    var pos = 0;

    // Write weight table
    pos += WriteWeights(output, pos, weights, actualMax);

    // Encode data using Huffman with bit-reversed codes (LSB-first in bitstream)
    ulong bitContainer = 0;
    var bitCount = 0;

    foreach (var symbol in data) {
      var len = codeLengths[symbol];
      var code = reversedCodes[symbol];

      bitContainer |= (ulong)code << bitCount;
      bitCount += len;

      while (bitCount >= 8) {
        output[pos++] = (byte)bitContainer;
        bitContainer >>= 8;
        bitCount -= 8;
      }
    }

    // Flush remaining bits with sentinel
    bitContainer |= 1UL << bitCount;
    ++bitCount;

    while (bitCount > 0) {
      output[pos++] = (byte)bitContainer;
      bitContainer >>= 8;
      bitCount -= 8;
    }

    return output.AsSpan(0, pos).ToArray();
  }

  /// <summary>
  /// Decompresses Huffman-coded data produced by <see cref="CompressHuffman"/>.
  /// </summary>
  /// <param name="compressed">The compressed data including the weight table header.</param>
  /// <param name="decompressedSize">The expected size of the decompressed data.</param>
  /// <returns>The decompressed byte array.</returns>
  /// <exception cref="InvalidDataException">The compressed data is malformed.</exception>
  public static byte[] DecompressHuffman(ReadOnlySpan<byte> compressed, int decompressedSize) {
    if (decompressedSize == 0)
      return [];

    // Read weight table
    var weights = ReadWeights(compressed, out var bytesRead);

    // Find max symbol and max bit length
    var maxSymbol = 0;
    var maxBitLen = 0;
    for (var i = weights.Length - 1; i >= 0; --i) {
      if (weights[i] > 0 && maxSymbol == 0)
        maxSymbol = i;
      if (weights[i] > maxBitLen)
        maxBitLen = weights[i];
    }

    // Build canonical codes (MSB-first, same assignment as encoder)
    var nextCode = new uint[maxBitLen + 1];
    CanonicalCodeAssigner.ComputeNextCodes(weights.AsSpan(0, maxSymbol + 1), maxBitLen, nextCode);

    // Build lookup table using bit-reversed codes (LSB-first, matching the bitstream)
    var lookupSize = 1 << maxBitLen;
    var lookupSymbol = new int[lookupSize];
    var lookupLen = new int[lookupSize];
    lookupSymbol.AsSpan().Fill(-1);

    for (var symbol = 0; symbol <= maxSymbol; symbol++) {
      var len = weights[symbol];
      if (len <= 0) continue;

      var msbCode = nextCode[len]++;
      var lsbCode = ReverseBits(msbCode, len);

      // Fill lookup: lsbCode is `len` bits. Pad with all combinations of
      // (maxBitLen - len) high bits to fill the lookup table.
      var shift = maxBitLen - len;
      var count = 1 << shift;
      var start = (int)lsbCode;
      for (var j = 0; j < count; ++j) {
        var index = start | (j << len);
        lookupSymbol[index] = symbol;
        lookupLen[index] = len;
      }
    }

    // Decode the bitstream
    var bitData = compressed[bytesRead..];
    var output = new byte[decompressedSize];
    var outputPos = 0;

    var bytePos = 0;
    ulong bitBuf = 0;
    var bitsAvailable = 0;

    // Load initial bits
    while (bitsAvailable <= 56 && bytePos < bitData.Length) {
      bitBuf |= (ulong)bitData[bytePos++] << bitsAvailable;
      bitsAvailable += 8;
    }

    while (outputPos < decompressedSize) {
      // Ensure we have enough bits
      while (bitsAvailable < maxBitLen + 8 && bytePos < bitData.Length) {
        bitBuf |= (ulong)bitData[bytePos++] << bitsAvailable;
        bitsAvailable += 8;
      }

      // Read maxBitLen bits and look up directly (codes are LSB-first in bitstream)
      var bits = (uint)(bitBuf & ((1UL << maxBitLen) - 1));
      var sym = lookupSymbol[(int)bits];
      if (sym < 0)
        ThrowInvalidHuffmanCode();

      var codeLen = lookupLen[(int)bits];
      output[outputPos++] = (byte)sym;

      bitBuf >>= codeLen;
      bitsAvailable -= codeLen;
    }

    return output;
  }

  /// <summary>
  /// Builds Huffman bit-length weights from frequency data.
  /// Weight w means the symbol has a code of w bits. 0 means unused.
  /// </summary>
  /// <param name="data">The data to analyze.</param>
  /// <returns>An array of weights indexed by symbol value (0..255).</returns>
  public static int[] BuildWeights(ReadOnlySpan<byte> data) {
    var freq = new long[256];
    foreach (var value in data)
      ++freq[value];

    var symbolCount = 0;
    for (var i = 0; i < 256; ++i)
      if (freq[i] > 0)
        ++symbolCount;

    if (symbolCount == 0)
      return new int[256];

    var root = HuffmanTree.BuildFromFrequencies(freq);
    var codeLengths = HuffmanTree.GetCodeLengths(root, 256);

    HuffmanTree.LimitCodeLengths(codeLengths, HuffmanFse.MaxHuffmanBits);

    return codeLengths;
  }

  /// <summary>
  /// Writes a Huffman weight table to the output buffer using direct representation
  /// (4 bits per weight, 2 weights per byte).
  /// </summary>
  /// <param name="output">The output buffer.</param>
  /// <param name="pos">The starting position in the output buffer.</param>
  /// <param name="weights">The weight array (bit lengths per symbol).</param>
  /// <param name="maxSymbol">The maximum symbol with non-zero weight.</param>
  /// <returns>The number of bytes written.</returns>
  public static int WriteWeights(byte[] output, int pos, int[] weights, int maxSymbol) {
    var startPos = pos;
    var numSymbols = maxSymbol + 1;

    // Direct representation: header byte >= 128
    // headerByte = numSymbols + 127
    var headerByte = numSymbols + 127;
    if (headerByte > 255) {
      headerByte = 255;
      numSymbols = 128;
    }

    output[pos++] = (byte)headerByte;

    // Pack weights 4 bits each, 2 per byte
    for (var i = 0; i < numSymbols; i += 2) {
      var w0 = (i < weights.Length) ? weights[i] : 0;
      var w1 = (i + 1 < numSymbols && i + 1 < weights.Length) ? weights[i + 1] : 0;
      output[pos++] = (byte)((w0 & 0x0F) | ((w1 & 0x0F) << 4));
    }

    return pos - startPos;
  }

  /// <summary>
  /// Reads a Huffman weight table from compressed data.
  /// </summary>
  /// <param name="input">The input data starting at the weight table header.</param>
  /// <param name="bytesRead">The number of bytes consumed from the input.</param>
  /// <returns>An array of weights indexed by symbol value.</returns>
  /// <exception cref="InvalidDataException">The weight table is malformed.</exception>
  public static int[] ReadWeights(ReadOnlySpan<byte> input, out int bytesRead) {
    if (input.Length < 1)
      throw new InvalidDataException("Huffman weight table too short.");

    int headerByte = input[0];
    var pos = 1;

    if (headerByte >= 128) {
      // Direct representation
      var numSymbols = headerByte - 127;
      var weights = new int[256];
      var packedBytes = (numSymbols + 1) / 2;

      if (pos + packedBytes > input.Length)
        throw new InvalidDataException("Huffman weight table truncated.");

      for (var i = 0; i < numSymbols; i += 2) {
        var packed = input[pos++];
        weights[i] = packed & 0x0F;
        if (i + 1 < numSymbols)
          weights[i + 1] = (packed >> 4) & 0x0F;
      }

      bytesRead = pos;
      return weights;
    }
    else {
      // FSE-compressed weights
      var compressedSize = headerByte;
      if (pos + compressedSize > input.Length)
        throw new InvalidDataException("FSE-compressed weight table truncated.");

      var fseData = input.Slice(pos, compressedSize);
      var (normalizedCounts, maxSym, tableLog, headerBytes) = FseDecoder.ReadNormalizedCounts(fseData);

      var decoder = new FseDecoder(normalizedCounts, maxSym, tableLog);
      var weightStream = fseData[headerBytes..];

      var decoded = decoder.Decode(weightStream, 255);

      var weights = new int[256];
      for (var i = 0; i < decoded.Length && i < 256; ++i)
        weights[i] = decoded[i];

      bytesRead = pos + compressedSize;
      return weights;
    }
  }

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowInvalidHuffmanCode() =>
    throw new InvalidDataException("Invalid Huffman code in stream.");

  private static uint ReverseBits(uint value, int numBits) =>
    BitIO.BitHelpers.ReverseBits(value, numBits);
}
