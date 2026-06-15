#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Pvf;

namespace Compression.Tests.Pvf;

[TestFixture]
public class PvfTests {

  private static byte[] MakePvf1(int channels, int rate, int bits, int[] samples) {
    using var ms = new MemoryStream();
    ms.Write(Encoding.ASCII.GetBytes($"PVF1\n{channels} {rate} {bits}\n"));
    Span<byte> w = stackalloc byte[4];
    foreach (var s in samples) {
      BinaryPrimitives.WriteInt32BigEndian(w, s);
      ms.Write(w);
    }
    return ms.ToArray();
  }

  private static byte[] MakePvf2(int channels, int rate, int bits, int[] samples)
    => Encoding.ASCII.GetBytes($"PVF2\n{channels} {rate} {bits}\n{string.Join(" ", samples)}\n");

  [Test]
  public void Pvf1Mono_ListsFullAndMonoChannel() {
    // 16-bit values, no shift.
    var pvf = MakePvf1(1, 8000, 16, new[] { 0, 100, -100, 32767 });
    var entries = new PvfFormatDescriptor().List(new MemoryStream(pvf), null);
    Assert.That(entries.First(e => e.Name == "FULL.pvf").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "MONO.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Pvf1Mono_ChannelIsValidMonoWav() {
    var pvf = MakePvf1(1, 8000, 16, new[] { 0, 100, -100, 32767 });
    using var output = new MemoryStream();
    new PvfFormatDescriptor().ExtractEntry(new MemoryStream(pvf), "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(8000u));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16));

    var pcm = wav.AsSpan(44);
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm), Is.EqualTo(0));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[2..]), Is.EqualTo(100));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[4..]), Is.EqualTo(-100));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[6..]), Is.EqualTo(32767));
  }

  [Test]
  public void Pvf1_HighBitWidth_ShiftsDownTo16() {
    // 24-bit significant values shift right by 8.
    var pvf = MakePvf1(1, 8000, 24, new[] { 0, 256, -256, 1 << 23 });
    using var output = new MemoryStream();
    new PvfFormatDescriptor().ExtractEntry(new MemoryStream(pvf), "MONO.wav", output, null);
    var pcm = output.ToArray().AsSpan(44);
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm), Is.EqualTo(0));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[2..]), Is.EqualTo(1));    // 256 >> 8
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[4..]), Is.EqualTo(-1));   // -256 >> 8
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[6..]), Is.EqualTo(unchecked((short)0x8000))); // 2^23 >> 8 = -32768
  }

  [Test]
  public void Pvf2Ascii_DecodesSameAsBinary() {
    var pvf = MakePvf2(1, 11025, 16, new[] { 0, 100, -100, 5000 });
    var entries = new PvfFormatDescriptor().List(new MemoryStream(pvf), null);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);

    using var output = new MemoryStream();
    new PvfFormatDescriptor().ExtractEntry(new MemoryStream(pvf), "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(11025u));
    var pcm = wav.AsSpan(44);
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm), Is.EqualTo(0));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[2..]), Is.EqualTo(100));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[4..]), Is.EqualTo(-100));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[6..]), Is.EqualTo(5000));
  }

  [Test]
  public void Create_FromMonoWav_RoundTripsExact() {
    var samples = new short[] { 0, 100, -100, 32767 };
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    var wav = PcmCodec.ToWavBlob(pcm, 1, 8000, 16);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wav) };

    using var output = new MemoryStream();
    new PvfFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var parsed = new PvfReader().Read(output.ToArray());

    Assert.That(parsed.Ascii, Is.False);
    Assert.That(parsed.SampleRate, Is.EqualTo(8000));
    Assert.That(parsed.NumChannels, Is.EqualTo(1));
    Assert.That(parsed.Bits, Is.EqualTo(16));
    Assert.That(parsed.Samples, Is.EqualTo(new[] { 0, 100, -100, 32767 }));
  }

  [Test]
  public void Create_FromStereoWavs_RoundTripsInterleaved() {
    byte[] Mono(params short[] s) {
      var b = new byte[s.Length * 2];
      for (var i = 0; i < s.Length; ++i) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2), s[i]);
      return PcmCodec.ToWavBlob(b, 1, 8000, 16);
    }
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("LEFT.wav", Mono(1, 2)),
      ArchiveInputInfo.InMemory("RIGHT.wav", Mono(-1, -2)),
    };

    using var output = new MemoryStream();
    new PvfFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var parsed = new PvfReader().Read(output.ToArray());

    Assert.That(parsed.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.Samples, Is.EqualTo(new[] { 1, -1, 2, -2 })); // interleaved L,R
  }
}
