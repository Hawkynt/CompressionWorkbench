using Compression.Registry;

namespace Compression.Tests.Audio;

/// <summary>
/// Pins the shared <see cref="AudioPseudoArchive"/> projection used by every
/// audio-container descriptor (listing, on-disk extraction, single-entry streaming).
/// </summary>
[TestFixture]
public class AudioPseudoArchiveTests {

  private static IReadOnlyList<AudioPseudoArchive.Entry> Sample() => [
    new("FULL.caf", "Track", [1, 2, 3, 4], "pcm"),
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
}
