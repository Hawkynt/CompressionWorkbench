#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Aac;
using Compression.Tests.Codecs.Aac;
using FileFormat.Aac;

namespace Compression.Tests.Aac;

[TestFixture]
public class AacTests {

  // A decodable mono silence frame: ReadStreamInfo parses the header and the
  // AAC-LC decoder yields 1024 zero samples, so the descriptor surfaces MONO.wav.
  private static byte[] MakeMonoSilenceFrame() =>
    AacTestFrames.SilenceFrame(channelConfig: 1, sampleRateIndex: 4);

  // A decodable stereo silence frame -> LEFT.wav + RIGHT.wav.
  private static byte[] MakeStereoSilenceFrame() =>
    AacTestFrames.SilenceFrame(channelConfig: 2, sampleRateIndex: 4);

  // A header-only frame whose 16-byte payload is garbage the decoder can't parse
  // as a valid raw_data_block (it runs off the end / hits an unsupported element),
  // forcing the FULL-only fallback. profile=1 (LC), sr idx 4, stereo.
  private static byte[] MakeUndecodableFrame() {
    const int payload = 16;
    var header = AacAdtsReader.BuildHeader(
      profile: 1, sampleRateIndex: 4, channelConfig: 2,
      frameLength: AacAdtsReader.ShortHeaderLength + payload);
    var frame = new byte[header.Length + payload];
    header.CopyTo(frame, 0);
    // Fill the payload with 0xFF: the first element id (111b) is END immediately,
    // which would yield zero channels — so instead poison it with a CCE (010...)
    // pattern the decoder explicitly rejects.
    for (var i = 0; i < payload; ++i)
      frame[header.Length + i] = 0x55; // 0101_0101 -> first 3 bits 010 = CCE (NotSupported)
    return frame;
  }

  [Test]
  public void AacDescriptor_ListsFull_AndDoesNotThrow() {
    var aac = MakeStereoSilenceFrame();
    using var ms = new MemoryStream(aac);

    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = new AacFormatDescriptor().List(ms, null), Throws.Nothing,
      "Listing must not throw.");

    var full = entries.FirstOrDefault(e => e.Name == "FULL.aac");
    Assert.That(full, Is.Not.Null, "Should always contain FULL.aac");
    Assert.That(full!.Method, Is.EqualTo("aac"));
    Assert.That(full.Kind, Is.EqualTo("Container"));
  }

  [Test]
  public void AacDescriptor_OnUndecodableInput_FallsBackToFullOnly() {
    var aac = MakeUndecodableFrame();
    using var ms = new MemoryStream(aac);
    var entries = new AacFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.aac"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False,
      "Undecodable AAC should not surface per-channel WAV entries.");
  }

  [Test]
  public void AacDescriptor_ExtractEntry_YieldsFullVerbatim() {
    var aac = MakeStereoSilenceFrame();
    using var ms = new MemoryStream(aac);
    using var output = new MemoryStream();
    new AacFormatDescriptor().ExtractEntry(ms, "FULL.aac", output, null);
    Assert.That(output.ToArray(), Is.EqualTo(aac), "FULL.aac must round-trip the original bytes.");
  }

  [Test]
  public void AacDescriptor_MonoSilence_SurfacesMonoWavChannel() {
    var aac = MakeMonoSilenceFrame();
    using var ms = new MemoryStream(aac);
    var descriptor = new AacFormatDescriptor();
    var entries = descriptor.List(ms, null);

    var channels = entries.Where(e => e.Kind == "Channel").ToList();
    Assert.That(channels.Select(e => e.Name), Is.EqualTo(new[] { "MONO.wav" }),
      "Decodable mono AAC surfaces a single MONO.wav channel.");

    using var input = new MemoryStream(aac);
    using var output = new MemoryStream();
    descriptor.ExtractEntry(input, "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()), "MONO.wav is a RIFF WAV");
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1), "mono");
  }

  [Test]
  public void AacDescriptor_StereoSilence_SurfacesLeftAndRightWavChannels() {
    var aac = MakeStereoSilenceFrame();
    using var ms = new MemoryStream(aac);
    var descriptor = new AacFormatDescriptor();
    var entries = descriptor.List(ms, null);

    var channels = entries.Where(e => e.Kind == "Channel").Select(e => e.Name).ToList();
    Assert.That(channels, Does.Contain("LEFT.wav"));
    Assert.That(channels, Does.Contain("RIGHT.wav"));

    foreach (var name in channels) {
      using var input = new MemoryStream(aac);
      using var output = new MemoryStream();
      descriptor.ExtractEntry(input, name, output, null);
      var wav = output.ToArray();
      Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()), $"{name} should be a RIFF WAV");
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1),
        $"{name} should be mono");
    }
  }
}
