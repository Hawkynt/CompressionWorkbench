using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Xpress;

/// <summary>
/// Represents a xpress huffman decompressor.
/// </summary>
public static partial class XpressHuffmanDecompressor {

  /// <summary>
  /// The reading side of an XPRESS Huffman chunk: bits taken most significant
  /// first from 16-bit little-endian words, and raw bytes taken from between
  /// them.
  /// </summary>
  /// <remarks>
  /// <para>Two words are loaded before anything is decoded and one more each
  /// time the supply runs out, so the position a raw byte comes from is always
  /// two words ahead of the bits being decoded around it. That is not an
  /// implementation detail to be improved on: it is where the writer put the
  /// byte. Refilling one word earlier or later moves every raw byte in the
  /// chunk.</para>
  /// </remarks>
  private ref struct SpanBitReader {
    private readonly ReadOnlySpan<byte> _input;
    private readonly int _start;
    private int _next;              // where the next word or raw byte comes from
    private uint _bitBuffer;        // 32 bits, the next one to read at the top
    private int _spareBits;         // bits left before another word is needed

    /// <summary>Starts reading a chunk whose bit stream begins at <paramref name="start"/>.</summary>
    public SpanBitReader(ReadOnlySpan<byte> input, int start) {
      this._input = input;
      this._start = start;
      this._next = start;
      this._bitBuffer = (uint)this.TakeWord() << 16;
      this._bitBuffer |= this.TakeWord();
      this._spareBits = 16;
    }

    /// <summary>
    /// How long the chunk is, counting from the start of its bit stream — which
    /// is where the reader has got to, the two sides having taken the same words
    /// and the same bytes in the same order.
    /// </summary>
    public readonly int ChunkLength => this._next - this._start;

    /// <summary>Returns the next <paramref name="count"/> bits without consuming them.</summary>
    public readonly uint Peek(int count) => count <= 0 ? 0 : this._bitBuffer >> (32 - count);

    /// <summary>Consumes <paramref name="count"/> bits, refilling if that used them up.</summary>
    public void Remove(int count) {
      if (count <= 0)
        return;

      this._bitBuffer <<= count;
      this._spareBits -= count;
      if (this._spareBits >= 0)
        return;

      this._bitBuffer |= (uint)this.TakeWord() << -this._spareBits;
      this._spareBits += 16;
    }

    /// <summary>Reads <paramref name="count"/> bits, most significant first.</summary>
    public uint ReadBits(int count) {
      var value = this.Peek(count);
      this.Remove(count);
      return value;
    }

    /// <summary>Reads one raw byte from between the words.</summary>
    public byte ReadRawByte() => this._next < this._input.Length ? this._input[this._next++] : (byte)0;

    /// <summary>Reads a raw 16-bit little-endian value from between the words.</summary>
    public ushort ReadRawUInt16() {
      var low = this.ReadRawByte();
      var high = this.ReadRawByte();
      return (ushort)(low | (high << 8));
    }

    private ushort TakeWord() {
      var word = this._next + 2 <= this._input.Length
        ? BinaryPrimitives.ReadUInt16LittleEndian(this._input[this._next..])
        : (ushort)0;
      this._next += 2;
      return word;
    }
  }
}
