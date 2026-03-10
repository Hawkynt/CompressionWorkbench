using Compression.Core.BitIO;
using Compression.Core.DataStructures;

namespace Compression.Core.Deflate;

/// <summary>
/// Decompresses data in the DEFLATE format (RFC 1951).
/// </summary>
public sealed class DeflateDecompressor {
  private readonly BitBuffer<LsbBitOrder> _bitBuffer;
  private readonly SlidingWindow _window;
  private readonly List<byte> _output;

  private bool _isFinalBlock;
  private bool _done;

  // Cached static tables
  private static readonly DeflateHuffmanTable StaticLiteralTable = DeflateHuffmanTable.CreateStaticLiteralTable();
  private static readonly DeflateHuffmanTable StaticDistanceTable = DeflateHuffmanTable.CreateStaticDistanceTable();

  /// <summary>
  /// Initializes a new <see cref="DeflateDecompressor"/> for streaming decompression.
  /// </summary>
  /// <param name="input">The stream containing DEFLATE compressed data.</param>
  public DeflateDecompressor(Stream input) {
    this._bitBuffer = new BitBuffer<LsbBitOrder>(input);
    this._window = new SlidingWindow(DeflateConstants.WindowSize);
    this._output = new List<byte>();
  }

  /// <summary>
  /// Decompresses all data from the stream.
  /// </summary>
  /// <returns>The decompressed data.</returns>
  public byte[] DecompressAll() {
    while (!this._done)
      DecompressBlock();

    return [.. this._output];
  }

  /// <summary>
  /// Decompresses DEFLATE data in one shot.
  /// </summary>
  /// <param name="compressedData">The DEFLATE compressed data.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> compressedData) {
    using var ms = new MemoryStream(compressedData.ToArray());
    var decompressor = new DeflateDecompressor(ms);
    return decompressor.DecompressAll();
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

    // Decompress blocks until we have enough output or are done
    while (this._output.Count < count && !this._done)
      DecompressBlock();

    int bytesToCopy = Math.Min(count, this._output.Count);
    if (bytesToCopy > 0) {
      this._output.CopyTo(0, output, offset, bytesToCopy);
      this._output.RemoveRange(0, bytesToCopy);
    }

    return bytesToCopy;
  }

  private void DecompressBlock() {
    // Check if there's any data to decompress
    if (!this._bitBuffer.EnsureBits(3)) {
      this._done = true;
      return;
    }

    // Read block header
    uint bfinal = this._bitBuffer.ReadBits(1);
    uint btype = this._bitBuffer.ReadBits(2);
    this._isFinalBlock = bfinal == 1;

    switch (btype) {
      case DeflateConstants.BlockTypeUncompressed:
        DecompressUncompressedBlock();
        break;
      case DeflateConstants.BlockTypeStaticHuffman:
        DecompressHuffmanBlock(StaticLiteralTable, StaticDistanceTable);
        break;
      case DeflateConstants.BlockTypeDynamicHuffman:
        ReadDynamicTables(out var litLen, out var dist);
        DecompressHuffmanBlock(litLen, dist);
        break;
      default:
        throw new InvalidDataException($"Invalid DEFLATE block type: {btype}");
    }

    if (this._isFinalBlock)
      this._done = true;
  }

  private void DecompressUncompressedBlock() {
    this._bitBuffer.AlignToByte();

    uint len = this._bitBuffer.ReadBits(16);
    uint nlen = this._bitBuffer.ReadBits(16);

    if ((len ^ nlen) != 0xFFFF)
      throw new InvalidDataException("Invalid uncompressed block: LEN/NLEN mismatch.");

    for (int i = 0; i < (int)len; ++i) {
      uint b = this._bitBuffer.ReadBits(8);
      byte value = (byte)b;
      this._output.Add(value);
      this._window.WriteByte(value);
    }
  }

  private void DecompressHuffmanBlock(DeflateHuffmanTable litLenTable, DeflateHuffmanTable distTable) {
    // Max match length in Deflate is 258
    byte[] copyBuf = new byte[258];

    while (true) {
      int symbol = litLenTable.DecodeSymbol(this._bitBuffer);

      if (symbol < DeflateConstants.EndOfBlock) {
        // Literal byte
        byte b = (byte)symbol;
        this._output.Add(b);
        this._window.WriteByte(b);
      }
      else if (symbol == DeflateConstants.EndOfBlock) {
        // End of block
        break;
      }
      else {
        // Length/distance pair
        int lengthIdx = symbol - 257;
        int length = DeflateConstants.LengthBase[lengthIdx];
        int extraBits = DeflateConstants.LengthExtraBits[lengthIdx];
        if (extraBits > 0)
          length += (int)this._bitBuffer.ReadBits(extraBits);

        int distSymbol = distTable.DecodeSymbol(this._bitBuffer);
        int distance = DeflateConstants.DistanceBase[distSymbol];
        int distExtraBits = DeflateConstants.DistanceExtraBits[distSymbol];
        if (distExtraBits > 0)
          distance += (int)this._bitBuffer.ReadBits(distExtraBits);

        // Copy from window
        this._window.CopyFromWindow(distance, length, copyBuf.AsSpan(0, length));
        for (int i = 0; i < length; ++i)
          this._output.Add(copyBuf[i]);
      }
    }
  }

  private void ReadDynamicTables(out DeflateHuffmanTable litLenTable, out DeflateHuffmanTable distTable) {
    int hlit = (int)this._bitBuffer.ReadBits(5) + 257;   // 257–286
    int hdist = (int)this._bitBuffer.ReadBits(5) + 1;     // 1–32
    int hclen = (int)this._bitBuffer.ReadBits(4) + 4;     // 4–19

    // Read code-length code lengths in permuted order
    int[] codeLengthCodeLengths = new int[DeflateConstants.CodeLengthAlphabetSize];
    for (int i = 0; i < hclen; ++i)
      codeLengthCodeLengths[DeflateConstants.CodeLengthOrder[i]] = (int)this._bitBuffer.ReadBits(3);

    var codeLengthTable = new DeflateHuffmanTable(codeLengthCodeLengths);

    // Decode literal/length + distance code lengths
    int totalCodes = hlit + hdist;
    int[] codeLengths = new int[totalCodes];
    int idx = 0;

    while (idx < totalCodes) {
      int sym = codeLengthTable.DecodeSymbol(this._bitBuffer);

      if (sym <= 15)
        codeLengths[idx++] = sym;
      else if (sym == 16) {
        // Repeat previous length 3–6 times
        int repeatCount = (int)this._bitBuffer.ReadBits(2) + 3;
        if (idx == 0)
          throw new InvalidDataException("Code length 16 at start of table.");

        int prev = codeLengths[idx - 1];
        for (int i = 0; i < repeatCount; ++i)
          codeLengths[idx++] = prev;
      }
      else if (sym == 17) {
        // Repeat 0 for 3–10 times
        int repeatCount = (int)this._bitBuffer.ReadBits(3) + 3;
        for (int i = 0; i < repeatCount; ++i)
          codeLengths[idx++] = 0;
      }
      else if (sym == 18) {
        // Repeat 0 for 11–138 times
        int repeatCount = (int)this._bitBuffer.ReadBits(7) + 11;
        for (int i = 0; i < repeatCount; ++i)
          codeLengths[idx++] = 0;
      }
      else
        throw new InvalidDataException($"Invalid code length symbol: {sym}");
    }

    // Split into literal/length and distance code lengths
    int[] litLenLengths = new int[hlit];
    codeLengths.AsSpan(0, hlit).CopyTo(litLenLengths);

    int[] distLengths = new int[hdist];
    codeLengths.AsSpan(hlit, hdist).CopyTo(distLengths);

    litLenTable = new DeflateHuffmanTable(litLenLengths);
    distTable = new DeflateHuffmanTable(distLengths);
  }
}
