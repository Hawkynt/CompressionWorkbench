#pragma warning disable CS1591
using Codec.Pcm;

namespace Compression.Tests.Pcm;

/// <summary>
/// Pins the speaker-channel model against FFmpeg's <c>libavutil/channel_layout</c>:
/// canonical per-speaker names in WAVE/AVChannel bit order, the default layout
/// chosen for a given channel count (first match in FFmpeg's layout map — mono,
/// stereo, 2.1, 4.0, 5.0, 5.1, 6.1, 7.1, 5.1.4, 7.1.4, 9.1.4, 9.1.6, 22.2), and
/// explicit-mask naming for containers that carry a speaker bitmap
/// (WAVE_FORMAT_EXTENSIBLE, CAF channel bitmap).
/// </summary>
[TestFixture]
public class ChannelLayoutTests {

  [Test]
  public void DefaultNames_MonoAndStereo_KeepLegacyNames() {
    Assert.That(ChannelLayout.DefaultNames(1), Is.EqualTo(new[] { "MONO" }));
    Assert.That(ChannelLayout.DefaultNames(2), Is.EqualTo(new[] { "LEFT", "RIGHT" }));
  }

  [Test]
  public void DefaultNames_3Channels_Is2Point1() {
    // FFmpeg's first 3-channel layout is "2.1" (FL FR LFE), not 3.0.
    Assert.That(ChannelLayout.DefaultNames(3),
      Is.EqualTo(new[] { "FRONT_LEFT", "FRONT_RIGHT", "LFE" }));
  }

  [Test]
  public void DefaultNames_4Channels_Is4Point0() {
    Assert.That(ChannelLayout.DefaultNames(4),
      Is.EqualTo(new[] { "FRONT_LEFT", "FRONT_RIGHT", "CENTER", "BACK_CENTER" }));
  }

  [Test]
  public void DefaultNames_5And6Channels_Are5Point0BackAnd5Point1Back() {
    Assert.That(ChannelLayout.DefaultNames(5),
      Is.EqualTo(new[] { "FRONT_LEFT", "FRONT_RIGHT", "CENTER", "BACK_LEFT", "BACK_RIGHT" }));
    Assert.That(ChannelLayout.DefaultNames(6),
      Is.EqualTo(new[] { "FRONT_LEFT", "FRONT_RIGHT", "CENTER", "LFE", "BACK_LEFT", "BACK_RIGHT" }));
  }

  [Test]
  public void DefaultNames_7Channels_Is6Point1() {
    Assert.That(ChannelLayout.DefaultNames(7),
      Is.EqualTo(new[] { "FRONT_LEFT", "FRONT_RIGHT", "CENTER", "LFE", "BACK_CENTER", "SIDE_LEFT", "SIDE_RIGHT" }));
  }

  [Test]
  public void DefaultNames_8Channels_Is7Point1() {
    Assert.That(ChannelLayout.DefaultNames(8),
      Is.EqualTo(new[] { "FRONT_LEFT", "FRONT_RIGHT", "CENTER", "LFE",
                         "BACK_LEFT", "BACK_RIGHT", "SIDE_LEFT", "SIDE_RIGHT" }));
  }

  [Test]
  public void DefaultNames_10And12Channels_AreAtmosBeds() {
    // 10 → 5.1.4, 12 → 7.1.4 (first matches in FFmpeg's map).
    Assert.That(ChannelLayout.DefaultNames(10),
      Is.EqualTo(new[] { "FRONT_LEFT", "FRONT_RIGHT", "CENTER", "LFE", "SIDE_LEFT", "SIDE_RIGHT",
                         "TOP_FRONT_LEFT", "TOP_FRONT_RIGHT", "TOP_BACK_LEFT", "TOP_BACK_RIGHT" }));
    Assert.That(ChannelLayout.DefaultNames(12),
      Is.EqualTo(new[] { "FRONT_LEFT", "FRONT_RIGHT", "CENTER", "LFE", "BACK_LEFT", "BACK_RIGHT",
                         "SIDE_LEFT", "SIDE_RIGHT",
                         "TOP_FRONT_LEFT", "TOP_FRONT_RIGHT", "TOP_BACK_LEFT", "TOP_BACK_RIGHT" }));
  }

  [Test]
  public void DefaultNames_24Channels_Is22Point2() {
    // NHK 22.2 — the full Super Hi-Vision bed in WAVE/AVChannel bit order.
    Assert.That(ChannelLayout.DefaultNames(24), Is.EqualTo(new[] {
      "FRONT_LEFT", "FRONT_RIGHT", "CENTER", "LFE", "BACK_LEFT", "BACK_RIGHT",
      "FRONT_LEFT_OF_CENTER", "FRONT_RIGHT_OF_CENTER", "BACK_CENTER",
      "SIDE_LEFT", "SIDE_RIGHT", "TOP_CENTER",
      "TOP_FRONT_LEFT", "TOP_FRONT_CENTER", "TOP_FRONT_RIGHT",
      "TOP_BACK_LEFT", "TOP_BACK_CENTER", "TOP_BACK_RIGHT",
      "LFE2", "TOP_SIDE_LEFT", "TOP_SIDE_RIGHT",
      "BOTTOM_FRONT_CENTER", "BOTTOM_FRONT_LEFT", "BOTTOM_FRONT_RIGHT",
    }));
  }

  [Test]
  public void DefaultNames_16Channels_Is9Point1Point6() {
    var names = ChannelLayout.DefaultNames(16);
    Assert.That(names, Has.Count.EqualTo(16));
    Assert.That(names, Does.Contain("TOP_SIDE_LEFT"));
    Assert.That(names, Does.Contain("FRONT_LEFT_OF_CENTER"));
    Assert.That(names, Does.Not.Contain("CH_0"));
  }

  [Test]
  public void DefaultNames_UnmappedCount_FallsBackToIndexedNames() {
    // FFmpeg has no default 9- or 13-channel layout → CH_n, "and beyond" stays decodable.
    Assert.That(ChannelLayout.DefaultNames(9), Is.EqualTo(Enumerable.Range(0, 9).Select(i => $"CH_{i}")));
    Assert.That(ChannelLayout.DefaultNames(13).Count, Is.EqualTo(13));
    Assert.That(ChannelLayout.DefaultNames(64).Count, Is.EqualTo(64));
  }

  [Test]
  public void NamesFromMask_ExplicitMask_OverridesCountDefault() {
    // 3.1 (FL FR FC LFE) — a 4-channel mask that differs from the 4.0 default.
    const ulong mask = 0b1111;
    Assert.That(ChannelLayout.NamesFromMask(mask, 4),
      Is.EqualTo(new[] { "FRONT_LEFT", "FRONT_RIGHT", "CENTER", "LFE" }));
  }

  [Test]
  public void NamesFromMask_PlainStereoMask_KeepsLegacyNames() {
    Assert.That(ChannelLayout.NamesFromMask(0b11, 2), Is.EqualTo(new[] { "LEFT", "RIGHT" }));
  }

  [Test]
  public void NamesFromMask_PopcountMismatch_FallsBackToDefaults() {
    // Mask says 2 speakers but the stream carries 6 channels → ignore the mask.
    Assert.That(ChannelLayout.NamesFromMask(0b11, 6), Is.EqualTo(ChannelLayout.DefaultNames(6)));
  }

  [Test]
  public void OrderIndex_RecoversCanonicalInterleavePosition() {
    Assert.That(ChannelLayout.OrderIndex("MONO"), Is.EqualTo(0));
    Assert.That(ChannelLayout.OrderIndex("LEFT"), Is.EqualTo(0));
    Assert.That(ChannelLayout.OrderIndex("FRONT_LEFT"), Is.EqualTo(0));
    Assert.That(ChannelLayout.OrderIndex("RIGHT"), Is.EqualTo(1));
    Assert.That(ChannelLayout.OrderIndex("LFE"), Is.EqualTo(3));
    Assert.That(ChannelLayout.OrderIndex("SIDE_RIGHT"), Is.EqualTo(10));
    Assert.That(ChannelLayout.OrderIndex("TOP_SIDE_RIGHT"), Is.EqualTo(37));
    Assert.That(ChannelLayout.OrderIndex("BOTTOM_FRONT_RIGHT"), Is.EqualTo(40));
    Assert.That(ChannelLayout.OrderIndex("CH_17"), Is.EqualTo(17));
    Assert.That(ChannelLayout.OrderIndex("ch_3"), Is.EqualTo(3));
    Assert.That(ChannelLayout.OrderIndex("bogus"), Is.EqualTo(int.MaxValue));
  }

  [Test]
  public void OrderIndex_SortsEveryDefaultLayoutBackIntoFileOrder() {
    // For every defaulted channel count, shuffling the names and re-sorting by
    // OrderIndex must restore the original interleave order — this is what the
    // assemble (Create) direction relies on.
    foreach (var count in new[] { 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 24 }) {
      var names = ChannelLayout.DefaultNames(count);
      var shuffled = names.OrderBy(n => n, StringComparer.Ordinal).ToList(); // deterministic shuffle
      var restored = shuffled.OrderBy(ChannelLayout.OrderIndex).ToList();
      Assert.That(restored, Is.EqualTo(names), $"count {count}");
    }
  }

  [Test]
  public void AllSpeakerNames_AreDistinct() {
    var names = ChannelLayout.DefaultNames(24)
      .Concat(ChannelLayout.DefaultNames(16))
      .Concat(ChannelLayout.DefaultNames(8))
      .Distinct()
      .ToList();
    Assert.That(names.Count, Is.EqualTo(names.Distinct().Count()));
  }
}
