#pragma warning disable CS1591
using System.Buffers.Binary;
using FileFormat.Flac;

namespace Compression.Tests.Audio;

[TestFixture]
public class FlacChannelSplitTests {

  // 44.1 kHz stereo 16-bit PCM, 256 samples — enough for FLAC to encode.
  private static byte[] MakeStereoFlac() {
    var pcm = new byte[256 * 2 * 2];
    for (var i = 0; i < 256; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), (short)(i * 100 % 30000));
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), (short)(-(i * 100 % 30000)));
    }

    using var input = new MemoryStream(pcm);
    using var output = new MemoryStream();
    FlacWriter.Compress(input, output);
    return output.ToArray();
  }


  [Test]
  public void FlacDescriptor_ListsFullAndChannels_Stereo() {
    var flac = MakeStereoFlac();
    using var ms = new MemoryStream(flac);
    var entries = new FlacFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.flac"), Is.True, "Should contain FULL.flac");
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True, "Should contain LEFT.wav");
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True, "Should contain RIGHT.wav");
    Assert.That(entries.First(e => e.Name == "FULL.flac").Method, Is.EqualTo("flac"));
    Assert.That(entries.First(e => e.Name == "LEFT.wav").Method, Is.EqualTo("pcm"));
    Assert.That(entries.First(e => e.Name == "LEFT.wav").Kind, Is.EqualTo("Channel"));
  }

  [Test]
  public void FlacDescriptor_StereoDoesNotProduceMono() {
    // The writer always produces stereo; verify that stereo FLAC
    // yields LEFT/RIGHT, never MONO.
    var flac = MakeStereoFlac();
    using var ms = new MemoryStream(flac);
    var entries = new FlacFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.False,
      "Stereo FLAC should not produce MONO.wav");
  }

  [Test]
  public void FlacDescriptor_ExtractedChannelIsValidMonoWav() {
    var flac = MakeStereoFlac();
    using var ms = new MemoryStream(flac);
    using var output = new MemoryStream();
    new FlacFormatDescriptor().ExtractEntry(ms, "LEFT.wav", output, null);
    var wav = output.ToArray();

    // Valid RIFF header
    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    // fmt chunk: NumChannels = 1 (mono)
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1));
    // fmt chunk: SampleRate = 44100
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(44100u));
    // fmt chunk: BitsPerSample = 16
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16));
  }

  [Test]
  public void FlacDescriptor_ExtractToDirectory() {
    var flac = MakeStereoFlac();
    var tmp = Path.Combine(Path.GetTempPath(), "flac_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(flac);
      new FlacFormatDescriptor().Extract(ms, tmp, null, ["LEFT.wav"]);
      var leftPath = Path.Combine(tmp, "LEFT.wav");
      Assert.That(File.Exists(leftPath), Is.True, "LEFT.wav should be extracted");
      var mono = File.ReadAllBytes(leftPath);
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(mono.AsSpan(22)), Is.EqualTo(1));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void FlacDescriptor_StreamDecompressStillWorks() {
    // Verify that stream compress/decompress is unaffected by the archive view addition.
    var pcm = new byte[256 * 2 * 2];
    for (var i = 0; i < 256; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), (short)(i * 100 % 30000));
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), (short)(-(i * 100 % 30000)));
    }

    using var input = new MemoryStream(pcm);
    using var compressed = new MemoryStream();
    new FlacFormatDescriptor().Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    new FlacFormatDescriptor().Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(pcm));
  }
}
