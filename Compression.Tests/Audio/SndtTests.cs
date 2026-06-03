#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Sndt;

namespace Compression.Tests.Audio;

[TestFixture]
public class SndtTests {

  private static byte[] MakeSndt(int rate, byte[] samples) {
    var file = new byte[SndtFormatDescriptor.HeaderSize + samples.Length];
    var s = file.AsSpan();
    "SOUND"u8.CopyTo(s);
    s[5] = 0x1A;
    BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)samples.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)rate);
    BinaryPrimitives.WriteUInt16LittleEndian(s[16..], 8);
    samples.CopyTo(s[SndtFormatDescriptor.HeaderSize..]);
    return file;
  }

  [Test]
  public void Reader_SurfacesFullMonoMetadata() {
    var blob = MakeSndt(22050, new byte[] { 128, 200, 50 });
    var entries = new SndtFormatDescriptor().List(new MemoryStream(blob), null);
    Assert.That(entries.Any(e => e.Name == "FULL.sndt" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Mono_DecodesUnsignedPcmAtHeaderRate() {
    var payload = new byte[] { 128, 200, 50, 10 };
    var blob = MakeSndt(22050, payload);
    using var output = new MemoryStream();
    new SndtFormatDescriptor().ExtractEntry(new MemoryStream(blob), "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(22050u));
    Assert.That(wav.AsSpan(44).ToArray(), Is.EqualTo(payload));
  }

  [Test]
  public void OutOfRangeRate_DefaultsAndNotesInMetadata() {
    var blob = MakeSndt(123, new byte[] { 1, 2, 3 }); // below MinRate
    using var metaStream = new MemoryStream();
    new SndtFormatDescriptor().ExtractEntry(new MemoryStream(blob), "metadata.ini", metaStream, null);
    var meta = System.Text.Encoding.UTF8.GetString(metaStream.ToArray());
    Assert.That(meta, Does.Contain("sample_rate=8000"));
    Assert.That(meta, Does.Contain("sample_rate_raw=123"));
  }

  [Test]
  public void Create_FromMonoWav_RoundTrips() {
    var samples = new byte[] { 128, 255, 0, 64 };
    var wav = PcmCodec.ToWavBlob(samples, 1, 22050, 8);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wav) };

    using var output = new MemoryStream();
    new SndtFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var blob = output.ToArray();

    Assert.That(blob.AsSpan(0, 6).ToArray(), Is.EqualTo(new byte[] { (byte)'S', (byte)'O', (byte)'U', (byte)'N', (byte)'D', 0x1A }));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(12)), Is.EqualTo(22050u));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(8)), Is.EqualTo((uint)samples.Length));
    Assert.That(blob.AsSpan(SndtFormatDescriptor.HeaderSize).ToArray(), Is.EqualTo(samples));

    using var rt = new MemoryStream();
    new SndtFormatDescriptor().ExtractEntry(new MemoryStream(blob), "MONO.wav", rt, null);
    Assert.That(rt.ToArray().AsSpan(44).ToArray(), Is.EqualTo(samples));
  }

  [Test]
  public void NonSoundTool_Throws() {
    var bogus = new byte[SndtFormatDescriptor.HeaderSize + 4];
    "NOPE!"u8.CopyTo(bogus);
    Assert.Throws<InvalidDataException>(() =>
      new SndtFormatDescriptor().List(new MemoryStream(bogus), null));
  }
}
