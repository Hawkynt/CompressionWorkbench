#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.EaSchl;

namespace Compression.Tests.EaSchl;

[TestFixture]
public class EaSchlTests {

  private static byte[] BuildWav(int channels, int sampleRate, int framesPerChannel) {
    var pcm = new byte[framesPerChannel * channels * 2];
    for (var f = 0; f < framesPerChannel; ++f)
      for (var c = 0; c < channels; ++c) {
        var v = (short)(Math.Sin((f + c * 7) * 2 * Math.PI / 48) * 9000);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((f * channels + c) * 2), v);
      }
    return PcmCodec.ToWavBlob(pcm, channels, sampleRate, bitsPerSample: 16);
  }

  [Test]
  public void Writer_Reader_RoundTripsMetadataAndAudio() {
    const int channels = 2;
    const int rate = 22050;
    const int frames = 200;
    var wav = BuildWav(channels, rate, frames);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("clip.wav", wav) };
    using var created = new MemoryStream();
    new EaSchlFormatDescriptor().Create(created, inputs, new FormatCreateOptions());
    var schl = created.ToArray();

    Assert.That(schl.AsSpan(0, 4).ToArray(), Is.EqualTo("SCHl"u8.ToArray()));

    var reader = new EaSchlReader(schl);
    Assert.That(reader.Channels, Is.EqualTo(channels));
    Assert.That(reader.SampleRate, Is.EqualTo(rate));
    Assert.That(reader.Compression, Is.EqualTo(EaSchlReader.CompressionEaXa));
    Assert.That(reader.TotalSamples, Is.EqualTo(frames));

    var decoded = reader.DecodeInterleaved();
    Assert.That(decoded, Is.Not.Null);
    Assert.That(decoded!.Length, Is.GreaterThanOrEqualTo(frames * channels));
  }

  [Test]
  public void Descriptor_List_SurfacesFullChannelsAndMetadata() {
    var wav = BuildWav(channels: 2, sampleRate: 16000, framesPerChannel: 120);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("clip.wav", wav) };
    using var created = new MemoryStream();
    new EaSchlFormatDescriptor().Create(created, inputs, new FormatCreateOptions());

    using var reopen = new MemoryStream(created.ToArray());
    var entries = new EaSchlFormatDescriptor().List(reopen, null);

    Assert.That(entries.Any(e => e.Name == "FULL.eam"), Is.True);
    Assert.That(entries.First(e => e.Name == "FULL.eam").Kind, Is.EqualTo("Container"));
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
    Assert.That(entries.First(e => e.Name == "LEFT.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Descriptor_MonoChannelWav_DecodesCloseToSource() {
    const int rate = 24000;
    const int frames = 28 * 6;
    var wav = BuildWav(channels: 1, sampleRate: rate, framesPerChannel: frames);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("voice.wav", wav) };
    using var created = new MemoryStream();
    new EaSchlFormatDescriptor().Create(created, inputs, new FormatCreateOptions());

    using var reopen = new MemoryStream(created.ToArray());
    using var monoOut = new MemoryStream();
    new EaSchlFormatDescriptor().ExtractEntry(reopen, "MONO.wav", monoOut, null);
    var mono = monoOut.ToArray();

    Assert.That(mono.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(mono.AsSpan(24)), Is.EqualTo((uint)rate));
  }

  [Test]
  public void Descriptor_Create_PassthroughFullEam() {
    var wav = BuildWav(channels: 1, sampleRate: 11025, framesPerChannel: 56);
    using var first = new MemoryStream();
    new EaSchlFormatDescriptor().Create(first, new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("x.wav", wav)
    }, new FormatCreateOptions());
    var original = first.ToArray();

    using var output = new MemoryStream();
    new EaSchlFormatDescriptor().Create(output, new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("FULL.eam", original)
    }, new FormatCreateOptions());

    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [Test]
  public void Reader_UnknownCompression_FallsBackToFullOnly() {
    // Hand-build a SCHl whose PT advertises an unsupported compression type (0x99).
    var pt = new List<byte> { 0xFD, 0x82, 0x01, 0x01, 0xA0, 0x01, 0x99, 0x8A };
    using var ms = new MemoryStream();
    WriteBlock(ms, "SCHl"u8, pt.ToArray());
    WriteBlock(ms, "SCDl"u8, [0, 0, 0, 0, 0xAA, 0xBB]);
    WriteBlock(ms, "SCEl"u8, []);
    var blob = ms.ToArray();

    var reader = new EaSchlReader(blob);
    Assert.That(reader.Compression, Is.EqualTo(0x99));
    Assert.That(reader.DecodeInterleaved(), Is.Null);

    using var src = new MemoryStream(blob);
    var entries = new EaSchlFormatDescriptor().List(src, null);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False);
    Assert.That(entries.Any(e => e.Name == "FULL.eam"), Is.True);

    using var metaOut = new MemoryStream();
    using var src2 = new MemoryStream(blob);
    new EaSchlFormatDescriptor().ExtractEntry(src2, "metadata.ini", metaOut, null);
    var meta = Encoding.UTF8.GetString(metaOut.ToArray());
    Assert.That(meta, Does.Contain("not decodable"));
  }

  private static void WriteBlock(Stream s, ReadOnlySpan<byte> tag, byte[] body) {
    Span<byte> header = stackalloc byte[8];
    tag.CopyTo(header);
    BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(8 + body.Length));
    s.Write(header);
    s.Write(body);
  }
}
