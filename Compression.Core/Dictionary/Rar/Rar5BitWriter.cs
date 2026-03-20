namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Bit writer for RAR5 compressed streams. Writes bits LSB-first, matching <see cref="Rar5BitReader"/>.
/// </summary>
internal sealed class Rar5BitWriter {
  private readonly List<byte> _output = [];
  private ulong _bitBuffer;
  private int _bitsUsed;

  /// <summary>
  /// Writes <paramref name="count"/> bits (LSB-first) to the stream.
  /// </summary>
  public void WriteBits(uint value, int count) {
    this._bitBuffer |= (ulong)(value & ((1u << count) - 1)) << this._bitsUsed;
    this._bitsUsed += count;
    this.Flush();
  }

  /// <summary>
  /// Writes a single bit.
  /// </summary>
  public void WriteBit(uint bit) => WriteBits(bit, 1);

  /// <summary>
  /// Flushes complete bytes from the buffer.
  /// </summary>
  private void Flush() {
    while (this._bitsUsed >= 8) {
      this._output.Add((byte)(this._bitBuffer & 0xFF));
      this._bitBuffer >>= 8;
      this._bitsUsed -= 8;
    }
  }

  /// <summary>
  /// Returns the written data as a byte array, flushing any remaining bits.
  /// </summary>
  public byte[] ToArray() {
    if (this._bitsUsed > 0)
      this._output.Add((byte)(this._bitBuffer & 0xFF));
    return [.. this._output];
  }
}
