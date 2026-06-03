#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Codec.SpuAdpcm;
using Compression.Registry;
using FileFormat.Vag;

namespace Compression.Tests.Vag;

[TestFixture]
public class VagTests {

  // Builds a minimal valid VAGp file around the given ADPCM payload (big-endian header).
  private static byte[] BuildVag(byte[] adpcm, int sampleRate, string name) {
    var header = new byte[0x30];
    "VAGp"u8.CopyTo(header);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 0x20);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), (uint)adpcm.Length);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), (uint)sampleRate);
    Encoding.ASCII.GetBytes(name).CopyTo(header.AsSpan(0x20));

    var blob = new byte[header.Length + adpcm.Length];
    header.CopyTo(blob.AsSpan());
    adpcm.CopyTo(blob.AsSpan(header.Length));
    return blob;
  }

  private static byte[] SampleVag(int blocks = 4, int sampleRate = 22050) {
    var pcm = new short[SpuAdpcmCodec.SamplesPerBlock * blocks];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (short)(Math.Sin(i * 2 * Math.PI / 50) * 8000);
    return BuildVag(SpuAdpcmCodec.Encode(pcm), sampleRate, "test");
  }

  [Test]
  public void Descriptor_List_SurfacesFullMonoAndMetadata() {
    using var ms = new MemoryStream(SampleVag());
    var entries = new VagFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.vag"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.First(e => e.Name == "FULL.vag").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "MONO.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.First(e => e.Name == "metadata.ini").Kind, Is.EqualTo("Tag"));
  }

  [Test]
  public void Descriptor_MonoWav_HasHeaderSampleRateAndDecodedLength() {
    const int blocks = 4;
    const int rate = 22050;
    using var ms = new MemoryStream(SampleVag(blocks, rate));
    using var output = new MemoryStream();
    new VagFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1)); // mono
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo((uint)rate));

    // data chunk size = samples * 2 bytes; samples = blocks * 28.
    var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40));
    Assert.That(dataSize, Is.EqualTo((uint)(blocks * SpuAdpcmCodec.SamplesPerBlock * 2)));
  }

  [Test]
  public void Descriptor_Create_FromMonoWav_ProducesValidVagp() {
    const int samples = SpuAdpcmCodec.SamplesPerBlock * 3;
    var pcm = new byte[samples * 2];
    for (var i = 0; i < samples; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(Math.Sin(i / 8.0) * 6000));
    var wav = PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate: 16000, bitsPerSample: 16);

    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("voice.wav", wav),
    };

    using var output = new MemoryStream();
    new VagFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var vag = output.ToArray();

    Assert.That(vag.AsSpan(0, 4).ToArray(), Is.EqualTo("VAGp"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(vag.AsSpan(16)), Is.EqualTo(16000u));
    // name field carries the WAV's base name.
    var name = Encoding.ASCII.GetString(vag.AsSpan(0x20, 5));
    Assert.That(name, Is.EqualTo("voice"));
  }

  [Test]
  public void Descriptor_Create_RoundTrips_ChannelPresentAndSampleCountMatches() {
    const int samples = SpuAdpcmCodec.SamplesPerBlock * 5;
    var pcm = new byte[samples * 2];
    for (var i = 0; i < samples; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(Math.Sin(i / 6.0) * 9000));
    var wav = PcmCodec.ToWavBlob(pcm, channels: 1, sampleRate: 32000, bitsPerSample: 16);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("clip.wav", wav) };
    using var created = new MemoryStream();
    new VagFormatDescriptor().Create(created, inputs, new FormatCreateOptions());

    // Re-open the produced VAG and confirm the channel survives the round-trip.
    using var reopen = new MemoryStream(created.ToArray());
    var entries = new VagFormatDescriptor().List(reopen, null);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);

    using var monoOut = new MemoryStream();
    using var reopen2 = new MemoryStream(created.ToArray());
    new VagFormatDescriptor().ExtractEntry(reopen2, "MONO.wav", monoOut, null);
    var mono = monoOut.ToArray();
    var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(mono.AsSpan(40));
    // Encoder rounds up to whole 28-sample blocks; sample count must match that.
    Assert.That(dataSize, Is.EqualTo((uint)(samples * 2)));
  }

  [Test]
  public void Descriptor_Create_PassthroughFullVag() {
    var original = SampleVag(2, 8000);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.vag", original) };
    using var output = new MemoryStream();
    new VagFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }
}
