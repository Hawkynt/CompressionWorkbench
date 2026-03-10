using Compression.Core.BitIO;

namespace Compression.Tests.BitIO;

[TestFixture]
public class BitWriterTests {
  [Test]
  public void WriteBit_LsbFirst_WritesLeastSignificantBitFirst() {
    var stream = new MemoryStream();
    var writer = new BitWriter(stream, BitOrder.LsbFirst);

    // Write bits: 0,0,1,0,1,1,0,1 → should produce 0b10110100 = 0xB4
    writer.WriteBit(0);
    writer.WriteBit(0);
    writer.WriteBit(1);
    writer.WriteBit(0);
    writer.WriteBit(1);
    writer.WriteBit(1);
    writer.WriteBit(0);
    writer.WriteBit(1);

    Assert.That(stream.ToArray(), Is.EqualTo(new byte[] { 0xB4 }));
  }

  [Test]
  public void WriteBit_MsbFirst_WritesMostSignificantBitFirst() {
    var stream = new MemoryStream();
    var writer = new BitWriter(stream, BitOrder.MsbFirst);

    // Write bits: 1,0,1,1,0,1,0,0 → should produce 0b10110100 = 0xB4
    writer.WriteBit(1);
    writer.WriteBit(0);
    writer.WriteBit(1);
    writer.WriteBit(1);
    writer.WriteBit(0);
    writer.WriteBit(1);
    writer.WriteBit(0);
    writer.WriteBit(0);

    Assert.That(stream.ToArray(), Is.EqualTo(new byte[] { 0xB4 }));
  }

  [Test]
  public void WriteBits_LsbFirst_WritesMultipleBits() {
    var stream = new MemoryStream();
    var writer = new BitWriter(stream, BitOrder.LsbFirst);

    writer.WriteBits(0x0F, 4); // Write 4 ones (LSB first)
    writer.WriteBits(0x00, 4); // Write 4 zeros
    // Expected: bits 1,1,1,1,0,0,0,0 → 0x0F in LSB order

    Assert.That(stream.ToArray(), Is.EqualTo(new byte[] { 0x0F }));
  }

  [Test]
  public void WriteBits_MsbFirst_WritesMultipleBits() {
    var stream = new MemoryStream();
    var writer = new BitWriter(stream, BitOrder.MsbFirst);

    writer.WriteBits(0x0D, 4); // Write 1101 (MSB first)
    writer.WriteBits(0x02, 4); // Write 0010
    // Expected: 11010010 = 0xD2

    Assert.That(stream.ToArray(), Is.EqualTo(new byte[] { 0xD2 }));
  }

  [Test]
  public void FlushBits_PadsWithZeros() {
    var stream = new MemoryStream();
    var writer = new BitWriter(stream, BitOrder.LsbFirst);

    writer.WriteBit(1);
    writer.WriteBit(1);
    writer.WriteBit(1);
    writer.FlushBits();

    // 3 ones + 5 zeros = 0b00000111 = 0x07
    Assert.That(stream.ToArray(), Is.EqualTo(new byte[] { 0x07 }));
  }

  [Test]
  public void FlushBits_NoOpWhenAligned() {
    var stream = new MemoryStream();
    var writer = new BitWriter(stream, BitOrder.LsbFirst);

    writer.WriteBits(0xFF, 8);
    writer.FlushBits(); // Should not write anything extra

    Assert.That(stream.ToArray(), Is.EqualTo(new byte[] { 0xFF }));
  }

  [Test]
  public void RoundTrip_LsbFirst() {
    var stream = new MemoryStream();
    var writer = new BitWriter(stream, BitOrder.LsbFirst);

    writer.WriteBits(42, 7);
    writer.WriteBits(123, 8);
    writer.WriteBits(3, 2);
    writer.FlushBits();

    stream.Position = 0;
    var reader = new BitReader(stream, BitOrder.LsbFirst);

    Assert.That(reader.ReadBits(7), Is.EqualTo(42u));
    Assert.That(reader.ReadBits(8), Is.EqualTo(123u));
    Assert.That(reader.ReadBits(2), Is.EqualTo(3u));
  }

  [Test]
  public void RoundTrip_MsbFirst() {
    var stream = new MemoryStream();
    var writer = new BitWriter(stream, BitOrder.MsbFirst);

    writer.WriteBits(42, 7);
    writer.WriteBits(123, 8);
    writer.WriteBits(3, 2);
    writer.FlushBits();

    stream.Position = 0;
    var reader = new BitReader(stream, BitOrder.MsbFirst);

    Assert.That(reader.ReadBits(7), Is.EqualTo(42u));
    Assert.That(reader.ReadBits(8), Is.EqualTo(123u));
    Assert.That(reader.ReadBits(2), Is.EqualTo(3u));
  }
}
