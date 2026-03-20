namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Bit writer for RAR3 compressed streams. Writes bits MSB-first, matching <see cref="Rar3Decoder"/>.
/// </summary>
internal sealed class Rar3BitWriter {
  private readonly List<byte> _output = [];
  private uint _bitBuffer;
  private int _bitsUsed;

  /// <summary>
  /// Writes <paramref name="count"/> bits (MSB-first) to the stream.
  /// </summary>
  public void WriteBits(uint value, int count) {
    this._bitBuffer |= (value & ((1u << count) - 1)) << (32 - this._bitsUsed - count);
    this._bitsUsed += count;
    this.Flush();
  }

  /// <summary>
  /// Writes a single bit.
  /// </summary>
  public void WriteBit(uint bit) => WriteBits(bit, 1);

  /// <summary>
  /// Returns the written data as a byte array, flushing any remaining bits.
  /// </summary>
  public byte[] ToArray() {
    if (this._bitsUsed > 0) {
      // Flush remaining partial byte(s)
      while (this._bitsUsed > 0) {
        this._output.Add((byte)(this._bitBuffer >> 24));
        this._bitBuffer <<= 8;
        this._bitsUsed -= 8;
      }
    }
    return [.. this._output];
  }

  private void Flush() {
    while (this._bitsUsed >= 8) {
      this._output.Add((byte)(this._bitBuffer >> 24));
      this._bitBuffer <<= 8;
      this._bitsUsed -= 8;
    }
  }
}
