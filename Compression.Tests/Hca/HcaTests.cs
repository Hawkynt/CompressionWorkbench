#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Tests.Codecs.CriHca;
using FileFormat.Hca;

namespace Compression.Tests.Hca;

[TestFixture]
public class HcaTests {

  [Test]
  public void Descriptor_List_SurfacesFullMonoAndMetadata() {
    using var ms = new MemoryStream(HcaFixture.BuildSilence(channels: 1, sampleRate: 44100, frameCount: 2));
    var entries = new HcaFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.hca"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.First(e => e.Name == "FULL.hca").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "MONO.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.First(e => e.Name == "metadata.ini").Kind, Is.EqualTo("Tag"));
  }

  [Test]
  public void Descriptor_Stereo_SurfacesPerChannelWavs() {
    using var ms = new MemoryStream(HcaFixture.BuildSilence(channels: 2, sampleRate: 48000, frameCount: 1));
    var entries = new HcaFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
    Assert.That(entries.Count(e => e.Kind == "Channel"), Is.EqualTo(2));
  }

  [Test]
  public void Descriptor_MonoWav_HasHeaderRateAndDecodedLength() {
    const int frames = 2;
    const int rate = 44100;
    using var ms = new MemoryStream(HcaFixture.BuildSilence(channels: 1, sampleRate: rate, frameCount: frames));
    using var output = new MemoryStream();
    new HcaFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1)); // mono
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo((uint)rate));

    var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40));
    Assert.That(dataSize, Is.EqualTo((uint)(frames * 1024 * 2))); // 1024 samples/frame, 16-bit
  }

  [Test]
  public void Descriptor_KeyedCipher_FallsBackToFullPlusMetadataNote() {
    var hca = HcaFixture.BuildSilence(cipherType: 56);
    using var ms = new MemoryStream(hca);
    var entries = new HcaFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.hca"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False, "keyed streams must not surface decoded channels");

    using var meta = new MemoryStream();
    using var ms2 = new MemoryStream(hca);
    new HcaFormatDescriptor().ExtractEntry(ms2, "metadata.ini", meta, null);
    var text = Encoding.UTF8.GetString(meta.ToArray());
    Assert.That(text, Does.Contain("cipher_type=56"));
    Assert.That(text, Does.Contain("note=keyed"));
  }

  [Test]
  public void Descriptor_NonHca_FallsBackToFullOnly() {
    using var ms = new MemoryStream(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 });
    var entries = new HcaFormatDescriptor().List(ms, null);

    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.hca"));
  }

  [Test]
  public void Metadata_CarriesRateChannelsFramesAndCipher() {
    using var ms = new MemoryStream(HcaFixture.BuildSilence(channels: 1, sampleRate: 44100, frameCount: 4, cipherType: 1));
    using var output = new MemoryStream();
    new HcaFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var text = Encoding.UTF8.GetString(output.ToArray());

    Assert.That(text, Does.Contain("sample_rate=44100"));
    Assert.That(text, Does.Contain("channels=1"));
    Assert.That(text, Does.Contain("frame_count=4"));
    Assert.That(text, Does.Contain("cipher_type=1"));
  }
}
