using System.Numerics;

namespace Compression.Core.Entropy.Fse;

/// <summary>
/// FSE entropy decoder using tANS (table-based Asymmetric Numeral Systems).
/// Reads a backward bitstream produced by <see cref="FseEncoder"/> and recovers
/// the original symbol sequence.
/// </summary>
public sealed class FseDecoder {
  private readonly FseTable _table;

  /// <summary>
  /// Initializes a new FSE decoder from normalized counts.
  /// </summary>
  /// <param name="normalizedCounts">Normalized frequency array (see <see cref="FseTable.Build"/>).</param>
  /// <param name="maxSymbol">The maximum symbol value present.</param>
  /// <param name="tableLog">The log2 of the table size.</param>
  public FseDecoder(short[] normalizedCounts, int maxSymbol, int tableLog) => this._table = FseTable.Build(normalizedCounts, maxSymbol, tableLog);

  /// <summary>
  /// Reads normalized counts from data written by <see cref="FseEncoder.WriteNormalizedCounts"/>.
  /// </summary>
  /// <param name="input">The input data starting at the header.</param>
  /// <returns>
  /// A tuple containing the normalized counts, maximum symbol, table log, and number of bytes consumed.
  /// </returns>
  /// <exception cref="InvalidDataException">The header data is malformed.</exception>
  public static (short[] NormalizedCounts, int MaxSymbol, int TableLog, int BytesRead)
    ReadNormalizedCounts(ReadOnlySpan<byte> input) {
    if (input.Length < 3)
      throw new InvalidDataException("FSE header too short.");

    int pos = 0;

    int tableLog = input[pos++];
    int maxSymbol = input[pos++] | (input[pos++] << 8);

    if (tableLog < FseConstants.MinTableLog || tableLog > FseConstants.MaxTableLog)
      throw new InvalidDataException($"Invalid FSE table log: {tableLog}.");

    if (maxSymbol > FseConstants.MaxSymbolValue)
      throw new InvalidDataException($"Invalid FSE max symbol: {maxSymbol}.");

    var normalized = new short[maxSymbol + 1];

    int needed = (maxSymbol + 1) * 2;
    if (pos + needed > input.Length)
      throw new InvalidDataException("FSE normalized counts data truncated.");

    for (int s = 0; s <= maxSymbol; ++s) {
      normalized[s] = (short)(input[pos] | (input[pos + 1] << 8));
      pos += 2;
    }

    return (normalized, maxSymbol, tableLog, pos);
  }

  /// <summary>
  /// Decodes compressed data produced by <see cref="FseEncoder.Encode"/> and returns
  /// the original byte sequence.
  /// </summary>
  /// <param name="compressed">The compressed data (backward bitstream with sentinel).</param>
  /// <param name="originalSize">The number of bytes in the original data.</param>
  /// <returns>The decompressed byte array.</returns>
  /// <exception cref="InvalidDataException">The compressed data is malformed.</exception>
  public byte[] Decode(ReadOnlySpan<byte> compressed, int originalSize) {
    if (originalSize == 0)
      return [];

    var output = new byte[originalSize];

    // The encoder writes bits from LSB upward into a byte stream.
    // Layout: [encoding bits (LSB)] ... [state bits] [sentinel bit (MSB)]
    // We need to read from MSB down: first the state, then the encoding bits.

    // Step 1: Load all bits into a large bit buffer.
    // The total number of valid bits is determined by the sentinel position.
    int totalBits = FindTotalBits(compressed);

    // Step 2: Load all bytes into a contiguous bit buffer.
    // We use a ulong array for large data, or just work byte-by-byte.
    var bitReader = new MsbBitReader(compressed, totalBits);

    // Step 3: Read initial state (tableLog bits from the top)
    int state = bitReader.ReadBitsFromTop(this._table.TableLog);

    // Step 4: Decode symbols
    // The last symbol decoded doesn't need a state transition (no bits to read)
    for (int i = 0; i < originalSize; ++i) {
      output[i] = this._table.Symbol[state];

      if (i < originalSize - 1) {
        int nbBits = this._table.NumBits[state];
        int readBits = nbBits > 0 ? bitReader.ReadBitsFromTop(nbBits) : 0;
        state = this._table.NewStateBase[state] + readBits;
      }
    }

    return output;
  }

  /// <summary>
  /// Finds the total number of data bits in the compressed stream (excluding sentinel).
  /// </summary>
  private static int FindTotalBits(ReadOnlySpan<byte> compressed) {
    // Find the sentinel: the highest set bit in the entire bitstream
    // The bitstream is stored as a sequence of bytes with bits from position 0 upward.
    // Total bits in the stream = (compressed.Length * 8)
    // The sentinel is the highest set bit. Data bits are below the sentinel.

    // Find the last non-zero byte
    int lastByteIndex = compressed.Length - 1;
    while (lastByteIndex > 0 && compressed[lastByteIndex] == 0)
      --lastByteIndex;

    if (compressed[lastByteIndex] == 0)
      throw new InvalidDataException("No sentinel bit found in FSE stream.");

    int lastByte = compressed[lastByteIndex];
    int highBit = BitOperations.Log2((uint)lastByte);

    // Total data bits = position of sentinel bit = lastByteIndex * 8 + highBit
    return lastByteIndex * 8 + highBit;
  }

  /// <summary>
  /// Reads bits from MSB to LSB of a flat bit buffer. The buffer is stored
  /// as bytes where byte 0 contains bits 0..7 (LSB first). Reading from the
  /// top means starting at the highest bit position and working down.
  /// </summary>
  private ref struct MsbBitReader {
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPos; // next bit position to read (counting down from totalBits-1)

    /// <summary>
    /// Initializes the reader with the data and the total number of valid data bits.
    /// </summary>
    /// <param name="data">The byte array containing the bitstream.</param>
    /// <param name="totalBits">Total number of data bits (sentinel excluded).</param>
    public MsbBitReader(ReadOnlySpan<byte> data, int totalBits) {
      this._data = data;
      this._bitPos = totalBits - 1; // start from the highest data bit
    }

    /// <summary>
    /// Reads nbBits bits from the top of the remaining bitstream.
    /// Returns the value with the first bit read in the MSB position.
    /// </summary>
    /// <param name="nbBits">Number of bits to read.</param>
    /// <returns>The value read.</returns>
    public int ReadBitsFromTop(int nbBits) {
      // Read nbBits starting from _bitPos going down.
      // The first bit read (at _bitPos) is the MSB of the result.
      int value = 0;
      for (int i = nbBits - 1; i >= 0; --i) {
        int bit = GetBit(this._bitPos);
        value |= bit << i;
        --this._bitPos;
      }

      return value;
    }

    /// <summary>
    /// Gets a single bit at the specified position in the flat buffer.
    /// </summary>
    private readonly int GetBit(int pos) {
      int byteIdx = pos >> 3;
      int bitIdx = pos & 7;
      return (this._data[byteIdx] >> bitIdx) & 1;
    }
  }
}
