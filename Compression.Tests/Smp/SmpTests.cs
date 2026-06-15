#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Smp;

namespace Compression.Tests.Smp;

[TestFixture]
public class SmpTests {

  private static byte[] MakeSmp(short[] samples, int rate) {
    using var ms = new MemoryStream();

    void Fixed(string text, int len) {
      var f = new byte[len];
      var bytes = Encoding.ASCII.GetBytes(text);
      Array.Copy(bytes, f, Math.Min(bytes.Length, len));
      ms.Write(f);
    }

    Fixed(SmpReader.Magic, SmpReader.MagicLength);
    Fixed("2.1 ", 4);
    Fixed("a comment", 60);
    Fixed("name", 30);

    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)samples.Length);
    ms.Write(u32);

    var sampleBytes = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(sampleBytes.AsSpan(i * 2), samples[i]);
    ms.Write(sampleBytes);

    var trailer = new byte[SmpReader.TrailerSize];
    var rateOffset = SmpReader.LoopCount * SmpReader.LoopRecordSize +
                     SmpReader.MarkerCount * SmpReader.MarkerRecordSize;
    trailer[rateOffset] = 60; // MIDI unity
    BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(rateOffset + 1), (uint)rate);
    ms.Write(trailer);

    return ms.ToArray();
  }

  [Test]
  public void Mono_ListsFullAndMonoChannel() {
    using var ms = new MemoryStream(MakeSmp(new short[] { 0, 100, -100, 32767 }, 44100));
    var entries = new SmpFormatDescriptor().List(ms, null);
    Assert.That(entries.First(e => e.Name == "FULL.smp").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "MONO.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name is "LEFT.wav" or "RIGHT.wav"), Is.False); // mono only
  }

  [Test]
  public void Mono_ChannelIsValidMonoWav() {
    using var ms = new MemoryStream(MakeSmp(new short[] { 0, 100, -100, 32767 }, 44100));
    using var output = new MemoryStream();
    new SmpFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(44100u));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16));

    var pcm = wav.AsSpan(44);
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm), Is.EqualTo(0));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[2..]), Is.EqualTo(100));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[4..]), Is.EqualTo(-100));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[6..]), Is.EqualTo(32767));
  }

  [Test]
  public void Create_FromMonoWav_RoundTripsExact() {
    var samples = new short[] { 0, 100, -100, 32767 };
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    var wav = PcmCodec.ToWavBlob(pcm, 1, 22050, 16);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wav) };

    using var output = new MemoryStream();
    new SmpFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var parsed = new SmpReader().Read(output.ToArray());

    Assert.That(parsed.SampleRate, Is.EqualTo(22050));
    Assert.That(parsed.SampleCount, Is.EqualTo(4u));
    Assert.That(parsed.SamplesLe, Is.EqualTo(pcm));
  }

  [Test]
  public void RoundTrip_DecodeThenCreate_PreservesSamples() {
    var original = MakeSmp(new short[] { 5, -5, 12345, -12345 }, 8000);
    using var decoded = new MemoryStream();
    new SmpFormatDescriptor().ExtractEntry(new MemoryStream(original), "MONO.wav", decoded, null);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", decoded.ToArray()) };
    using var rebuilt = new MemoryStream();
    new SmpFormatDescriptor().Create(rebuilt, inputs, new FormatCreateOptions());

    var a = new SmpReader().Read(original);
    var b = new SmpReader().Read(rebuilt.ToArray());
    Assert.That(b.SamplesLe, Is.EqualTo(a.SamplesLe));
    Assert.That(b.SampleRate, Is.EqualTo(a.SampleRate));
  }
}
