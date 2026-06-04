using Codec.SolDpcm;

namespace Compression.Tests.Codecs.SolDpcm;

[TestFixture]
public class SolDpcmCodecTests {

  // ──────────── 8-bit DPCM table walk (hand-computed) ────────────

  // table = {0,1,2,3,6,0xA,0xF,0x15}. accumulator starts at 0x80 (128).
  // sample surfaced as (acc-128)<<8. Low nibble first.
  [Test]
  public void DecodeOld8_TableWalk_AddsAndSubtractsMagnitudes() {
    // byte 0x4C → low nibble 0x4 (mag idx4=6, +) → 134; high nibble 0xC (mag idx4=6, -) → 128.
    var output = SolDpcmCodec.Decode([0x4C], SolDpcmCodec.Mode.Old8);
    Assert.That(output.Length, Is.EqualTo(2));
    Assert.That(output[0], Is.EqualTo((short)((134 - 128) << 8)));
    Assert.That(output[1], Is.EqualTo((short)((128 - 128) << 8)));
  }

  [Test]
  public void DecodeNew8_TableWalk() {
    // byte 0x07 → low nibble 7 (mag idx7=0x15, +) → 128+21=149; high nibble 0 (mag 0) → 149.
    var output = SolDpcmCodec.Decode([0x07], SolDpcmCodec.Mode.New8);
    Assert.That(output[0], Is.EqualTo((short)((149 - 128) << 8)));
    Assert.That(output[1], Is.EqualTo((short)((149 - 128) << 8)));
  }

  [Test]
  public void DecodeOld8_AccumulatorWrapsAsByte() {
    // Underflow below 0 wraps modulo 256: start 128, subtract 0x15 (21) nine times via
    // repeated high-magnitude negative nibbles to push past zero.
    // byte 0x0F → low nibble 0xF (mag idx7=0x15=21, -) → 128-21=107; high nibble 0 → 107.
    var output = SolDpcmCodec.Decode([0x0F], SolDpcmCodec.Mode.Old8);
    Assert.That(output[0], Is.EqualTo((short)((107 - 128) << 8)));
  }

  // ──────────── 16-bit integrate mode ────────────

  [Test]
  public void DecodeSixteen_IntegratesSignedByteSteps() {
    // SolTable16[1] = 8, [2] = 16. byte 0x01 → +8; byte 0x82 → -(SolTable16[2])=-16.
    var output = SolDpcmCodec.Decode([0x01, 0x82], SolDpcmCodec.Mode.Sixteen);
    Assert.That(output.Length, Is.EqualTo(2)); // one sample per byte in 16-bit mode
    Assert.That(output[0], Is.EqualTo(8));
    Assert.That(output[1], Is.EqualTo(8 - 16));
  }

  // ──────────── Raw 8-bit PCM ────────────

  [Test]
  public void DecodePcm8_MapsUnsignedToSigned16() {
    var output = SolDpcmCodec.DecodePcm8([128, 255, 0]);
    Assert.That(output[0], Is.EqualTo(0));
    Assert.That(output[1], Is.EqualTo((short)((255 - 128) << 8)));
    Assert.That(output[2], Is.EqualTo((short)((0 - 128) << 8)));
  }
}
