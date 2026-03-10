using Compression.Core.Transforms;

namespace Compression.Tests.Transforms;

[TestFixture]
public class BcjFilterTests {
  [Test]
  public void EncodeX86_DecodeX86_RoundTrip_Random() {
    var data = new byte[1024];
    new Random(42).NextBytes(data);
    var encoded = BcjFilter.EncodeX86(data);
    var decoded = BcjFilter.DecodeX86(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeX86_DecodeX86_RoundTrip_SimulatedCode() {
    // Create data with E8/E9 bytes
    var data = new byte[100];
    data[10] = 0xE8; // CALL
    data[11] = 0x10; data[12] = 0x00; data[13] = 0x00; data[14] = 0x00;
    data[50] = 0xE9; // JMP
    data[51] = 0xFF; data[52] = 0xFF; data[53] = 0xFF; data[54] = 0xFF;

    var encoded = BcjFilter.EncodeX86(data);
    var decoded = BcjFilter.DecodeX86(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeX86_ConvertsCallAddresses() {
    var data = new byte[10];
    data[0] = 0xE8;
    data[1] = 0x05; data[2] = 0x00; data[3] = 0x00; data[4] = 0x00; // relative addr = 5

    var encoded = BcjFilter.EncodeX86(data);
    // At position 0, with startOffset=0: absolute = 5 + (0 + 0 + 5) = 10
    int absAddr = BitConverter.ToInt32(encoded, 1);
    Assert.That(absAddr, Is.EqualTo(10));
  }

  [Test]
  public void EncodeX86_DecodeX86_Empty() {
    var encoded = BcjFilter.EncodeX86(ReadOnlySpan<byte>.Empty);
    var decoded = BcjFilter.DecodeX86(encoded);
    Assert.That(decoded, Is.Empty);
  }

  [Test]
  public void EncodeX86_NoE8E9_DataUnchanged() {
    var data = new byte[] { 0x90, 0x90, 0x90, 0x90, 0xCC, 0xCC, 0xCC, 0xCC };
    var encoded = BcjFilter.EncodeX86(data);
    Assert.That(encoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeX86_DecodeX86_RoundTrip_WithStartOffset() {
    var data = new byte[64];
    new Random(11).NextBytes(data);

    var encoded = BcjFilter.EncodeX86(data, startOffset: 0x401000);
    var decoded = BcjFilter.DecodeX86(encoded, startOffset: 0x401000);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeX86_E8AtEndOfBuffer_NotConverted() {
    // E8 at last position: not enough room for 4-byte address
    var data = new byte[5];
    data[4] = 0xE8;

    var encoded = BcjFilter.EncodeX86(data);
    // E8 at position 4 with length 5: i + 4 = 8, which is > 5, so not converted
    Assert.That(encoded[4], Is.EqualTo(0xE8));
  }

  [Test]
  public void EncodeX86_DecodeX86_ConsecutiveE8() {
    // Two CALL instructions back to back
    var data = new byte[15];
    data[0] = 0xE8;
    data[1] = 0x0A; data[2] = 0x00; data[3] = 0x00; data[4] = 0x00; // CALL +10
    data[5] = 0xE8;
    data[6] = 0x14; data[7] = 0x00; data[8] = 0x00; data[9] = 0x00; // CALL +20

    var encoded = BcjFilter.EncodeX86(data);
    var decoded = BcjFilter.DecodeX86(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeX86_E9_ConvertsJmpAddresses() {
    var data = new byte[10];
    data[0] = 0xE9;
    data[1] = 0x0A; data[2] = 0x00; data[3] = 0x00; data[4] = 0x00; // JMP +10

    var encoded = BcjFilter.EncodeX86(data);
    int absAddr = BitConverter.ToInt32(encoded, 1);
    // absolute = 10 + (0 + 0 + 5) = 15
    Assert.That(absAddr, Is.EqualTo(15));
  }

  [Test]
  public void EncodeX86_DecodeX86_RoundTrip_LargeRandom() {
    var data = new byte[8192];
    new Random(99).NextBytes(data);

    var encoded = BcjFilter.EncodeX86(data);
    var decoded = BcjFilter.DecodeX86(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeX86_NegativeRelativeAddress() {
    // Backward call: relative address = -10
    var data = new byte[10];
    data[0] = 0xE8;
    BitConverter.GetBytes(-10).CopyTo(data, 1);

    var encoded = BcjFilter.EncodeX86(data);
    int absAddr = BitConverter.ToInt32(encoded, 1);
    // absolute = -10 + (0 + 0 + 5) = -5
    Assert.That(absAddr, Is.EqualTo(-5));

    var decoded = BcjFilter.DecodeX86(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }
}
