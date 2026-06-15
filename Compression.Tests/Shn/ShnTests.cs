#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Shorten;
using Compression.Registry;
using FileFormat.Shn;

namespace Compression.Tests.Shn;

[TestFixture]
public class ShnTests {

  private static byte[] MakeStereoShn(int frames = 256) {
    var pcm = new byte[frames * 2 * 2];
    for (var i = 0; i < frames; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), (short)(i * 97 % 30000 - 15000));
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), (short)(-(i * 53 % 20000)));
    }
    using var inp = new MemoryStream(pcm);
    using var shn = new MemoryStream();
    ShortenCodec.Compress(inp, shn, 2, 44100, 16);
    return shn.ToArray();
  }

  private static byte[] MakeMonoShn(int frames = 400) {
    var pcm = new byte[frames * 2];
    for (var i = 0; i < frames; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(i * 41 % 12000 - 6000));
    using var inp = new MemoryStream(pcm);
    using var shn = new MemoryStream();
    ShortenCodec.Compress(inp, shn, 1, 44100, 16);
    return shn.ToArray();
  }

  [Test]
  public void Descriptor_ListsFullAndChannels_Stereo() {
    using var ms = new MemoryStream(MakeStereoShn());
    var entries = new ShnFormatDescriptor().List(ms, null);

    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "FULL.shn"), Is.True);
      Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
      Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
      Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    });
    Assert.That(entries.First(e => e.Name == "FULL.shn").Method, Is.EqualTo("shorten"));
    Assert.That(entries.First(e => e.Name == "LEFT.wav").Kind, Is.EqualTo("Channel"));
  }

  [Test]
  public void Descriptor_Mono_ProducesMonoWav() {
    using var ms = new MemoryStream(MakeMonoShn());
    var entries = new ShnFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.False);
  }

  [Test]
  public void Descriptor_ExtractedChannelIsValidMonoWav_AssumedSampleRate() {
    using var ms = new MemoryStream(MakeStereoShn());
    using var output = new MemoryStream();
    new ShnFormatDescriptor().ExtractEntry(ms, "LEFT.wav", output, null);
    var wav = output.ToArray();

    Assert.Multiple(() => {
      Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1)); // mono
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(44100u));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16));
    });
  }

  [Test]
  public void Descriptor_MetadataNotesUnknownSampleRate() {
    using var ms = new MemoryStream(MakeStereoShn());
    using var output = new MemoryStream();
    new ShnFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var text = Encoding.UTF8.GetString(output.ToArray());

    Assert.That(text, Does.Contain("sample_rate=unknown(assumed 44100)"));
    Assert.That(text, Does.Contain("channels=2"));
  }

  [Test]
  public void Descriptor_UndecodableStream_FallsBackToFullOnly() {
    // Valid magic + version but a truncated/garbage body that cannot parse a header.
    var bad = new byte[] { (byte)'a', (byte)'j', (byte)'k', (byte)'g', 2, 0x00 };
    using var ms = new MemoryStream(bad);
    var entries = new ShnFormatDescriptor().List(ms, null);

    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.shn"));
  }

  [Test]
  public void Descriptor_RoundTrip_SplitAssembleSplit_BitExactPcm() {
    var shn = MakeStereoShn();

    // 1) split into per-channel WAVs
    using var ms = new MemoryStream(shn);
    var descriptor = new ShnFormatDescriptor();
    var left = ExtractBlob(descriptor, shn, "LEFT.wav");
    var right = ExtractBlob(descriptor, shn, "RIGHT.wav");

    // 2) assemble a fresh .shn from those WAVs
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("LEFT.wav", left),
      ArchiveInputInfo.InMemory("RIGHT.wav", right),
    };
    using var assembled = new MemoryStream();
    descriptor.Create(assembled, inputs, new FormatCreateOptions());
    var rebuilt = assembled.ToArray();

    // 3) split the rebuilt .shn and verify the channel PCM is byte-exact
    var left2 = ExtractBlob(descriptor, rebuilt, "LEFT.wav");
    var right2 = ExtractBlob(descriptor, rebuilt, "RIGHT.wav");

    Assert.Multiple(() => {
      Assert.That(left2, Is.EqualTo(left));
      Assert.That(right2, Is.EqualTo(right));
    });
  }

  [Test]
  public void Descriptor_Create_FullShnPassthrough() {
    var shn = MakeStereoShn();
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.shn", shn) };
    using var output = new MemoryStream();
    new ShnFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(shn));
  }

  private static byte[] ExtractBlob(ShnFormatDescriptor descriptor, byte[] shn, string entry) {
    using var ms = new MemoryStream(shn);
    using var output = new MemoryStream();
    descriptor.ExtractEntry(ms, entry, output, null);
    return output.ToArray();
  }
}
