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

  // ---- ARM ----

  [Test]
  public void EncodeArm_DecodeArm_RoundTrip_Random() {
    var data = new byte[1024];
    new Random(42).NextBytes(data);
    var encoded = BcjFilter.EncodeArm(data);
    var decoded = BcjFilter.DecodeArm(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeArm_DecodeArm_RoundTrip_SimulatedBL() {
    var data = new byte[16];
    // ARM BL instruction at offset 0: opcode byte 0xEB, 24-bit offset = 0x000010
    data[0] = 0x10; data[1] = 0x00; data[2] = 0x00; data[3] = 0xEB;
    var encoded = BcjFilter.EncodeArm(data);
    var decoded = BcjFilter.DecodeArm(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeArm_NoBlInstructions_DataUnchanged() {
    var data = new byte[] { 0x00, 0x00, 0x00, 0xEA, 0x01, 0x02, 0x03, 0x04 }; // 0xEA not 0xEB
    var encoded = BcjFilter.EncodeArm(data);
    Assert.That(encoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeArm_DecodeArm_Empty() {
    var encoded = BcjFilter.EncodeArm(ReadOnlySpan<byte>.Empty);
    Assert.That(encoded, Is.Empty);
  }

  [Test]
  public void EncodeArm_DecodeArm_RoundTrip_WithStartOffset() {
    var data = new byte[256];
    new Random(77).NextBytes(data);
    var encoded = BcjFilter.EncodeArm(data, startOffset: 0x8000);
    var decoded = BcjFilter.DecodeArm(encoded, startOffset: 0x8000);
    Assert.That(decoded, Is.EqualTo(data));
  }

  // ---- ARM Thumb ----

  [Test]
  public void EncodeArmThumb_DecodeArmThumb_RoundTrip_Random() {
    var data = new byte[1024];
    new Random(42).NextBytes(data);
    var encoded = BcjFilter.EncodeArmThumb(data);
    var decoded = BcjFilter.DecodeArmThumb(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeArmThumb_DecodeArmThumb_RoundTrip_SimulatedBL() {
    var data = new byte[8];
    // Thumb BL: halfword1 = 0xF000 + imm10, halfword2 = 0xF800 + imm11
    data[0] = 0x10; data[1] = 0xF0; // 0xF010 → imm10 = 0x010
    data[2] = 0x20; data[3] = 0xF8; // 0xF820 → imm11 = 0x020
    var encoded = BcjFilter.EncodeArmThumb(data);
    var decoded = BcjFilter.DecodeArmThumb(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeArmThumb_NoBlInstructions_DataUnchanged() {
    var data = new byte[] { 0x00, 0xE0, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
    var encoded = BcjFilter.EncodeArmThumb(data);
    Assert.That(encoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeArmThumb_DecodeArmThumb_WithStartOffset() {
    var data = new byte[256];
    new Random(88).NextBytes(data);
    var encoded = BcjFilter.EncodeArmThumb(data, startOffset: 0x1000);
    var decoded = BcjFilter.DecodeArmThumb(encoded, startOffset: 0x1000);
    Assert.That(decoded, Is.EqualTo(data));
  }

  // ---- PowerPC ----

  [Test]
  public void EncodePowerPC_DecodePowerPC_RoundTrip_Random() {
    var data = new byte[1024];
    new Random(42).NextBytes(data);
    var encoded = BcjFilter.EncodePowerPC(data);
    var decoded = BcjFilter.DecodePowerPC(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodePowerPC_DecodePowerPC_RoundTrip_SimulatedBL() {
    var data = new byte[8];
    // PPC BL: opcode 18 (010010), AA=0, LK=1 → (instr & 0xFC000003) == 0x48000001
    // BL +0x100: 0x48000101
    data[0] = 0x48; data[1] = 0x00; data[2] = 0x01; data[3] = 0x01;
    var encoded = BcjFilter.EncodePowerPC(data);
    var decoded = BcjFilter.DecodePowerPC(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodePowerPC_NoBranchInstructions_DataUnchanged() {
    // 0x60000000 = nop (ori r0,r0,0)
    var data = new byte[] { 0x60, 0x00, 0x00, 0x00, 0x60, 0x00, 0x00, 0x00 };
    var encoded = BcjFilter.EncodePowerPC(data);
    Assert.That(encoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodePowerPC_DecodePowerPC_WithStartOffset() {
    var data = new byte[256];
    new Random(99).NextBytes(data);
    var encoded = BcjFilter.EncodePowerPC(data, startOffset: 0x10000);
    var decoded = BcjFilter.DecodePowerPC(encoded, startOffset: 0x10000);
    Assert.That(decoded, Is.EqualTo(data));
  }

  // ---- SPARC ----

  [Test]
  public void EncodeSparc_DecodeSparc_RoundTrip_Random() {
    var data = new byte[1024];
    new Random(42).NextBytes(data);
    var encoded = BcjFilter.EncodeSparc(data);
    var decoded = BcjFilter.DecodeSparc(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeSparc_DecodeSparc_RoundTrip_SimulatedCall() {
    var data = new byte[8];
    // SPARC CALL: bits 31-30 = 01, 30-bit displacement
    // CALL +0x100 → 0x40000040 (displacement = 0x40 words = 0x100 bytes)
    data[0] = 0x40; data[1] = 0x00; data[2] = 0x00; data[3] = 0x40;
    var encoded = BcjFilter.EncodeSparc(data);
    var decoded = BcjFilter.DecodeSparc(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeSparc_NoCallInstructions_DataUnchanged() {
    // 0x01000000 = nop (sethi %hi(0), %g0) → bits 31-30 = 00, not CALL
    var data = new byte[] { 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };
    var encoded = BcjFilter.EncodeSparc(data);
    Assert.That(encoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeSparc_DecodeSparc_WithStartOffset() {
    var data = new byte[256];
    new Random(55).NextBytes(data);
    var encoded = BcjFilter.EncodeSparc(data, startOffset: 0x20000);
    var decoded = BcjFilter.DecodeSparc(encoded, startOffset: 0x20000);
    Assert.That(decoded, Is.EqualTo(data));
  }

  // ---- IA-64 ----

  [Test]
  public void EncodeIA64_DecodeIA64_RoundTrip_Random() {
    var data = new byte[1024];
    new Random(42).NextBytes(data);
    var encoded = BcjFilter.EncodeIA64(data);
    var decoded = BcjFilter.DecodeIA64(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeIA64_DecodeIA64_RoundTrip_SimulatedBranch() {
    // Build a 16-byte bundle with template 0x16 (BBB — all 3 slots are B-type)
    var data = new byte[16];
    data[0] = 0x16; // template byte (low 5 bits = 0x16)

    // Place a branch instruction (opcode 4) in slot 0 (bits 5..45)
    // Opcode 4 = 0100 in bits 37-40 of the 41-bit instruction
    // bit39 of slot0 = bundle bit 44 = byte 5, bit 4
    data[5] |= 0x10; // opcode bit 39 = 1 → opcode = 0100 = 4

    // Set imm20b to a small value: slot bits 13-32 → bundle bits 18-37
    // Set bundle bit 18 (byte 2, bit 2) → imm20b bit 0 = 1
    data[2] |= 0x04; // imm20b = 1

    // Use a non-zero startOffset so the transform actually changes the data
    // (with offset 0 and pos 0, the target round-trips to the same imm20b)
    var encoded = BcjFilter.EncodeIA64(data, startOffset: 0x1000);
    Assert.That(encoded, Is.Not.EqualTo(data));

    var decoded = BcjFilter.DecodeIA64(encoded, startOffset: 0x1000);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeIA64_NoTransform_DataUnchanged() {
    // Template 0x00 = MII → no branch slots (mask = 0)
    var data = new byte[32];
    data[0] = 0x00; // template MII
    data[16] = 0x08; // template MMI
    // Fill remaining bytes with non-zero data
    for (int i = 1; i < 16; ++i) data[i] = (byte)(i * 3);
    for (int i = 17; i < 32; ++i) data[i] = (byte)(i * 7);

    var encoded = BcjFilter.EncodeIA64(data);
    Assert.That(encoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeIA64_DecodeIA64_Empty() {
    var encoded = BcjFilter.EncodeIA64(ReadOnlySpan<byte>.Empty);
    Assert.That(encoded, Is.Empty);
  }

  [Test]
  public void EncodeIA64_DecodeIA64_WithStartOffset() {
    var data = new byte[1024];
    new Random(77).NextBytes(data);
    var encoded = BcjFilter.EncodeIA64(data, startOffset: 0x4000);
    var decoded = BcjFilter.DecodeIA64(encoded, startOffset: 0x4000);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void EncodeIA64_DecodeIA64_ShortData() {
    // Data shorter than 16 bytes — no bundles to process
    var data = new byte[] { 0x16, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
    var encoded = BcjFilter.EncodeIA64(data);
    Assert.That(encoded, Is.EqualTo(data));

    var decoded = BcjFilter.DecodeIA64(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }
}
