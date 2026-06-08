#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Mod;

namespace Compression.Tests.Mod;

[TestFixture]
public class ModTests {

  // Build a minimal synthetic 4-channel MOD with two samples (one with data, one empty)
  // and exactly one pattern (the minimum required).
  private static byte[] MakeSyntheticMod() {
    const int patternBytes = 64 * 4 * 4; // 1024 bytes for 4 channels
    const int sample1Words = 8;
    const int sample1Bytes = sample1Words * 2;
    var total = 1084 + patternBytes + sample1Bytes;
    var buf = new byte[total];

    // 20-byte title.
    var title = Encoding.ASCII.GetBytes("SyntheticMod");
    Buffer.BlockCopy(title, 0, buf, 0, title.Length);

    // 31 sample headers starting at offset 20, 30 bytes each.
    // Sample 1: name "HelloSample", length in words, rest zero.
    var s1Name = Encoding.ASCII.GetBytes("HelloSample");
    Buffer.BlockCopy(s1Name, 0, buf, 20, s1Name.Length);
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(20 + 22, 2), (ushort)sample1Words);
    // finetune=0, volume=64
    buf[20 + 22 + 2] = 0;
    buf[20 + 22 + 3] = 64;
    // loop start = 0, loop length = 1 (standard "no loop" convention)
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(20 + 26, 2), 0);
    BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(20 + 28, 2), 1);

    // songlen = 1, one pattern referenced (pattern 0).
    buf[950] = 1;
    buf[951] = 127;
    buf[952] = 0; // order[0] = pattern 0

    // Signature at offset 1080.
    var sig = Encoding.ASCII.GetBytes("M.K.");
    Buffer.BlockCopy(sig, 0, buf, 1080, 4);

    // Pattern 0 (all zero — empty).
    // Sample 1 data (just a bytewise ramp).
    for (var i = 0; i < sample1Bytes; ++i) buf[1084 + patternBytes + i] = (byte)(i - 8);

    return buf;
  }

  [Test]
  public void List_ReturnsFullModAndMetadataAndPatternAndSample() {
    var blob = MakeSyntheticMod();
    using var ms = new MemoryStream(blob);
    var entries = new ModFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.mod"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "patterns/pattern_00.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/01_")), Is.True);
    // Only 1 sample has data → no sample 02 entry.
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/02_")), Is.False);
  }

  [Test]
  public void List_IncludesRenderedSongWav() {
    var blob = MakeSyntheticMod();
    using var ms = new MemoryStream(blob);
    var entries = new ModFormatDescriptor().List(ms, null);

    var song = entries.FirstOrDefault(e => e.Name == "SONG.wav");
    Assert.That(song, Is.Not.Null);
    Assert.That(song!.Kind, Is.EqualTo("Track"));
    Assert.That(song.Method, Is.EqualTo("render"));
  }

  [Test]
  public void SongWav_IsValidRiffStereo44100() {
    var blob = MakeSyntheticMod();
    var wav = ExtractEntry(blob, "SONG.wav");
    AssertWav(wav, expectedChannels: 2, expectedRate: 44100);
  }

  [Test]
  public void SongWav_AlsoSurfacesPerChannelMonoWavs() {
    var blob = MakeSyntheticMod();
    using var ms = new MemoryStream(blob);
    var entries = new ModFormatDescriptor().List(ms, null);

    var channels = entries.Where(e => e.Kind == "Channel").Select(e => e.Name).ToList();
    Assert.That(channels, Does.Contain("SONG_LEFT.wav"));
    Assert.That(channels, Does.Contain("SONG_RIGHT.wav"));
    AssertWav(ExtractEntry(blob, "SONG_LEFT.wav"), expectedChannels: 1, expectedRate: 44100);
    AssertWav(ExtractEntry(blob, "SONG_RIGHT.wav"), expectedChannels: 1, expectedRate: 44100);
  }

  [Test]
  public void Samples_AreMonoWavFiles() {
    var blob = MakeSyntheticMod();
    using var ms = new MemoryStream(blob);
    var entries = new ModFormatDescriptor().List(ms, null);
    var sample = entries.First(e => e.Name.StartsWith("samples/01_"));
    Assert.That(sample.Name, Does.EndWith(".wav"));
    var wav = ExtractEntry(blob, sample.Name);
    AssertWav(wav, expectedChannels: 1, expectedRate: -1);
  }

  [Test]
  public void MalformedModule_DegradesToFullOnly() {
    // 1084 bytes of zeros with no valid signature still parses to a 4-channel MOD,
    // so use a too-short blob: the descriptor must surface FULL.mod and nothing else.
    var blob = new byte[64];
    using var ms = new MemoryStream(blob);
    var entries = new ModFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.mod"), Is.True);
    Assert.That(entries.Any(e => e.Name == "SONG.wav"), Is.False);
  }

  private static byte[] ExtractEntry(byte[] blob, string name) {
    using var input = new MemoryStream(blob);
    using var output = new MemoryStream();
    new ModFormatDescriptor().ExtractEntry(input, name, output, null);
    return output.ToArray();
  }

  private static void AssertWav(byte[] wav, int expectedChannels, int expectedRate) {
    Assert.That(wav.Length, Is.GreaterThan(44));
    Assert.That(System.Text.Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
    Assert.That(System.Text.Encoding.ASCII.GetString(wav, 8, 4), Is.EqualTo("WAVE"));
    var channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22, 2));
    var rate = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24, 4));
    var bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34, 2));
    Assert.That(channels, Is.EqualTo(expectedChannels));
    Assert.That(bits, Is.EqualTo(16));
    if (expectedRate > 0)
      Assert.That(rate, Is.EqualTo((uint)expectedRate));
  }

  [Test]
  public void Extract_WritesExpectedFiles() {
    var blob = MakeSyntheticMod();
    var tmp = Path.Combine(Path.GetTempPath(), "mod_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new ModFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "FULL.mod")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "patterns", "pattern_00.bin")), Is.True);
      var sampleDir = Path.Combine(tmp, "samples");
      Assert.That(Directory.Exists(sampleDir), Is.True);
      Assert.That(Directory.GetFiles(sampleDir).Length, Is.EqualTo(1));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }
}
