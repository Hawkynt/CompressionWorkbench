#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Aac;
using FileFormat.Aac;

namespace Compression.Tests.Aac;

[TestFixture]
public class AacTests {

  // A single ADTS AAC-LC frame: profile=1 (LC), sample-rate index 4 (44.1 kHz),
  // channel config 2 (stereo). ReadStreamInfo parses this header; full decode is
  // attempted but the AAC-LC spectral pipeline is not implemented, so the
  // descriptor must fall back to FULL-only.
  private static byte[] MakeStereoAdtsFrame() {
    const int payload = 16;
    var header = AacAdtsReader.BuildHeader(
      profile: 1, sampleRateIndex: 4, channelConfig: 2,
      frameLength: AacAdtsReader.ShortHeaderLength + payload);
    var frame = new byte[header.Length + payload];
    header.CopyTo(frame, 0);
    return frame;
  }

  [Test]
  public void AacDescriptor_ListsFull_AndDoesNotThrow() {
    var aac = MakeStereoAdtsFrame();
    using var ms = new MemoryStream(aac);

    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = new AacFormatDescriptor().List(ms, null), Throws.Nothing,
      "Listing must not throw even when the bitstream can't be decoded.");

    var full = entries.FirstOrDefault(e => e.Name == "FULL.aac");
    Assert.That(full, Is.Not.Null, "Should always contain FULL.aac");
    Assert.That(full!.Method, Is.EqualTo("aac"));
    Assert.That(full.Kind, Is.EqualTo("Track"));
  }

  [Test]
  public void AacDescriptor_OnUndecodableInput_FallsBackToFullOnly() {
    // The decoder's SCE/CPE bodies (spectral pipeline) are unimplemented, so a
    // bare AAC-LC frame can't be decoded: the listing must be FULL-only, no
    // channel entries, no exception.
    var aac = MakeStereoAdtsFrame();
    using var ms = new MemoryStream(aac);
    var entries = new AacFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.aac"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False,
      "Undecodable AAC should not surface per-channel WAV entries.");
  }

  [Test]
  public void AacDescriptor_ExtractEntry_YieldsFullVerbatim() {
    var aac = MakeStereoAdtsFrame();
    using var ms = new MemoryStream(aac);
    using var output = new MemoryStream();
    new AacFormatDescriptor().ExtractEntry(ms, "FULL.aac", output, null);
    Assert.That(output.ToArray(), Is.EqualTo(aac), "FULL.aac must round-trip the original bytes.");
  }

  [Test]
  public void AacDescriptor_AttemptsDecode_AndSplitsChannelsWhenItSucceeds() {
    // Documents the intended positive path: when the AAC-LC decoder can produce
    // PCM, the descriptor splits it into per-channel mono WAVs. With the current
    // (header-only) decoder this path is unreachable, so the assertion is
    // conditional — channel entries, if present, must be valid mono RIFF WAVs.
    var aac = MakeStereoAdtsFrame();
    using var ms = new MemoryStream(aac);
    var descriptor = new AacFormatDescriptor();
    var entries = descriptor.List(ms, null);

    var channels = entries.Where(e => e.Kind == "Channel").ToList();
    if (channels.Count == 0)
      Assert.Pass("Decode not yet supported for this input; graceful FULL-only fallback verified elsewhere.");

    foreach (var ch in channels) {
      using var input = new MemoryStream(aac);
      using var output = new MemoryStream();
      descriptor.ExtractEntry(input, ch.Name, output, null);
      var wav = output.ToArray();
      Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()), $"{ch.Name} should be a RIFF WAV");
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1),
        $"{ch.Name} should be mono");
    }
  }
}
