#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Sndr;

namespace Compression.Tests.Audio;

[TestFixture]
public class SndrTests {

  private static byte[] MakeSndr(int rate, byte[] samples) {
    var file = new byte[SndrFormatDescriptor.HeaderSize + samples.Length];
    var s = file.AsSpan();
    BinaryPrimitives.WriteUInt16LittleEndian(s, 0);
    BinaryPrimitives.WriteUInt16LittleEndian(s[2..], (ushort)rate);
    samples.CopyTo(s[SndrFormatDescriptor.HeaderSize..]);
    return file;
  }

  [Test]
  public void Reader_SurfacesFullMonoAndMetadata() {
    var blob = MakeSndr(8000, new byte[] { 128, 200, 50 });
    var entries = new SndrFormatDescriptor().List(new MemoryStream(blob), null);
    Assert.That(entries.Any(e => e.Name == "FULL.sndr" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Mono_DecodesUnsignedPcmAtHeaderRate() {
    var payload = new byte[] { 128, 200, 50, 10 };
    var blob = MakeSndr(11025, payload);
    using var output = new MemoryStream();
    new SndrFormatDescriptor().ExtractEntry(new MemoryStream(blob), "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(11025u));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(8));
    Assert.That(wav.AsSpan(44).ToArray(), Is.EqualTo(payload));
  }

  [Test]
  public void Create_FromMonoWav_RoundTrips() {
    var samples = new byte[] { 128, 255, 0, 64 };
    var wav = PcmCodec.ToWavBlob(samples, 1, 8000, 8);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wav) };

    using var output = new MemoryStream();
    new SndrFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var blob = output.ToArray();

    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(2)), Is.EqualTo(8000));
    Assert.That(blob.AsSpan(SndrFormatDescriptor.HeaderSize).ToArray(), Is.EqualTo(samples));

    // Re-read through the descriptor.
    using var rt = new MemoryStream();
    new SndrFormatDescriptor().ExtractEntry(new MemoryStream(blob), "MONO.wav", rt, null);
    Assert.That(rt.ToArray().AsSpan(44).ToArray(), Is.EqualTo(samples));
  }

  [Test]
  public void Create_From16BitWav_DownconvertsTo8Bit() {
    var pcm = new byte[4];
    BinaryPrimitives.WriteInt16LittleEndian(pcm, 0);        // → 128
    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(2), 32512); // → ~255
    var wav = PcmCodec.ToWavBlob(pcm, 1, 22050, 16);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wav) };

    using var output = new MemoryStream();
    new SndrFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var blob = output.ToArray();
    Assert.That(blob[SndrFormatDescriptor.HeaderSize], Is.EqualTo(128));
    Assert.That(blob[SndrFormatDescriptor.HeaderSize + 1], Is.GreaterThan((byte)250));
  }
}
