// HA's 16-bit binary arithmetic coder, implemented from docs/HA-ON-DISK.md §2.

namespace Compression.Core.Dictionary.Ha;

/// <summary>Decoder half of the HA arithmetic coder (docs/HA-ON-DISK.md §2).</summary>
internal sealed class HaArithmeticDecoder {
  private readonly ReadOnlyMemory<byte> _source;
  private int _position;
  private int _byte;
  private int _bitsLeft;
  private int _low;
  private int _high = 0xFFFF;
  private int _value;

  public HaArithmeticDecoder(ReadOnlyMemory<byte> source) {
    this._source = source;
    this._value = (this._NextByte() << 8) | this._NextByte();
  }

  // Past the end of the stream the coder sees 1-bits (§2.2).
  private int _NextByte() {
    var span = this._source.Span;
    return this._position < span.Length ? span[this._position++] : 0xFF;
  }

  private int _NextBit() {
    if (this._bitsLeft == 0) {
      this._byte = this._NextByte();
      this._bitsLeft = 8;
    }

    --this._bitsLeft;
    return (this._byte >> this._bitsLeft) & 1;
  }

  /// <summary>The position, in <c>[0, total)</c>, of the next symbol.</summary>
  public int Threshold(int total) {
    var range = (uint)(this._high - this._low) + 1;
    return (int)(((uint)(this._value - this._low + 1) * (uint)total - 1) / range);
  }

  /// <summary>Consumes the symbol occupying <c>[low, high)</c> of <paramref name="total"/>.</summary>
  public void Consume(int low, int high, int total) {
    var range = (uint)(this._high - this._low) + 1;
    this._high = (this._low + (int)(range * (uint)high / (uint)total) - 1) & 0xFFFF;
    this._low = (this._low + (int)(range * (uint)low / (uint)total)) & 0xFFFF;

    while (((this._high ^ this._low) & 0x8000) == 0) {
      this._low = (this._low << 1) & 0xFFFF;
      this._high = ((this._high << 1) | 1) & 0xFFFF;
      this._value = ((this._value << 1) | this._NextBit()) & 0xFFFF;
    }

    while ((this._low & 0x4000) != 0 && (this._high & 0x4000) == 0) {
      this._low = (this._low << 1) & 0x7FFF;
      this._high = ((this._high << 1) | 0x8001) & 0xFFFF;
      this._value = (((this._value << 1) ^ 0x8000) | this._NextBit()) & 0xFFFF;
    }
  }
}

/// <summary>Encoder half of the HA arithmetic coder (docs/HA-ON-DISK.md §2).</summary>
internal sealed class HaArithmeticEncoder {
  private readonly MemoryStream _output = new();
  private int _byte;
  private int _bitCount;
  private int _low;
  private int _high = 0xFFFF;
  private int _pending;

  private void _PutBit(int bit) {
    this._byte = (this._byte << 1) | bit;
    if (++this._bitCount < 8)
      return;

    this._output.WriteByte((byte)this._byte);
    this._byte = 0;
    this._bitCount = 0;
  }

  /// <summary>Codes the symbol occupying <c>[low, high)</c> of <paramref name="total"/>.</summary>
  public void Code(int low, int high, int total) {
    var range = (uint)(this._high - this._low) + 1;
    this._high = (this._low + (int)(range * (uint)high / (uint)total) - 1) & 0xFFFF;
    this._low = (this._low + (int)(range * (uint)low / (uint)total)) & 0xFFFF;

    if (((this._high ^ this._low) & 0x8000) == 0) {
      var bit = this._low >> 15;
      this._PutBit(bit);
      for (; this._pending > 0; --this._pending)
        this._PutBit(bit ^ 1);

      do {
        this._low = (this._low << 1) & 0xFFFF;
        this._high = ((this._high << 1) | 1) & 0xFFFF;
        if (((this._high ^ this._low) & 0x8000) != 0)
          break;
        this._PutBit(this._low >> 15);
      } while (true);
    }

    while ((this._low & 0x4000) != 0 && (this._high & 0x4000) == 0) {
      ++this._pending;
      this._low = (this._low << 1) & 0x7FFF;
      this._high = ((this._high << 1) | 0x8001) & 0xFFFF;
    }
  }

  /// <summary>Terminates the stream (§2.2) and returns every byte written.</summary>
  public byte[] Finish() {
    var bit = (this._low >> 14) & 1;
    this._PutBit(bit);
    for (var i = this._pending + 1; i > 0; --i)
      this._PutBit(bit ^ 1);

    while (this._bitCount != 0)
      this._PutBit(0);
    return this._output.ToArray();
  }
}
