#pragma warning disable CS1591
using Codec.TrackerXmIt;

namespace Compression.Tests.Codecs.TrackerXmIt;

/// <summary>
/// Engine-level IT tests: New Note Action virtual-channel allocation and deterministic
/// song-length traversal, driven by hand-built minimal IT modules.
/// </summary>
[TestFixture]
public class ItPlayerTests {

  [Test]
  public void Nna_Continue_KeepsOldVoiceRinging() {
    // Two notes on the SAME channel on consecutive rows, instrument NNA = continue.
    // After the second note triggers, the first voice must still be active (moved to a
    // background virtual channel), so two voices ring simultaneously.
    var b = new ItModuleBuilder {
      NewNoteAction = 1, // continue
      Rows = [
        [(0, 60, 1, 0, 0)],   // row 0: C-5, instrument 1
        [(0, 64, 1, 0, 0)],   // row 1: E-5, instrument 1
      ],
    };
    var player = ItPlayer.Load(b.Build());

    // Step past row 0 into row 1's trigger (speed 6 → 6 ticks per row; 7 ticks lands in row 1).
    var active = player.ActiveVoicesAfterTicks(7);
    Assert.That(active, Is.EqualTo(2));
  }

  [Test]
  public void Nna_Cut_ReusesSingleVoice() {
    var b = new ItModuleBuilder {
      NewNoteAction = 0, // cut
      Rows = [
        [(0, 60, 1, 0, 0)],
        [(0, 64, 1, 0, 0)],
      ],
    };
    var player = ItPlayer.Load(b.Build());
    var active = player.ActiveVoicesAfterTicks(7);
    Assert.That(active, Is.EqualTo(1));
  }

  [Test]
  public void SongLength_StopsOnOrderLoop() {
    // Single order, single pattern of 1 row with a Bxx jump back to order 0 → infinite loop,
    // caught by revisit detection. Length must be finite and well under the 10-minute cap.
    var b = new ItModuleBuilder {
      InitialSpeed = 6,
      InitialTempo = 125,
      Orders = [0],
      Rows = [
        [(0, 60, 1, 2 /*Bxx*/, 0)], // jump to order 0
      ],
    };
    var player = ItPlayer.Load(b.Build());
    var seconds = player.EstimateSeconds();
    Assert.That(seconds, Is.GreaterThan(0));
    Assert.That(seconds, Is.LessThan(TrackerRender.MaxSeconds));
  }

  [Test]
  public void Render_ProducesNonEmptyStereoPcm() {
    var b = new ItModuleBuilder {
      Rows = [
        [(0, 60, 1, 0, 0)],
        [],
      ],
    };
    var player = ItPlayer.Load(b.Build());
    var pcm = player.Render(maxSeconds: 1.0);
    Assert.That(pcm.Length, Is.GreaterThan(0));
    Assert.That(pcm.Length % (TrackerRender.OutputChannels * 2), Is.EqualTo(0)); // whole stereo frames
  }
}
