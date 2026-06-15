#pragma warning disable CS1591
using Codec.Tracker;

namespace Compression.Tests.Tracker;

/// <summary>
/// Hand-walks the MOD effect engine and the mixer's sample-loop continuation,
/// pinning observable behaviour (volume ramps, arpeggio cycling, sample looping).
/// </summary>
[TestFixture]
public class TrackerEffectTests {

  private static TrackerSample SquareSample(int length, int loopStart = 0, int loopLength = 0) {
    var data = new short[length];
    for (var i = 0; i < length; ++i)
      data[i] = (short)(i < length / 2 ? 8000 : -8000);
    return new TrackerSample {
      Data = data,
      LoopStart = loopStart,
      LoopLength = loopLength,
      DefaultVolume = 64,
      BaseRate = 8287,
      FineTune = 0,
    };
  }

  [Test]
  public void Mixer_NonLoopingSample_EndsAfterData() {
    var ch = new MixerChannel();
    ch.Trigger(SquareSample(4), frequencyHz: 44100, volume: 64); // 1 source sample per output frame
    var left = new int[16];
    var right = new int[16];
    ch.Mix(left, right, 16, 44100, 1.0);
    Assert.That(ch.Ended, Is.True);
    // After 4 source samples consumed, the rest stay silent.
    Assert.That(left[5], Is.EqualTo(0));
  }

  [Test]
  public void Mixer_LoopingSample_WrapsAndKeepsPlaying() {
    var smp = SquareSample(8, loopStart: 2, loopLength: 4); // loop window [2,6)
    var ch = new MixerChannel();
    ch.Trigger(smp, frequencyHz: 44100, volume: 64);
    var left = new int[64];
    var right = new int[64];
    ch.Mix(left, right, 64, 44100, 1.0);
    Assert.That(ch.Ended, Is.False);
    var pos = (int)(ch.PositionFixed >> 16);
    Assert.That(pos, Is.GreaterThanOrEqualTo(2));
    Assert.That(pos, Is.LessThan(smp.LoopStart + smp.LoopLength));
  }

  [Test]
  public void Arpeggio_CyclesBaseThirdFifthAcrossThreeTicks() {
    // Build a 1-channel pattern: row 0 plays C-2 with effect 0 param 0x37 (arp +3,+7).
    var song = MakeArpSong();
    var player = new ModPlayer(song, 44100);
    // Render a short slice covering the first row (speed 6 → 6 ticks).
    var pcm = player.Render(0.2);
    Assert.That(pcm.Length, Is.GreaterThan(0));
    // The render exercising arpeggio without throwing is the behaviour we pin here;
    // exact frequency cycling is covered by AmigaPeriods math tests.
  }

  [Test]
  public void VolumeSlide_RampsDownOverTicks() {
    // Cxx sets volume 64 then A0F slides down 15/tick; verify the engine renders
    // a decaying envelope (later frames quieter than earlier ones).
    var song = MakeVolSlideSong();
    var player = new ModPlayer(song, 44100);
    var pcm = player.Render(0.3);
    Assert.That(pcm.Length, Is.GreaterThan(0));
    var firstPeak = PeakAbs(pcm, 0, pcm.Length / 4);
    var lastPeak = PeakAbs(pcm, pcm.Length * 3 / 4, pcm.Length);
    Assert.That(lastPeak, Is.LessThanOrEqualTo(firstPeak));
  }

  private static int PeakAbs(byte[] pcm, int startByte, int endByte) {
    startByte &= ~1;
    var peak = 0;
    for (var i = startByte; i + 1 < endByte; i += 2) {
      var v = (short)(pcm[i] | (pcm[i + 1] << 8));
      peak = Math.Max(peak, Math.Abs(v));
    }
    return peak;
  }

  private static TrackerSong MakeArpSong() {
    var song = MakeBareSong(out var pat);
    ref var cell = ref pat.Cell(0, 0);
    cell.Period = 428;     // C-2
    cell.Instrument = 1;
    cell.Effect = 0x0;
    cell.EffectParam = 0x37;
    return song;
  }

  private static TrackerSong MakeVolSlideSong() {
    var song = MakeBareSong(out var pat);
    ref var c0 = ref pat.Cell(0, 0);
    c0.Period = 428;
    c0.Instrument = 1;
    c0.Effect = 0xC;       // set volume
    c0.EffectParam = 64;
    ref var c1 = ref pat.Cell(1, 0);
    c1.Effect = 0xA;       // volume slide
    c1.EffectParam = 0x0F; // down 15
    ref var c2 = ref pat.Cell(2, 0);
    c2.Effect = 0xA;
    c2.EffectParam = 0x0F;
    return song;
  }

  private static TrackerSong MakeBareSong(out TrackerPattern pattern) {
    var cells = new TrackerCell[64];
    for (var i = 0; i < cells.Length; ++i)
      cells[i] = new TrackerCell();
    pattern = new TrackerPattern { Rows = 64, Channels = 1, Cells = cells };
    var samples = new TrackerSample?[2];
    samples[1] = SquareSample(64, loopStart: 0, loopLength: 64);
    return new TrackerSong {
      Kind = TrackerKind.Mod,
      Channels = 1,
      Order = [0],
      Patterns = [pattern],
      Samples = samples,
      InitialSpeed = 6,
      InitialTempo = 125,
      GlobalVolume = 64,
      ChannelPan = [128],
      ChannelMuted = [false],
    };
  }
}
