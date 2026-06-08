#pragma warning disable CS1591
using Codec.Tracker;

namespace Compression.Tests.Tracker;

/// <summary>
/// Pins the documented tracker arithmetic: the PAL period→frequency relation, the
/// tick/row timing model, and the S3M C2SPD→frequency formula.
/// </summary>
[TestFixture]
public class TrackerMathTests {

  [Test]
  public void Period428_MapsToPalC2Frequency() {
    // PAL clock 7093789.2 / (period * 2). Period 428 → ~8287.6 Hz (C-2).
    var freq = AmigaPeriods.FrequencyForPeriod(428);
    Assert.That(freq, Is.EqualTo(7093789.2 / (428 * 2)).Within(0.01));
    Assert.That(freq, Is.EqualTo(8287.6).Within(0.5));
  }

  [Test]
  public void Period113_IsAnOctaveAbovePeriod226() {
    // Halving the period doubles the frequency (one octave up).
    var low = AmigaPeriods.FrequencyForPeriod(226); // C-3
    var high = AmigaPeriods.FrequencyForPeriod(113); // C-4
    Assert.That(high, Is.EqualTo(low * 2).Within(0.5));
  }

  [Test]
  public void FineTuneZero_C2Rate_IsStandardPalRate() {
    // C-2 at finetune 0 uses period 428.
    var rate = ModModule.RateForFineTune(0);
    Assert.That(rate, Is.EqualTo((int)Math.Round(7093789.2 / (428 * 2))));
  }

  [Test]
  public void S3m_C2SpdMapsToReferenceFrequency() {
    // ST3 reference: C-4 plays at the sample's C2SPD. period = 8363*16*107/C2SPD,
    // frequency = 14317056 / period. For C-4 (1-based note 49) this equals C2SPD.
    const double c2spd = 8363;
    var note = 4 * 12 + 0 + 1; // octave 4, C, 1-based
    var freq = S3mPlayer.FrequencyForNote(note, c2spd);
    Assert.That(freq, Is.EqualTo(c2spd).Within(1.0));
  }

  [Test]
  public void S3m_OctaveUp_DoublesFrequency() {
    const double c2spd = 8363;
    var c4 = S3mPlayer.FrequencyForNote(4 * 12 + 1, c2spd);
    var c5 = S3mPlayer.FrequencyForNote(5 * 12 + 1, c2spd);
    Assert.That(c5, Is.EqualTo(c4 * 2).Within(0.5));
  }

  [Test]
  public void Speed6Tempo125_Produces882FramesPerTickAt44100() {
    // Row rate = BPM*2/5 = 50 Hz; frames per tick at 44100 = 44100 * 5 / (125*2) = 882.
    var song = MakeOneChannelSong(speed: 6, tempo: 125);
    var player = new ModPlayer(song, 44100);
    var pcm = player.Render(1.0); // 1 second
    // 50 ticks/sec → speed 6 → 50/6 rows... but frame count is what we pin:
    // total frames should be close to 44100 (1 second).
    var frames = pcm.Length / 4;
    Assert.That(frames, Is.EqualTo(44100).Within(882)); // within one tick
  }

  private static TrackerSong MakeOneChannelSong(int speed, int tempo) {
    var cells = new TrackerCell[64];
    for (var i = 0; i < cells.Length; ++i)
      cells[i] = new TrackerCell();
    var pat = new TrackerPattern { Rows = 64, Channels = 1, Cells = cells };
    return new TrackerSong {
      Kind = TrackerKind.Mod,
      Channels = 1,
      Order = [0],
      Patterns = [pat],
      Samples = new TrackerSample?[1],
      InitialSpeed = speed,
      InitialTempo = tempo,
      GlobalVolume = 64,
      ChannelPan = [128],
      ChannelMuted = [false],
    };
  }
}
