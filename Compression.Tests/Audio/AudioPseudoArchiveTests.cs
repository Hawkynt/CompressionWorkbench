using Compression.Registry;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the shared <see cref="AudioPseudoArchive"/> projection used by every
/// audio-container descriptor (listing, on-disk extraction, single-entry streaming).
/// </summary>
[TestFixture]
public class AudioPseudoArchiveTests {

  private static IReadOnlyList<AudioPseudoArchive.Entry> Sample() => [
    new("FULL.caf", "Container", [1, 2, 3, 4], "pcm"),
    new("LEFT.wav", "Channel", [9, 9], "pcm"),
    new("RIGHT.wav", "Channel", [8, 8], "pcm"),
    new("metadata/info.bin", "Tag", [7], "stored"),
  ];

  [Test]
  public void List_ProjectsNamesKindsAndMethods() {
    var rows = AudioPseudoArchive.List(Sample());
    Assert.That(rows.Select(r => r.Name),
      Is.EqualTo(new[] { "FULL.caf", "LEFT.wav", "RIGHT.wav", "metadata/info.bin" }));
    Assert.That(rows[1].Kind, Is.EqualTo("Channel"));
    Assert.That(rows[0].Method, Is.EqualTo("pcm"));
    Assert.That(rows[0].OriginalSize, Is.EqualTo(4));
  }

  [Test]
  public void Extract_WithFilter_WritesOnlyMatching() {
    var tmp = Path.Combine(Path.GetTempPath(), "audpa_" + Guid.NewGuid().ToString("N"));
    try {
      AudioPseudoArchive.Extract(Sample(), tmp, ["LEFT.wav"]);
      Assert.That(File.Exists(Path.Combine(tmp, "LEFT.wav")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "RIGHT.wav")), Is.False);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "LEFT.wav")), Is.EqualTo(new byte[] { 9, 9 }));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void ExtractEntry_StreamsNamedEntry_AndThrowsForMissing() {
    using var ms = new MemoryStream();
    AudioPseudoArchive.ExtractEntry(Sample(), "RIGHT.wav", ms);
    Assert.That(ms.ToArray(), Is.EqualTo(new byte[] { 8, 8 }));
    Assert.That(() => AudioPseudoArchive.ExtractEntry(Sample(), "nope.wav", new MemoryStream()),
      Throws.InstanceOf<FileNotFoundException>());
  }

  // ── lazy entries ──────────────────────────────────────────────────────────

  [Test]
  public void List_DoesNotInvokeLazyFactory_AndReportsDeclaredSize() {
    var invoked = 0;
    var lazy = AudioPseudoArchive.Entry.Lazy(
      "TRACK_01.wav", "Track", () => { ++invoked; return new byte[100]; }, declaredSize: 100, "render");

    var rows = AudioPseudoArchive.List([lazy]);

    Assert.That(invoked, Is.Zero, "List() must not materialise a lazy entry");
    Assert.That(rows[0].OriginalSize, Is.EqualTo(100));
    Assert.That(rows[0].CompressedSize, Is.EqualTo(100));
    Assert.That(rows[0].Kind, Is.EqualTo("Track"));
    Assert.That(rows[0].Method, Is.EqualTo("render"));
  }

  [Test]
  public void ExtractEntry_InvokesLazyFactoryOnce_AndCaches() {
    var invoked = 0;
    var produced = new byte[] { 1, 2, 3, 4, 5 };
    var lazy = AudioPseudoArchive.Entry.Lazy(
      "TRACK_01.wav", "Track", () => { ++invoked; return produced; }, declaredSize: 5, "render");
    IReadOnlyList<AudioPseudoArchive.Entry> entries = [lazy];

    using var first = new MemoryStream();
    AudioPseudoArchive.ExtractEntry(entries, "TRACK_01.wav", first);
    using var second = new MemoryStream();
    AudioPseudoArchive.ExtractEntry(entries, "TRACK_01.wav", second);

    Assert.That(invoked, Is.EqualTo(1), "factory must run once and the result cached");
    Assert.That(first.ToArray(), Is.EqualTo(produced));
    Assert.That(second.ToArray(), Is.EqualTo(produced));
  }

  [Test]
  public void DeclaredSize_MatchesProducedBytesExactly() {
    var lazy = AudioPseudoArchive.Entry.Lazy(
      "TRACK_01.wav", "Track", () => new byte[777], declaredSize: 777, "render");

    Assert.That(lazy.DeclaredSize, Is.EqualTo(lazy.Materialize().Length));
    Assert.That(lazy.IsLazy, Is.False, "after materialisation the payload is cached");
  }

  [Test]
  public void Extract_OnlyMaterialisesWrittenLazyEntries() {
    var invokedA = 0;
    var invokedB = 0;
    IReadOnlyList<AudioPseudoArchive.Entry> entries = [
      AudioPseudoArchive.Entry.Lazy("TRACK_01.wav", "Track", () => { ++invokedA; return new byte[3]; }, 3, "render"),
      AudioPseudoArchive.Entry.Lazy("TRACK_02.wav", "Track", () => { ++invokedB; return new byte[3]; }, 3, "render"),
    ];
    var tmp = Path.Combine(Path.GetTempPath(), "audpa_" + Guid.NewGuid().ToString("N"));
    try {
      AudioPseudoArchive.Extract(entries, tmp, ["TRACK_01.wav"]);
      Assert.That(invokedA, Is.EqualTo(1));
      Assert.That(invokedB, Is.Zero, "filtered-out lazy entry must not be materialised");
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }
}
