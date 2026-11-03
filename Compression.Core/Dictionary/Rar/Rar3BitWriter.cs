namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Bit writer for RAR3 compressed streams. Writes bits MSB-first, matching <see cref="Rar3Decoder"/>.
/// Completed bytes collect in a buffer that <see cref="DrainTo"/> hands to a stream, so a caller
/// streaming a large file keeps only what it has not drained yet.
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

  /// <summary>Pads with zero bits to the next byte boundary, where RAR's table reader resumes.</summary>
  public void AlignToByte() {
    var pad = (8 - (this._bitsUsed & 7)) & 7;
    if (pad > 0) this.WriteBits(0, pad);
  }

  /// <summary>Writes the completed bytes to <paramref name="output"/> and forgets them.</summary>
  public void DrainTo(Stream output) {
    if (this._output.Count == 0) return;
    output.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(this._output));
    this._output.Clear();
  }

  /// <summary>
  /// Returns the written data as a byte array, flushing any remaining bits.
  /// </summary>
  public byte[] ToArray() {
    this.AlignToByte();
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
