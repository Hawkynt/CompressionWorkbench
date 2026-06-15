#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Paf;

namespace Compression.Tests.Paf;

[TestFixture]
public class PafTests {

  private static byte[] MakePaf(bool littleEndian, int rate, int format, int channels, byte[] data) {
    var file = new byte[PafReader.DataOffset + data.Length];
    void W(int offset, uint v) {
      if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(offset), v);
      else BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(offset), v);
    }
    (littleEndian ? "fap "u8 : " paf"u8).CopyTo(file);
    W(4, 0);                                  // version
    W(8, (uint)(littleEndian ? 1 : 0));       // endianness
    W(12, (uint)rate);
    W(16, (uint)format);
    W(20, (uint)channels);
    data.CopyTo(file.AsSpan(PafReader.DataOffset));
    return file;
  }

  private static byte[] InterleavedLe16(params short[] s) {
    var b = new byte[s.Length * 2];
    for (var i = 0; i < s.Length; ++i) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2), s[i]);
    return b;
  }

  private static byte[] InterleavedBe16(params short[] s) {
    var b = new byte[s.Length * 2];
    for (var i = 0; i < s.Length; ++i) BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(i * 2), s[i]);
    return b;
  }

  [Test]
  public void LittleEndianMono_ListsFullAndMonoChannel() {
    var paf = MakePaf(true, 44100, PafReader.FormatPcm16, 1, InterleavedLe16(0, 100, -100, 32767));
    var entries = new PafFormatDescriptor().List(new MemoryStream(paf), null);
    Assert.That(entries.First(e => e.Name == "FULL.paf").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "MONO.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void LittleEndianMono_ChannelIsValidMonoWav() {
    var paf = MakePaf(true, 44100, PafReader.FormatPcm16, 1, InterleavedLe16(0, 100, -100, 32767));
    using var output = new MemoryStream();
    new PafFormatDescriptor().ExtractEntry(new MemoryStream(paf), "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(44100u));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16));
    Assert.That(wav.AsSpan(44).ToArray(), Is.EqualTo(InterleavedLe16(0, 100, -100, 32767)));
  }

  [Test]
  public void BigEndianStereo_SplitsAndByteSwaps() {
    // " paf" big-endian file, interleaved L,R: (1,-1),(2,-2)
    var paf = MakePaf(false, 22050, PafReader.FormatPcm16, 2, InterleavedBe16(1, -1, 2, -2));
    var entries = new PafFormatDescriptor().List(new MemoryStream(paf), null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);

    using var left = new MemoryStream();
    new PafFormatDescriptor().ExtractEntry(new MemoryStream(paf), "LEFT.wav", left, null);
    var l = left.ToArray().AsSpan(44);
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(l), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(l[2..]), Is.EqualTo(2));

    using var right = new MemoryStream();
    new PafFormatDescriptor().ExtractEntry(new MemoryStream(paf), "RIGHT.wav", right, null);
    var r = right.ToArray().AsSpan(44);
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(r), Is.EqualTo(-1));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(r[2..]), Is.EqualTo(-2));
  }

  [Test]
  public void Create_FromMonoWav_RoundTripsExact() {
    var pcm = InterleavedLe16(0, 100, -100, 32767);
    var wav = PcmCodec.ToWavBlob(pcm, 1, 44100, 16);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wav) };

    using var output = new MemoryStream();
    new PafFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var parsed = new PafReader().Read(output.ToArray());

    Assert.That(parsed.LittleEndian, Is.True);
    Assert.That(parsed.SampleRate, Is.EqualTo(44100));
    Assert.That(parsed.NumChannels, Is.EqualTo(1));
    Assert.That(parsed.Format, Is.EqualTo(PafReader.FormatPcm16));
    Assert.That(parsed.Data, Is.EqualTo(pcm));
  }

  [Test]
  public void Create_FromStereoWavs_RoundTripsInterleaved() {
    byte[] Mono(params short[] s) => PcmCodec.ToWavBlob(InterleavedLe16(s), 1, 48000, 16);
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("LEFT.wav", Mono(1, 2)),
      ArchiveInputInfo.InMemory("RIGHT.wav", Mono(-1, -2)),
    };

    using var output = new MemoryStream();
    new PafFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var parsed = new PafReader().Read(output.ToArray());

    Assert.That(parsed.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.Data, Is.EqualTo(InterleavedLe16(1, -1, 2, -2)));
  }
}
