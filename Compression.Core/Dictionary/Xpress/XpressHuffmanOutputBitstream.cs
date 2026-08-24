using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Xpress;

/// <summary>
/// The output side of an XPRESS Huffman chunk: a stream of 16-bit words holding
/// Huffman codes, with raw bytes free to appear between them.
/// </summary>
/// <remarks>
/// <para>The awkward part of the format is that a chunk carries two things at
/// once — a bit stream of codes and the occasional whole byte of match length —
/// interleaved in one run of bytes. A decoder reads bits from 16-bit words but
/// takes those raw bytes from wherever it has got to, which is two words further
/// on, because it is always holding the next two words already.</para>
///
/// <para>So the writer keeps two word-sized holes open ahead of itself. Bits go
/// into the older hole when sixteen of them have accumulated, the holes shuffle
/// forward, and raw bytes are appended past both. Written in the obvious way
/// instead — bits and bytes in the order they occur — every raw byte would land
/// two words earlier than the decoder looks for it.</para>
///
/// <para>Format per [MS-XCA] section 2.2, "LZ77+Huffman Compression Algorithm".</para>
/// </remarks>
internal sealed class XpressHuffmanOutputBitstream {
  private byte[] _buffer;
  private int _length;            // one past the last byte handed out
  private int _firstHole;         // the word-sized hole bits are written into next
  private int _secondHole;        // the one after it
  private uint _bitBuffer;        // pending bits, newest in the low positions
  private int _bitCount;

  /// <summary>Creates a bitstream with room for <paramref name="capacity"/> bytes.</summary>
  public XpressHuffmanOutputBitstream(int capacity) {
    this._buffer = new byte[Math.Max(capacity, 16)];
    this._firstHole = 0;
    this._secondHole = 2;
    this._length = 4;
  }

  /// <summary>
  /// Appends <paramref name="count"/> bits, most significant first.
  /// </summary>
  /// <param name="bits">The value to append, in its low <paramref name="count"/> bits.</param>
  /// <param name="count">How many bits to append; at most 16.</param>
  public void WriteBits(uint bits, int count) {
    if (count <= 0)
      return;

    this._bitCount += count;
    this._bitBuffer = (this._bitBuffer << count) | (bits & ((1u << count) - 1u));

    // Sixteen bits are only complete once there are more than sixteen: the
    // format's last word is written by Finish, left-aligned, and flushing at
    // exactly sixteen here would leave nothing for it to write.
    if (this._bitCount <= 16)
      return;

    this._bitCount -= 16;
    this.PutWord(this._firstHole, (ushort)(this._bitBuffer >> this._bitCount));
    this._firstHole = this._secondHole;
    this._secondHole = this.Take(2);
  }

  /// <summary>Appends one raw byte, past the two holes held open for bits.</summary>
  public void WriteByte(byte value) {
    // The index has to be taken first: taking it may replace the buffer, and an
    // index into the array as it was writes the byte into an array nobody keeps.
    var at = this.Take(1);
    this._buffer[at] = value;
  }

  /// <summary>Appends a raw 16-bit little-endian value.</summary>
  public void WriteUInt16(ushort value) {
    var at = this.Take(2);
    BinaryPrimitives.WriteUInt16LittleEndian(this._buffer.AsSpan(at), value);
  }

  /// <summary>
  /// Closes the chunk: the bits still pending go into the first hole,
  /// left-aligned, and the second is filled with zeros so the decoder that is
  /// always two words ahead has something to read.
  /// </summary>
  /// <returns>The chunk's bytes.</returns>
  public byte[] Finish() {
    this.PutWord(this._firstHole, (ushort)(this._bitBuffer << (16 - this._bitCount)));
    this.PutWord(this._secondHole, 0);
    return this._buffer.AsSpan(0, this._length).ToArray();
  }

  private int Take(int count) {
    var at = this._length;
    this._length += count;
    if (this._length > this._buffer.Length)
      Array.Resize(ref this._buffer, Math.Max(this._length, this._buffer.Length * 2));
    return at;
  }

  private void PutWord(int at, ushort value)
    => BinaryPrimitives.WriteUInt16LittleEndian(this._buffer.AsSpan(at), value);
}
