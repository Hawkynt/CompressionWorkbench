using Compression.Core.BitIO;

namespace Compression.Tests.BitIO;

[TestFixture]
public class BitBufferTests {
  [Test]
  public void PeekBits_LsbFirst_DoesNotConsumeBits() {
    var stream = new MemoryStream([0xAB]);
    var buffer = new BitBuffer(stream, BitOrder.LsbFirst);

    uint peeked = buffer.PeekBits(4);
    Assert.That(peeked, Is.EqualTo(0x0Bu)); // lower nibble of 0xAB

    uint peekedAgain = buffer.PeekBits(4);
    Assert.That(peekedAgain, Is.EqualTo(0x0Bu)); // Same value — not consumed
  }

  [Test]
  public void DropBits_ConsumesSpecifiedBits() {
    var stream = new MemoryStream([0xAB]);
    var buffer = new BitBuffer(stream, BitOrder.LsbFirst);

    buffer.PeekBits(4);
    buffer.DropBits(4);

    uint value = buffer.PeekBits(4);
    Assert.That(value, Is.EqualTo(0x0Au)); // upper nibble of 0xAB
  }

  [Test]
  public void ReadBits_LsbFirst_ConsumesAndReturns() {
    var stream = new MemoryStream([0xAB, 0xCD]);
    var buffer = new BitBuffer(stream, BitOrder.LsbFirst);

    uint v1 = buffer.ReadBits(8);
    Assert.That(v1, Is.EqualTo(0xABu));

    uint v2 = buffer.ReadBits(8);
    Assert.That(v2, Is.EqualTo(0xCDu));
  }

  [Test]
  public void ReadBits_MsbFirst_ConsumesAndReturns() {
    var stream = new MemoryStream([0xAB, 0xCD]);
    var buffer = new BitBuffer(stream, BitOrder.MsbFirst);

    uint v1 = buffer.ReadBits(8);
    Assert.That(v1, Is.EqualTo(0xABu));

    uint v2 = buffer.ReadBits(8);
    Assert.That(v2, Is.EqualTo(0xCDu));
  }

  [Test]
  public void EnsureBits_ReturnsFalseOnEndOfStream() {
    var stream = new MemoryStream([0xFF]);
    var buffer = new BitBuffer(stream, BitOrder.LsbFirst);

    Assert.That(buffer.EnsureBits(8), Is.True);
    buffer.DropBits(8);
    Assert.That(buffer.EnsureBits(1), Is.False);
  }

  [Test]
  public void PeekBits_ThrowsOnEndOfStream() {
    var stream = new MemoryStream([]);
    var buffer = new BitBuffer(stream, BitOrder.LsbFirst);

    Assert.Throws<EndOfStreamException>(() => buffer.PeekBits(1));
  }

  [Test]
  public void AlignToByte_DropsRemainderBits() {
    var stream = new MemoryStream([0xFF, 0xAB]);
    var buffer = new BitBuffer(stream, BitOrder.LsbFirst);

    buffer.ReadBits(3);
    buffer.AlignToByte();

    // Remaining bits from first byte are dropped
    // Buffer still has some bits from byte 1, next full byte read comes from byte 2
    // After align, bits in buffer are a multiple of 8
    Assert.That(buffer.BitsAvailable % 8, Is.EqualTo(0));
  }

  [Test]
  public void ReadBits_CrossesByteBoundary_LsbFirst() {
    var stream = new MemoryStream([0b11110000, 0b00001111]);
    var buffer = new BitBuffer(stream, BitOrder.LsbFirst);

    buffer.ReadBits(4); // consume lower 4 bits of first byte (0000)
    uint cross = buffer.ReadBits(8); // 4 bits from first byte + 4 bits from second byte
    // Upper 4 of first byte = 1111, lower 4 of second byte = 1111
    // LSB first: value = 0xFF
    Assert.That(cross, Is.EqualTo(0xFFu));
  }

  [Test]
  public void DropBits_ThrowsWhenDroppingMoreThanAvailable() {
    var stream = new MemoryStream([0xFF]);
    var buffer = new BitBuffer(stream, BitOrder.LsbFirst);

    buffer.EnsureBits(8);
    Assert.Throws<InvalidOperationException>(() => buffer.DropBits(9));
  }
}
