#pragma warning disable CS1591
using Codec.Nes2a03.Expansion;

namespace Compression.Tests.Codecs.Nes2a03;

/// <summary>
/// Register-level expectations for the FDS expansion sound, derived from
/// docs/FDS-EXPANSION-SOUND-SPEC.md and the NESdev wiki page "FDS audio".
/// </summary>
[TestFixture]
public class FdsAudioTests {

  private const float FullScale = 0.55f;   // peak 63 × 32 at master volume 0 (spec §6)

  private static void Clock(FdsAudio chip, int cycles) {
    for (var i = 0; i < cycles; ++i)
      chip.ClockOneCpuCycle();
  }

  private static void LoadWave(FdsAudio chip, Func<int, int> sample) {
    chip.Write(0x4089, 0x80);
    for (var i = 0; i < 64; ++i)
      chip.Write((ushort)(0x4040 + i), (byte)sample(i));
    chip.Write(0x4089, 0x00);
  }

  private static byte ReadWave(FdsAudio chip, int index) {
    Assert.That(chip.TryRead((ushort)(0x4040 + index), out var value), Is.True);
    return value;
  }

  /// <summary>Halts the modulator, fills all 32 table slots with <paramref name="entry"/>.</summary>
  private static void FillModTable(FdsAudio chip, int entry) {
    chip.Write(0x4087, 0x80);
    for (var i = 0; i < 32; ++i)
      chip.Write(0x4088, (byte)entry);
  }

  // ── register window ─────────────────────────────────────────────────────────

  [TestCase((ushort)0x403F, false)]
  [TestCase((ushort)0x4040, true)]
  [TestCase((ushort)0x407F, true)]
  [TestCase((ushort)0x4080, true)]
  [TestCase((ushort)0x408A, true)]
  [TestCase((ushort)0x408B, false)]
  [TestCase((ushort)0x4023, false)]
  public void GivenAddress_WhenAskedForTheWriteWindow_ThenOnlyFortyFortyToFortyEightyAIsClaimed(ushort addr, bool expected)
    => Assert.That(new FdsAudio().HandlesWrite(addr), Is.EqualTo(expected));

  [TestCase((ushort)0x403F)]
  [TestCase((ushort)0x4080)]
  [TestCase((ushort)0x4090)]
  public void GivenAddressOutsideWaveRam_WhenRead_ThenUnhandled(ushort addr)
    => Assert.That(new FdsAudio().TryRead(addr, out _), Is.False);

  // ── wave RAM ────────────────────────────────────────────────────────────────

  [Test]
  public void GivenWriteProtectedRam_WhenWaveRamWritten_ThenWriteIsIgnored() {
    var chip = new FdsAudio();
    chip.Write(0x4045, 0x2A);
    chip.Write(0x4089, 0x80);
    Assert.That(ReadWave(chip, 5) & 0x3F, Is.Zero);
  }

  [Test]
  public void GivenWriteEnabledRam_WhenEntryWritten_ThenItIsStoredAsSixBitsWithOpenBusHighBits() {
    var chip = new FdsAudio();
    chip.Write(0x4089, 0x80);
    chip.Write(0x4040, 0xFF);
    chip.Write(0x407F, 0x15);
    Assert.Multiple(() => {
      Assert.That(ReadWave(chip, 0), Is.EqualTo(0x40 | 0x3F), "7..6 read as 01, value masked to 6 bits");
      Assert.That(ReadWave(chip, 63), Is.EqualTo(0x40 | 0x15));
    });
  }

  [Test]
  public void GivenWriteProtectedRam_WhenAnyEntryRead_ThenTheCurrentWavePositionIsReturned() {
    var chip = new FdsAudio();
    LoadWave(chip, i => i);
    Assert.Multiple(() => {
      Assert.That(ReadWave(chip, 0), Is.EqualTo(0x40));
      Assert.That(ReadWave(chip, 37), Is.EqualTo(0x40), "position 0 regardless of the address");
    });
    chip.Write(0x4082, 0x00);
    chip.Write(0x4083, 0x08);   // pitch 0x800: one position per two ticks
    Clock(chip, 32 * 7);
    Assert.That(chip.WaveIndex, Is.EqualTo(7));
    Assert.That(ReadWave(chip, 0), Is.EqualTo(0x40 | 7));
  }

  // ── wave unit ───────────────────────────────────────────────────────────────

  [TestCase(0x001)]
  [TestCase(0x100)]
  [TestCase(0x7FF)]
  [TestCase(0xFFF)]
  public void GivenUnmodulatedPitch_WhenTicked_ThenAccumulatorGainsSixtyFourTimesPitchEverySixteenCycles(int pitch) {
    var chip = new FdsAudio();
    chip.Write(0x4082, (byte)pitch);
    chip.Write(0x4083, (byte)(pitch >> 8));
    Clock(chip, 15);
    Assert.That(chip.WavePhase, Is.Zero, "no tick before the 16th cycle");
    Clock(chip, 1);
    Assert.That(chip.WavePhase, Is.EqualTo((uint)(pitch * 64)));
    Clock(chip, 16 * 999);
    Assert.That(chip.WavePhase, Is.EqualTo((uint)(pitch * 64 * 1000) & 0xFFFFFF), "24-bit wrap");
  }

  [Test]
  public void GivenZeroPitch_WhenClocked_ThenWaveStaysAtPositionZero() {
    var chip = new FdsAudio();
    Clock(chip, 10_000);
    Assert.That(chip.WavePhase, Is.Zero);
  }

  [Test]
  public void GivenRunningWave_WhenHaltBitWritten_ThenAccumulatorResetsAndOutputIsEntryZero() {
    var chip = new FdsAudio();
    LoadWave(chip, i => i == 0 ? 40 : 10);
    chip.Write(0x4080, 0xA0);   // gain 32
    chip.Write(0x4082, 0x00);
    chip.Write(0x4083, 0x08);
    Clock(chip, 1000);
    Assert.That(chip.WavePhase, Is.Not.Zero);
    chip.Write(0x4083, 0x88);
    Assert.That(chip.WavePhase, Is.Zero);
    Clock(chip, 1000);
    Assert.Multiple(() => {
      Assert.That(chip.WavePhase, Is.Zero, "halted wave does not advance");
      Assert.That(chip.Output(), Is.EqualTo(FullScale * 40 / 63).Within(1e-6));
    });
  }

  // ── pitch modulation formula ────────────────────────────────────────────────

  // The NESdev pseudocode reduces counter × gain to a multiplier centred on 64. Expected values
  // were hand-walked from that pseudocode (e.g. counter 1, gain 1: 1 → +0x20 → 33 → +0x400 →
  // 1057 → >>4 = 66; counter −64, gain 31: −1984 → no rounding → −960 → >>4 = −60 → & 0xFF = 196).
  [TestCase(-64, 0, 64)]
  [TestCase(-64, 1, 60)]
  [TestCase(-64, 15, 4)]
  [TestCase(-64, 16, 0)]
  [TestCase(-64, 31, 196)]
  [TestCase(-64, 32, 192)]
  [TestCase(-64, 63, 68)]
  [TestCase(-63, 16, 1)]
  [TestCase(-63, 63, 73)]
  [TestCase(-1, 1, 63)]
  [TestCase(-1, 31, 62)]
  [TestCase(-1, 63, 60)]
  [TestCase(0, 63, 64)]
  [TestCase(1, 1, 66)]
  [TestCase(1, 16, 65)]
  [TestCase(1, 31, 67)]
  [TestCase(1, 63, 69)]
  [TestCase(63, 1, 69)]
  [TestCase(63, 15, 125)]
  [TestCase(63, 16, 127)]
  [TestCase(63, 32, 190)]
  [TestCase(63, 63, 56)]
  public void GivenCounterAndGain_WhenPitchModulated_ThenIncrementIsPitchTimesMultiplier(int counter, int gain, int multiplier) {
    foreach (var pitch in new[] { 0, 1, 0x7FF, 0xFFF }) {
      var chip = new FdsAudio();
      chip.Write(0x4082, (byte)pitch);
      chip.Write(0x4083, (byte)(pitch >> 8));
      chip.Write(0x4084, (byte)(0x80 | gain));
      chip.Write(0x4085, (byte)(counter & 0x7F));
      Assert.That(chip.ModulatedPitchForTest(), Is.EqualTo(pitch * multiplier), $"pitch {pitch:X3}");
    }
  }

  [Test]
  public void Fds_ModulationOffset_IsZeroWhenCounterZero() {
    var chip = new FdsAudio();
    chip.Write(0x4082, 0x00);
    chip.Write(0x4083, 0x01);          // main pitch = 0x100
    chip.Write(0x4084, 0xA0);          // mod gain 32 (envelope disabled, direct gain)
    chip.Write(0x4085, 0x00);          // mod counter 0
    Assert.That(chip.ModulationOffsetForTest(), Is.EqualTo(0));
  }

  [Test]
  public void Fds_ModulationOffset_PositiveCounter() {
    var chip = new FdsAudio();
    // pitch 0x100, gain 16, counter 4: 64 → +0x400 → >>4 = 68 = 64 + 4 → offset 0x100 × 4 / 64 = 16.
    chip.Write(0x4082, 0x00);
    chip.Write(0x4083, 0x01);
    chip.Write(0x4084, 0x90);
    chip.Write(0x4085, 0x04);
    Assert.That(chip.ModulationOffsetForTest(), Is.EqualTo(16));
  }

  [Test]
  public void Fds_ModulationOffset_NegativeCounter() {
    var chip = new FdsAudio();
    // counter 0x7C = −4, gain 16: −64 → +0x400 → >>4 = 60 = 64 − 4 → offset −16.
    chip.Write(0x4082, 0x00);
    chip.Write(0x4083, 0x01);
    chip.Write(0x4084, 0x90);
    chip.Write(0x4085, 0x7C);
    Assert.That(chip.ModulationOffsetForTest(), Is.EqualTo(-16));
  }

  [Test]
  public void GivenDirectModGainAbove32_WhenWritten_ThenItIsNotCapped() {
    var chip = new FdsAudio();
    chip.Write(0x4084, 0xBF);
    Assert.That(chip.ModGain, Is.EqualTo(63));
  }

  // ── modulation unit ─────────────────────────────────────────────────────────

  [Test]
  public void Fds_ModCounter_DirectWrite() {
    var chip = new FdsAudio();
    chip.Write(0x4085, 0x05);
    Assert.That(chip.ModCounter, Is.EqualTo(5));
    chip.Write(0x4085, 0x40);
    Assert.That(chip.ModCounter, Is.EqualTo(-64), "7-bit two's complement minimum");
    chip.Write(0x4085, 0xBF);
    Assert.That(chip.ModCounter, Is.EqualTo(63), "bit 7 ignored");
  }

  [Test]
  public void GivenHaltedModulator_WhenTableWritten_ThenEachWriteFillsOneSlotAndAdvancesTwoPositions() {
    var chip = new FdsAudio();
    chip.Write(0x4087, 0x80);
    chip.Write(0x4088, 0x01);
    chip.Write(0x4088, 0x0E);   // only 3 bits kept
    Assert.Multiple(() => {
      Assert.That(chip.ModTableEntry(0), Is.EqualTo(1));
      Assert.That(chip.ModTableEntry(1), Is.EqualTo(6));
      Assert.That(chip.ModTablePosition, Is.EqualTo(4), "two writes = two slots = four 6-bit positions");
    });
    for (var i = 0; i < 30; ++i)
      chip.Write(0x4088, 0x03);
    Assert.That(chip.ModTablePosition, Is.Zero, "32 writes return to the starting position");
  }

  [Test]
  public void GivenRunningModulator_WhenTableWritten_ThenWriteIsIgnored() {
    var chip = new FdsAudio();
    chip.Write(0x4087, 0x00);
    chip.Write(0x4088, 0x05);
    Assert.Multiple(() => {
      Assert.That(chip.ModTableEntry(0), Is.Zero);
      Assert.That(chip.ModTablePosition, Is.Zero);
    });
  }

  [Test]
  public void GivenRunningModulator_WhenHaltBitWritten_ThenFractionClearsButPositionIsKept() {
    var chip = new FdsAudio();
    chip.Write(0x4086, 0x23);
    chip.Write(0x4087, 0x01);   // mod pitch 0x123
    Clock(chip, 16 * 100);      // 100 × 0x123 = 0x71AC → position 7, fraction 0x1AC
    Assert.That(chip.ModPhase, Is.EqualTo(0x71AC));
    chip.Write(0x4087, 0x81);
    Assert.That(chip.ModPhase, Is.EqualTo(0x7000));
  }

  // entry → counter after one step from 10
  [TestCase(0, 10)]
  [TestCase(1, 11)]
  [TestCase(2, 12)]
  [TestCase(3, 14)]
  [TestCase(4, 0)]
  [TestCase(5, 6)]
  [TestCase(6, 8)]
  [TestCase(7, 9)]
  public void GivenTableEntry_WhenModulatorSteps_ThenCounterChangesByTheEntryAdjustment(int entry, int expected) {
    var chip = new FdsAudio();
    FillModTable(chip, entry);
    chip.Write(0x4085, 10);
    chip.Write(0x4086, 0x00);
    chip.Write(0x4087, 0x08);   // pitch 0x800: carry on every second tick
    Clock(chip, 31);
    Assert.That(chip.ModCounter, Is.EqualTo(10), "no carry after one tick");
    Clock(chip, 1);
    Assert.That(chip.ModCounter, Is.EqualTo(expected));
  }

  [TestCase(63, 1, -64)]
  [TestCase(-64, 7, 63)]
  [TestCase(62, 3, -62)]
  [TestCase(-63, 5, 61)]
  public void GivenCounterAtLimit_WhenStepped_ThenItWrapsWithinSevenBits(int start, int entry, int expected) {
    var chip = new FdsAudio();
    FillModTable(chip, entry);
    chip.Write(0x4085, (byte)(start & 0x7F));
    chip.Write(0x4086, 0x00);
    chip.Write(0x4087, 0x40);   // running, pitch 0, forced carry: one step per tick
    Clock(chip, 16);
    Assert.That(chip.ModCounter, Is.EqualTo(expected));
  }

  [Test]
  public void GivenOneNonZeroSlot_WhenModulatorPassesIt_ThenTheEntryIsAppliedTwice() {
    var chip = new FdsAudio();
    chip.Write(0x4087, 0x80);
    chip.Write(0x4088, 0x01);
    for (var i = 1; i < 32; ++i)
      chip.Write(0x4088, 0x00);
    chip.Write(0x4085, 0x00);
    chip.Write(0x4086, 0x00);
    chip.Write(0x4087, 0x08);
    Clock(chip, 32);
    Assert.That(chip.ModCounter, Is.EqualTo(1));
    Clock(chip, 32);
    Assert.That(chip.ModCounter, Is.EqualTo(2), "ghost bit: same slot on the next step");
    Clock(chip, 32 * 62);
    Assert.That(chip.ModCounter, Is.EqualTo(2), "remaining slots are zero");
  }

  [Test]
  public void GivenZeroModPitch_WhenClocked_ThenCounterNeverChanges() {
    var chip = new FdsAudio();
    FillModTable(chip, 1);
    chip.Write(0x4085, 0x07);
    chip.Write(0x4087, 0x00);
    Clock(chip, 100_000);
    Assert.That(chip.ModCounter, Is.EqualTo(7));
  }

  [Test]
  public void GivenHaltedModulator_WhenClocked_ThenCounterNeverChanges() {
    var chip = new FdsAudio();
    FillModTable(chip, 1);
    chip.Write(0x4085, 0x07);
    chip.Write(0x4086, 0xFF);
    chip.Write(0x4087, 0x8F);
    Clock(chip, 100_000);
    Assert.That(chip.ModCounter, Is.EqualTo(7));
  }

  [Test]
  public void Fds_ModTable_ResetEntryZeroesCounter() {
    var chip = new FdsAudio();
    chip.Write(0x4087, 0x80);
    for (var i = 0; i < 8; ++i)
      chip.Write(0x4088, 0x04);   // slots 0..7 = reset
    chip.Write(0x4085, 0x3F);
    chip.Write(0x4086, 0x00);
    chip.Write(0x4087, 0x08);     // mod pitch 0x800; one table pass = 2048 cycles
    Clock(chip, 3000);
    Assert.That(chip.ModCounter, Is.EqualTo(0), "mod-table value 4 resets the counter to zero");
  }

  // ── envelopes ───────────────────────────────────────────────────────────────

  [TestCase(0, 1, 16)]
  [TestCase(1, 0xE8, 3728)]
  [TestCase(63, 1, 1024)]
  [TestCase(0, 0xFF, 2048)]
  [TestCase(63, 0xFF, 131072)]
  public void GivenEnvelopeSpeeds_WhenIncreasing_ThenGainStepsEveryEightTimesSpeedPlusOneTimesMasterPlusOne(int speed, int master, int period) {
    foreach (var (envelope, gain) in new (ushort, Func<FdsAudio, int>)[] { (0x4080, c => c.VolumeGain), (0x4084, c => c.ModGain) }) {
      var chip = new FdsAudio();
      chip.Write(0x408A, (byte)master);
      chip.Write(envelope, (byte)(0x40 | speed));
      Clock(chip, period - 1);
      Assert.That(gain(chip), Is.Zero, $"{envelope:X4}: one cycle early");
      Clock(chip, 1);
      Assert.That(gain(chip), Is.EqualTo(1), $"{envelope:X4}: first step");
      Clock(chip, period);
      Assert.That(gain(chip), Is.EqualTo(2), $"{envelope:X4}: second step");
    }
  }

  [Test]
  public void GivenDefaultMasterSpeed_WhenEnvelopeRuns_ThenBiosValueE8IsUsed() {
    var chip = new FdsAudio();
    chip.Write(0x4080, 0x40);
    Clock(chip, 8 * 1 * 233);
    Assert.That(chip.VolumeGain, Is.EqualTo(1));
  }

  [Test]
  public void GivenIncreasingEnvelope_WhenRunLong_ThenGainStopsAt32() {
    var chip = new FdsAudio();
    chip.Write(0x408A, 0x01);
    chip.Write(0x4080, 0x40);
    Clock(chip, 16 * 40);
    Assert.That(chip.VolumeGain, Is.EqualTo(32));
  }

  [Test]
  public void GivenDirectGainAbove32_WhenIncreasing_ThenItHoldsAndWhenDecreasing_ThenItFalls() {
    var chip = new FdsAudio();
    chip.Write(0x408A, 0x01);
    chip.Write(0x4084, 0xBF);   // gain 63
    chip.Write(0x4084, 0x40);   // increase, speed 0
    Clock(chip, 16 * 4);
    Assert.That(chip.ModGain, Is.EqualTo(63), "no increase above 32");
    chip.Write(0x4084, 0x00);   // decrease
    Clock(chip, 16);
    Assert.That(chip.ModGain, Is.EqualTo(62));
  }

  [Test]
  public void GivenDecreasingEnvelope_WhenRunLong_ThenGainStopsAtZero() {
    var chip = new FdsAudio();
    chip.Write(0x408A, 0x01);
    chip.Write(0x4080, 0x85);
    chip.Write(0x4080, 0x00);
    Clock(chip, 16 * 10);
    Assert.That(chip.VolumeGain, Is.Zero);
  }

  [Test]
  public void GivenMasterSpeedZero_WhenClocked_ThenEnvelopesAreFrozen() {
    var chip = new FdsAudio();
    chip.Write(0x408A, 0x00);
    chip.Write(0x4080, 0x40);
    chip.Write(0x4084, 0x40);
    Clock(chip, 100_000);
    Assert.Multiple(() => {
      Assert.That(chip.VolumeGain, Is.Zero);
      Assert.That(chip.ModGain, Is.Zero);
    });
  }

  [Test]
  public void GivenEnvelopeHaltBit_WhenClocked_ThenEnvelopesFreezeUntilReleasedAndTimersRestart() {
    var chip = new FdsAudio();
    chip.Write(0x408A, 0x01);
    chip.Write(0x4080, 0x40);
    Clock(chip, 10);
    chip.Write(0x4083, 0x40);
    Clock(chip, 1000);
    Assert.That(chip.VolumeGain, Is.Zero, "halted");
    chip.Write(0x4083, 0x00);
    Clock(chip, 15);
    Assert.That(chip.VolumeGain, Is.Zero, "timer restarted from a full period");
    Clock(chip, 1);
    Assert.That(chip.VolumeGain, Is.EqualTo(1));
  }

  [Test]
  public void GivenHaltedWave_WhenClocked_ThenEnvelopesDoNotTick() {
    var chip = new FdsAudio();
    chip.Write(0x408A, 0x01);
    chip.Write(0x4083, 0x80);
    chip.Write(0x4080, 0x40);
    Clock(chip, 1000);
    Assert.That(chip.VolumeGain, Is.Zero);
  }

  [Test]
  public void GivenEnvelopeRegisterRewritten_WhenClocked_ThenTimerRestarts() {
    var chip = new FdsAudio();
    chip.Write(0x408A, 0x01);
    chip.Write(0x4080, 0x40);
    Clock(chip, 15);
    chip.Write(0x4080, 0x40);
    Clock(chip, 15);
    Assert.That(chip.VolumeGain, Is.Zero);
    Clock(chip, 1);
    Assert.That(chip.VolumeGain, Is.EqualTo(1));
  }

  [Test]
  public void GivenMasterSpeedRewritten_WhenClocked_ThenBothTimersRestart() {
    var chip = new FdsAudio();
    chip.Write(0x408A, 0x01);
    chip.Write(0x4080, 0x40);
    chip.Write(0x4084, 0x40);
    Clock(chip, 15);
    chip.Write(0x408A, 0x01);
    Clock(chip, 15);
    Assert.That(chip.VolumeGain + chip.ModGain, Is.Zero);
    Clock(chip, 1);
    Assert.Multiple(() => {
      Assert.That(chip.VolumeGain, Is.EqualTo(1));
      Assert.That(chip.ModGain, Is.EqualTo(1));
    });
  }

  // ── output level ────────────────────────────────────────────────────────────

  [TestCase(0, 1f)]
  [TestCase(1, 2f / 3f)]
  [TestCase(2, 2f / 4f)]
  [TestCase(3, 2f / 5f)]
  public void GivenMasterVolume_WhenPeakPlayed_ThenOutputIsScaledByTheMasterFactor(int masterVolume, float factor) {
    var chip = new FdsAudio();
    LoadWave(chip, _ => 63);
    chip.Write(0x4089, (byte)masterVolume);
    chip.Write(0x4080, 0xA0);
    Clock(chip, 16);
    Assert.That(chip.Output(), Is.EqualTo(FullScale * factor).Within(1e-6));
  }

  [Test]
  public void GivenVolumeGainAbove32_WhenOutput_ThenItIsClampedTo32() {
    var chip = new FdsAudio();
    LoadWave(chip, _ => 63);
    chip.Write(0x4080, 0xBF);
    Clock(chip, 16);
    Assert.Multiple(() => {
      Assert.That(chip.LatchedVolume, Is.EqualTo(32));
      Assert.That(chip.Output(), Is.EqualTo(FullScale).Within(1e-6));
    });
  }

  [Test]
  public void GivenSilentWave_WhenOutput_ThenZero() {
    var chip = new FdsAudio();
    LoadWave(chip, _ => 0);
    chip.Write(0x4080, 0xA0);
    Clock(chip, 16);
    Assert.That(chip.Output(), Is.Zero);
  }

  [Test]
  public void GivenWaveAwayFromPositionZero_WhenGainChanges_ThenOutputUsesNewGainOnlyAfterWrap() {
    var chip = new FdsAudio();
    LoadWave(chip, _ => 63);
    chip.Write(0x4080, 0x90);   // gain 16
    chip.Write(0x4082, 0x00);
    chip.Write(0x4083, 0x08);   // 128 ticks per wave cycle
    Clock(chip, 16);
    Assert.That(chip.LatchedVolume, Is.EqualTo(16));
    Clock(chip, 32 * 10);
    chip.Write(0x4080, 0xA0);   // gain 32 at position 10
    Clock(chip, 32 * 40);
    Assert.That(chip.Output(), Is.EqualTo(FullScale / 2).Within(1e-6), "still the old gain mid-cycle");
    Clock(chip, 32 * 20);       // wraps through position 0
    Assert.That(chip.Output(), Is.EqualTo(FullScale).Within(1e-6));
  }

  [Test]
  public void GivenWaveAwayFromPositionZero_WhenGainWrittenZero_ThenMuteIsImmediate() {
    var chip = new FdsAudio();
    LoadWave(chip, _ => 63);
    chip.Write(0x4080, 0xA0);
    chip.Write(0x4083, 0x08);
    Clock(chip, 32 * 10);
    chip.Write(0x4080, 0x80);
    Assert.That(chip.Output(), Is.Zero);
  }

  [Test]
  public void GivenWaveRamWriteEnabled_WhenChannelRuns_ThenOutputHoldsItsLastLevel() {
    var chip = new FdsAudio();
    LoadWave(chip, i => i);
    chip.Write(0x4080, 0xA0);
    chip.Write(0x4083, 0x08);
    Clock(chip, 32 * 20);
    var before = chip.Output();
    Assert.That(before, Is.Not.Zero);
    chip.Write(0x4089, 0x80);
    Clock(chip, 32 * 17);
    Assert.That(chip.Output(), Is.EqualTo(before), "held while write-enabled");
    Assert.That(chip.WaveIndex, Is.EqualTo(37), "the wave unit keeps running");
    chip.Write(0x4089, 0x00);
    Assert.That(chip.Output(), Is.EqualTo(FullScale * 37 / 63).Within(1e-6));
  }

  [Test]
  public void Fds_WaveChannel_AdvancesPhaseAndProducesOutput() {
    var chip = new FdsAudio();
    LoadWave(chip, i => i);
    chip.Write(0x4080, 0x9F);          // gain 31
    chip.Write(0x4087, 0x80);          // halt modulator
    chip.Write(0x4082, 0x00);
    chip.Write(0x4083, 0x08);

    var movedIndex = false;
    var nonZero = false;
    for (var i = 0; i < 4000; ++i) {
      chip.ClockOneCpuCycle();
      movedIndex |= chip.WaveIndex != 0;
      nonZero |= chip.Output() > 0f;
    }
    Assert.That(movedIndex, Is.True);
    Assert.That(nonZero, Is.True);
  }
}
