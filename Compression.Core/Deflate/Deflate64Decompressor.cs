using System.Buffers;
using Compression.Core.BitIO;
using Compression.Core.DataStructures;

namespace Compression.Core.Deflate;

/// <summary>
/// Decompresses data in the Deflate64 (Enhanced Deflate) format.
/// Deflate64 extends DEFLATE with a 64 KB sliding window, two additional distance codes
/// (30 and 31 with 14 extra bits each), and length code 285 representing lengths 3..65538
/// via 16 extra bits (instead of the fixed value 258 in standard Deflate).
/// </summary>
public sealed class Deflate64Decompressor {
  private readonly BitBuffer<LsbBitOrder> _bitBuffer;
  private readonly SlidingWindow _window;
  private readonly List<byte> _output;
  private readonly byte[] _copyBuf;

  private bool _isFinalBlock;
  private bool _done;

  // Cached static tables (same as standard Deflate for static Huffman blocks)
  private static readonly DeflateHuffmanTable StaticLiteralTable = DeflateHuffmanTable.CreateStaticLiteralTable();
  private static readonly DeflateHuffmanTable StaticDistanceTable = DeflateHuffmanTable.CreateStaticDistanceTable();

  /// <summary>
  /// Initializes a new <see cref="Deflate64Decompressor"/> for streaming decompression.
  /// </summary>
  /// <param name="input">The stream containing Deflate64 compressed data.</param>
  public Deflate64Decompressor(Stream input) {
    this._bitBuffer = new(input);
    this._window = new(Deflate64Constants.WindowSize);
    this._output = [];
    this._copyBuf = new byte[Deflate64Constants.MaxMatchLength];
  }

  /// <summary>
  /// Decompresses all data from the stream.
  /// </summary>
  /// <returns>The decompressed data.</returns>
  public byte[] DecompressAll() {
    while (!this._done)
      this.DecompressBlock();

    return [.. this._output];
  }

  /// <summary>
  /// Decompresses Deflate64 data in one shot.
  /// </summary>
  /// <param name="compressedData">The Deflate64 compressed data.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> compressedData) {
    var rented = ArrayPool<byte>.Shared.Rent(compressedData.Length);
    try {
      compressedData.CopyTo(rented);
      using var ms = new MemoryStream(rented, 0, compressedData.Length);
      var decompressor = new Deflate64Decompressor(ms);
      return decompressor.DecompressAll();
    } finally {
      ArrayPool<byte>.Shared.Return(rented);
    }
  }

  /// <summary>
  /// Decompresses data from the input stream into the provided buffer.
  /// Returns the number of bytes written. Returns 0 when decompression is complete.
  /// </summary>
  /// <param name="output">Buffer to write decompressed data into.</param>
  /// <param name="offset">Offset in the output buffer.</param>
  /// <param name="count">Maximum number of bytes to write.</param>
  /// <returns>Number of bytes written, or 0 if decompression is complete.</returns>
  public int Decompress(byte[] output, int offset, int count) {
    if (this._done && this._output.Count == 0)
      return 0;

    while (this._output.Count < count && !this._done)
      this.DecompressBlock();

    var bytesToCopy = Math.Min(count, this._output.Count);
    if (bytesToCopy <= 0)
      return bytesToCopy;

    this._output.CopyTo(0, output, offset, bytesToCopy);
    this._output.RemoveRange(0, bytesToCopy);
    return bytesToCopy;
  }

  private void DecompressBlock() {
    if (!this._bitBuffer.EnsureBits(3)) {
      this._done = true;
      return;
    }

    var bfinal = this._bitBuffer.ReadBits(1);
    var btype = this._bitBuffer.ReadBits(2);
    this._isFinalBlock = bfinal == 1;

    switch (btype) {
      case DeflateConstants.BlockTypeUncompressed:
        this.DecompressUncompressedBlock();
        break;
      case DeflateConstants.BlockTypeStaticHuffman:
        this.DecompressHuffmanBlock(Deflate64Decompressor.StaticLiteralTable, Deflate64Decompressor.StaticDistanceTable);
        break;
      case DeflateConstants.BlockTypeDynamicHuffman:
        this.ReadDynamicTables(out var litLen, out var dist);
        this.DecompressHuffmanBlock(litLen, dist);
        break;
      default:
        throw new InvalidDataException($"Invalid Deflate64 block type: {btype}");
    }

    if (this._isFinalBlock)
      this._done = true;
  }

  private void DecompressUncompressedBlock() {
    this._bitBuffer.AlignToByte();

    var len = this._bitBuffer.ReadBits(16);
    var nlen = this._bitBuffer.ReadBits(16);

    if ((len ^ nlen) != 0xFFFF)
      throw new InvalidDataException("Invalid uncompressed block: LEN/NLEN mismatch.");

    for (var i = 0; i < (int)len; ++i) {
      var b = this._bitBuffer.ReadBits(8);
      var value = (byte)b;
      this._output.Add(value);
      this._window.WriteByte(value);
    }
  }

  private void DecompressHuffmanBlock(DeflateHuffmanTable litLenTable, DeflateHuffmanTable distTable) {
    // Max match length in Deflate64 is 65538
    var copyBuf = this._copyBuf;

    while (true) {
      var symbol = litLenTable.DecodeSymbol(this._bitBuffer);

      if (symbol < DeflateConstants.EndOfBlock) {
        var b = (byte)symbol;
        this._output.Add(b);
        this._window.WriteByte(b);
      } else if (symbol == DeflateConstants.EndOfBlock)
        break;
      else {
        // Length/distance pair using Deflate64 tables
        var lengthIdx = symbol - 257;
        var length = Deflate64Constants.LengthBase[lengthIdx];
        var extraBits = Deflate64Constants.LengthExtraBits[lengthIdx];
        if (extraBits > 0)
          length += (int)this._bitBuffer.ReadBits(extraBits);

        var distSymbol = distTable.DecodeSymbol(this._bitBuffer);
        var distance = Deflate64Constants.DistanceBase[distSymbol];
        var distExtraBits = Deflate64Constants.DistanceExtraBits[distSymbol];
        if (distExtraBits > 0)
          distance += (int)this._bitBuffer.ReadBits(distExtraBits);

        // Copy from window
        this._window.CopyFromWindow(distance, length, copyBuf.AsSpan(0, length));
        for (var i = 0; i < length; ++i)
          this._output.Add(copyBuf[i]);
      }
    }
  }

  private void ReadDynamicTables(out DeflateHuffmanTable litLenTable, out DeflateHuffmanTable distTable) {
    var hlit = (int)this._bitBuffer.ReadBits(5) + 257;
    var hdist = (int)this._bitBuffer.ReadBits(5) + 1;
    var hclen = (int)this._bitBuffer.ReadBits(4) + 4;

    var codeLengthCodeLengths = new int[DeflateConstants.CodeLengthAlphabetSize];
    for (var i = 0; i < hclen; ++i)
      codeLengthCodeLengths[DeflateConstants.CodeLengthOrder[i]] = (int)this._bitBuffer.ReadBits(3);

    var codeLengthTable = new DeflateHuffmanTable(codeLengthCodeLengths);

    var totalCodes = hlit + hdist;
    var codeLengths = new int[totalCodes];
    var idx = 0;

    while (idx < totalCodes) {
      var sym = codeLengthTable.DecodeSymbol(this._bitBuffer);

      switch (sym) {
        case <= 15:
          codeLengths[idx++] = sym;
          break;
        case 16: {
          var repeatCount = (int)this._bitBuffer.ReadBits(2) + 3;
          if (idx == 0)
            throw new InvalidDataException("Code length 16 at start of table.");
          var prev = codeLengths[idx - 1];
          for (var i = 0; i < repeatCount; ++i)
            codeLengths[idx++] = prev;
          break;
        }
        case 17: {
          var repeatCount = (int)this._bitBuffer.ReadBits(3) + 3;
          for (var i = 0; i < repeatCount; ++i)
            codeLengths[idx++] = 0;
          break;
        }
        case 18: {
          var repeatCount = (int)this._bitBuffer.ReadBits(7) + 11;
          for (var i = 0; i < repeatCount; ++i)
            codeLengths[idx++] = 0;
          break;
        }
        default: throw new InvalidDataException($"Invalid code length symbol: {sym}");
      }
    }

    var litLenLengths = new int[hlit];
    codeLengths.AsSpan(0, hlit).CopyTo(litLenLengths);

    var distLengths = new int[hdist];
    codeLengths.AsSpan(hlit, hdist).CopyTo(distLengths);

    litLenTable = new(litLenLengths);
    distTable = new(distLengths);
  }
}
