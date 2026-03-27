namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Bit writer for RAR5 compressed streams. Writes bits MSB-first (big-endian),
/// matching the bit ordering used by RAR and <see cref="Rar5BitReader"/>.
/// </summary>
internal sealed class Rar5BitWriter {
  private readonly List<byte> _output = [];
  private uint _bitBuffer;
  private int _bitsUsed;
  private int _totalBitsWritten;

  /// <summary>Gets the total number of bits written so far.</summary>
  public int BitCount => this._totalBitsWritten;

  /// <summary>
  /// Writes <paramref name="count"/> bits (MSB-first) to the stream.
  /// The most significant bit of <paramref name="value"/> (within the <paramref name="count"/>-bit field)
  /// is written first.
  /// </summary>
  public void WriteBits(uint value, int count) {
    // Place value bits into the top of the 32-bit buffer
    this._bitBuffer |= (value & ((1u << count) - 1)) << (32 - this._bitsUsed - count);
    this._bitsUsed += count;
    this._totalBitsWritten += count;
    this.Flush();
  }

  /// <summary>
  /// Writes a single bit.
  /// </summary>
  public void WriteBit(uint bit) => WriteBits(bit, 1);

  /// <summary>
  /// Copies <paramref name="bitCount"/> bits from a byte array into this writer (MSB-first).
  /// </summary>
  /// <param name="data">Source byte array (MSB-first packed).</param>
  /// <param name="bitCount">Number of bits to copy.</param>
  public void WriteBytes(byte[] data, int bitCount) {
    var bitsRemaining = bitCount;
    var byteIdx = 0;
    while (bitsRemaining >= 8) {
      WriteBits(data[byteIdx++], 8);
      bitsRemaining -= 8;
    }
    if (bitsRemaining > 0)
      WriteBits((uint)data[byteIdx] >> (8 - bitsRemaining), bitsRemaining);
  }

  /// <summary>
  /// Flushes complete bytes from the buffer.
  /// </summary>
  private void Flush() {
    while (this._bitsUsed >= 8) {
      this._output.Add((byte)(this._bitBuffer >> 24));
      this._bitBuffer <<= 8;
      this._bitsUsed -= 8;
    }
  }

  /// <summary>
  /// Returns the written data as a byte array, flushing any remaining bits.
  /// </summary>
  public byte[] ToArray() {
    if (this._bitsUsed > 0)
      this._output.Add((byte)(this._bitBuffer >> 24));
    return [.. this._output];
  }
}
