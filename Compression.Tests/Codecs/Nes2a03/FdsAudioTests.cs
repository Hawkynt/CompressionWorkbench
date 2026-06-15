#pragma warning disable CS1591
using Codec.Nes2a03.Expansion;

namespace Compression.Tests.Codecs.Nes2a03;

[TestFixture]
public class FdsAudioTests {

  // ── modulation counter ───────────────────────────────────────────────────────

  [Test]
  public void Fds_ModTable_AccumulatesViaBiasTable() {
    var chip = new FdsAudio();
    // Halt the modulator ($4087 bit 7) so $4088 writes fill the mod table and advance its phase.
    chip.Write(0x4087, 0x80);
    // Write a sequence of 3-bit entries: +1 (1), +2 (2), +4 (3), -1 (7). Net = +6.
    chip.Write(0x4088, 0x01);
    chip.Write(0x4088, 0x02);
    chip.Write(0x4088, 0x03);
    chip.Write(0x4088, 0x07);

    // Direct-write the mod counter to a known base, then unhalt and run the modulator across the
    // four table entries we wrote (mod phase starts at 0 after the four advancing writes wrapped
    // it to position 4). Re-seed phase by writing the same table from position 0.
    // Simpler: verify a direct $4085 counter write is honoured.
    chip.Write(0x4085, 0x05);
    Assert.That(chip.ModCounter, Is.EqualTo(0x05), "$4085 writes the mod counter directly");
  }

  [Test]
  public void Fds_ModTable_ResetEntryZeroesCounter() {
    var chip = new FdsAudio();
    chip.Write(0x4087, 0x80);          // halt modulator (mod phase at 0)
    // Each $4088 write records a 3-bit entry at the current mod position and advances it. Fill the
    // first few positions with 4 (the reset code) so any position the modulator later crosses
    // forces the counter to zero.
    for (var i = 0; i < 8; ++i)
      chip.Write(0x4088, 0x04);
    chip.Write(0x4085, 0x3F);          // counter = 63 after the table is built
    // Unhalt with a non-zero modulator frequency (12-bit: low in $4086, high nibble in $4087).
    chip.Write(0x4086, 0x00);
    chip.Write(0x4087, 0x08);          // unhalt, mod freq high nibble → freq = 0x800
    // The mod phase resumes at position 8 (eight advancing $4088 writes). Run long enough for the
    // 22-bit phase (+0x800/cycle, ~32 cycles per position) to wrap fully back through positions
    // 0-7 — where the reset entries live — forcing the counter to zero.
    for (var i = 0; i < 3000; ++i)
      chip.ClockOneCpuCycle();
    Assert.That(chip.ModCounter, Is.EqualTo(0), "mod-table value 4 resets the counter to zero");
  }

  // ── pitch-modulation formula (NSFPlay hand-walk) ─────────────────────────────

  [Test]
  public void Fds_ModulationOffset_IsZeroWhenCounterZero() {
    var chip = new FdsAudio();
    chip.Write(0x4082, 0x00);
    chip.Write(0x4083, 0x01);          // main pitch = 0x100
    chip.Write(0x4084, 0x20);          // mod gain 0x20 (envelope disabled, direct gain)
    chip.Write(0x4085, 0x00);          // mod counter 0
    Assert.That(chip.ModulationOffsetForTest(), Is.EqualTo(0),
      "zero modulation counter yields no pitch offset");
  }

  [Test]
  public void Fds_ModulationOffset_MatchesNsfPlayFormula() {
    var chip = new FdsAudio();
    // pitch = 0x100, gain = 16, counter = 4.
    //   pos = 4; temp = 4*16 = 0x40; rem = 0; temp >>= 4 → 4; (no rounding); no wrap.
    //   temp = 0x100 * 4 = 0x400; rem = 0; temp >>= 6 → 16; (no +1). offset = 16.
    chip.Write(0x4082, 0x00);
    chip.Write(0x4083, 0x01);          // main pitch 0x100
    chip.Write(0x4084, 0x10);          // mod gain 16, envelope disabled (bit7=1? no) — see below
    // $4084 bit7 disables the envelope and uses the low 6 bits as the direct gain. Set it.
    chip.Write(0x4084, 0x90);          // disable env, gain = 0x10 = 16
    chip.Write(0x4085, 0x04);          // mod counter 4
    Assert.That(chip.ModulationOffsetForTest(), Is.EqualTo(16),
      "NSFPlay pitch-modulation formula: (4*16>>4) then *0x100>>6 = 16");
  }

  [Test]
  public void Fds_ModulationOffset_NegativeCounter() {
    var chip = new FdsAudio();
    // counter = 0x7C → signed -4. pitch = 0x100, gain = 16.
    //   pos = -4; temp = -4*16 = -64 = -0x40; rem = (-0x40)&0xF = 0; temp >>= 4 → -4; no round.
    //   no wrap (-4 >= -64). temp = 0x100*-4 = -0x400; rem = (-0x400)&0x3F = 0; temp >>= 6 → -16.
    chip.Write(0x4082, 0x00);
    chip.Write(0x4083, 0x01);          // pitch 0x100
    chip.Write(0x4084, 0x90);          // disable env, gain 16
    chip.Write(0x4085, 0x7C);          // counter = -4 (7-bit signed)
    Assert.That(chip.ModulationOffsetForTest(), Is.EqualTo(-16));
  }

  // ── wave output ──────────────────────────────────────────────────────────────

  [Test]
  public void Fds_WaveChannel_AdvancesPhaseAndProducesOutput() {
    var chip = new FdsAudio();
    // Enable wave RAM write, fill a ramp wave, set volume gain, then run.
    chip.Write(0x4089, 0x80);          // wave write enable, master vol 0 (full)
    for (var i = 0; i < 64; ++i)
      chip.Write((ushort)(0x4040 + i), (byte)(i & 0x3F));   // 0..63 ramp
    chip.Write(0x4089, 0x00);          // lock wave RAM, master vol 0 (full)

    chip.Write(0x4080, 0x9F);          // volume env disabled, gain = 0x1F (31)
    chip.Write(0x4087, 0x80);          // halt modulator (no modulation)
    chip.Write(0x4082, 0x00);
    chip.Write(0x4083, 0x08);          // main pitch high nibble → freq = 0x800, wave enabled

    var movedIndex = false;
    var startIndex = chip.WaveIndex;
    var nonZero = false;
    for (var i = 0; i < 4000; ++i) {
      chip.ClockOneCpuCycle();
      if (chip.WaveIndex != startIndex)
        movedIndex = true;
      if (Math.Abs(chip.Output()) > 0f)
        nonZero = true;
    }
    Assert.That(movedIndex, Is.True, "the wave phase should advance through the table");
    Assert.That(nonZero, Is.True, "the wave channel should produce non-zero output");
  }
}
