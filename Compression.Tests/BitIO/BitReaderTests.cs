using Compression.Core.BitIO;

namespace Compression.Tests.BitIO;

[TestFixture]
public class BitReaderTests {
  [Test]
  public void ReadBit_LsbFirst_ReadsLeastSignificantBitFirst() {
    // 0b10110100 = 0xB4
    var stream = new MemoryStream([0xB4]);
    var reader = new BitReader(stream, BitOrder.LsbFirst);

    // LSB first: bits come out 0,0,1,0,1,1,0,1
    Assert.That(reader.ReadBit(), Is.EqualTo(0));
    Assert.That(reader.ReadBit(), Is.EqualTo(0));
    Assert.That(reader.ReadBit(), Is.EqualTo(1));
    Assert.That(reader.ReadBit(), Is.EqualTo(0));
    Assert.That(reader.ReadBit(), Is.EqualTo(1));
    Assert.That(reader.ReadBit(), Is.EqualTo(1));
    Assert.That(reader.ReadBit(), Is.EqualTo(0));
    Assert.That(reader.ReadBit(), Is.EqualTo(1));
  }

  [Test]
  public void ReadBit_MsbFirst_ReadsMostSignificantBitFirst() {
    // 0b10110100 = 0xB4
    var stream = new MemoryStream([0xB4]);
    var reader = new BitReader(stream, BitOrder.MsbFirst);

    // MSB first: bits come out 1,0,1,1,0,1,0,0
    Assert.That(reader.ReadBit(), Is.EqualTo(1));
    Assert.That(reader.ReadBit(), Is.EqualTo(0));
    Assert.That(reader.ReadBit(), Is.EqualTo(1));
    Assert.That(reader.ReadBit(), Is.EqualTo(1));
    Assert.That(reader.ReadBit(), Is.EqualTo(0));
    Assert.That(reader.ReadBit(), Is.EqualTo(1));
    Assert.That(reader.ReadBit(), Is.EqualTo(0));
    Assert.That(reader.ReadBit(), Is.EqualTo(0));
  }

  [Test]
  public void ReadBits_LsbFirst_ReadsMultipleBits() {
    // 0b11010010 = 0xD2
    var stream = new MemoryStream([0xD2]);
    var reader = new BitReader(stream, BitOrder.LsbFirst);

    // Read 4 bits LSB first: bits 0,1,0,0 (from 0xD2 = 11010010)
    // In LSB-first, we read bit0=0, bit1=1, bit2=0, bit3=0 → value = 0b0010 = 2
    uint value = reader.ReadBits(4);
    Assert.That(value, Is.EqualTo(2u)); // lower nibble of 0xD2

    // Read next 4 bits: bit4=1, bit5=1, bit6=0, bit7=1 → value = 0b1011 = 0xD = 13
    value = reader.ReadBits(4);
    Assert.That(value, Is.EqualTo(13u)); // upper nibble of 0xD2
  }

  [Test]
  public void ReadBits_MsbFirst_ReadsMultipleBits() {
    // 0b11010010 = 0xD2
    var stream = new MemoryStream([0xD2]);
    var reader = new BitReader(stream, BitOrder.MsbFirst);

    // Read 4 bits MSB first: 1,1,0,1 → value = 0b1101 = 13
    uint value = reader.ReadBits(4);
    Assert.That(value, Is.EqualTo(13u));

    // Read next 4 bits: 0,0,1,0 → value = 0b0010 = 2
    value = reader.ReadBits(4);
    Assert.That(value, Is.EqualTo(2u));
  }

  [Test]
  public void ReadBit_ThrowsEndOfStreamException_WhenStreamEnds() {
    var stream = new MemoryStream([]);
    var reader = new BitReader(stream);

    Assert.Throws<EndOfStreamException>(() => reader.ReadBit());
  }

  [Test]
  public void ReadBits_CrossesByteBoundary() {
    var stream = new MemoryStream([0xFF, 0x00]);
    var reader = new BitReader(stream, BitOrder.LsbFirst);

    reader.ReadBits(4); // read 4 ones = 0xF
    uint value = reader.ReadBits(8); // read across boundary: 4 ones + 4 zeros = 0x0F
    Assert.That(value, Is.EqualTo(0x0Fu));
  }

  [Test]
  public void AlignToByte_DiscardsRemainingBits() {
    var stream = new MemoryStream([0xFF, 0xAB]);
    var reader = new BitReader(stream, BitOrder.LsbFirst);

    reader.ReadBits(3); // Read 3 bits
    reader.AlignToByte(); // Discard remaining 5 bits

    // Next read should come from the second byte
    uint value = reader.ReadBits(8);
    Assert.That(value, Is.EqualTo(0xABu));
  }

  [TestCase(0)]
  [TestCase(33)]
  public void ReadBits_ThrowsForInvalidCount(int count) {
    var stream = new MemoryStream([0xFF]);
    var reader = new BitReader(stream);

    Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadBits(count));
  }
}
