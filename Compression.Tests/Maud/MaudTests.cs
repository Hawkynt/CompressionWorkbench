#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Maud;

namespace Compression.Tests.Maud;

[TestFixture]
public class MaudTests {

  private static byte[] Chunk(string id, byte[] body) {
    var pad = (body.Length & 1) == 1 ? 1 : 0;
    var r = new byte[8 + body.Length + pad];
    System.Text.Encoding.ASCII.GetBytes(id).CopyTo(r, 0);
    BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4), (uint)body.Length);
    body.CopyTo(r, 8);
    return r;
  }

  private static byte[] Mhdr(uint sampleCount, int bits, int rate, int channelInfo,
      int numChannels, int compression) {
    var b = new byte[32];
    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0), sampleCount);
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)bits);
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), (ushort)bits);
    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), (uint)rate);
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(12), 1); // rate divide
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(14), (ushort)channelInfo);
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(16), (ushort)numChannels);
    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(18), (ushort)compression);
    return b;
  }

  private static byte[] Form(params byte[][] chunks) {
    using var inner = new MemoryStream();
    foreach (var c in chunks) inner.Write(c);
    var innerBytes = inner.ToArray();
    var r = new byte[12 + innerBytes.Length];
    "FORM"u8.ToArray().CopyTo(r, 0);
    BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4), (uint)(4 + innerBytes.Length));
    "MAUD"u8.ToArray().CopyTo(r, 8);
    innerBytes.CopyTo(r, 12);
    return r;
  }

  private static byte[] Be16(params short[] samples) {
    var b = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(i * 2), samples[i]);
    return b;
  }

  private static byte[] MakeMono16() =>
    Form(Chunk("MHDR", Mhdr(4, 16, 8000, MaudReader.ChannelInfoMono, 1, MaudReader.CompressionNone)),
         Chunk("MDAT", Be16(0, 100, -100, 32767)));

  private static byte[] MakeStereo16() =>
    // interleaved L,R: (1,-1),(2,-2)
    Form(Chunk("MHDR", Mhdr(2, 16, 22050, MaudReader.ChannelInfoStereo, 2, MaudReader.CompressionNone)),
         Chunk("MDAT", Be16(1, -1, 2, -2)));

  private static byte[] MakeUlawMono() {
    // μ-law encode three known PCM samples, then verify decode matches.
    var pcm = new short[] { 0, 1000, -1000 };
    var ulaw = Codec.MuLaw.MuLawCodec.Encode(pcm);
    return Form(Chunk("MHDR", Mhdr((uint)pcm.Length, 8, 8000, MaudReader.ChannelInfoMono, 1, MaudReader.CompressionULaw)),
                Chunk("MDAT", ulaw));
  }

  [Test]
  public void Mono_ListsFullAndMonoChannel() {
    using var ms = new MemoryStream(MakeMono16());
    var entries = new MaudFormatDescriptor().List(ms, null);
    Assert.That(entries.First(e => e.Name == "FULL.maud").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "MONO.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Mono_ChannelIsValidMonoWavWithLittleEndianSamples() {
    using var ms = new MemoryStream(MakeMono16());
    using var output = new MemoryStream();
    new MaudFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
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
  public void Stereo_SplitsIntoLeftAndRight() {
    using var ms = new MemoryStream(MakeStereo16());
    var entries = new MaudFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);

    using var left = new MemoryStream();
    new MaudFormatDescriptor().ExtractEntry(new MemoryStream(MakeStereo16()), "LEFT.wav", left, null);
    var l = left.ToArray().AsSpan(44);
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(l), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(l[2..]), Is.EqualTo(2));

    using var right = new MemoryStream();
    new MaudFormatDescriptor().ExtractEntry(new MemoryStream(MakeStereo16()), "RIGHT.wav", right, null);
    var r = right.ToArray().AsSpan(44);
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(r), Is.EqualTo(-1));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(r[2..]), Is.EqualTo(-2));
  }

  [Test]
  public void Ulaw_DecodesToSixteenBitMonoWav() {
    using var ms = new MemoryStream(MakeUlawMono());
    using var output = new MemoryStream();
    new MaudFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16));

    var expected = Codec.MuLaw.MuLawCodec.Decode(
      Codec.MuLaw.MuLawCodec.Encode(new short[] { 0, 1000, -1000 }));
    var pcm = wav.AsSpan(44);
    for (var i = 0; i < expected.Length; ++i)
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[(i * 2)..]), Is.EqualTo(expected[i]));
  }

  [Test]
  public void Create_FromMonoWav_RoundTripsExact() {
    var samples = Be16(0, 100, -100, 32767); // expected MDAT bytes (BE)
    var wavPcm = new byte[8];
    BinaryPrimitives.WriteInt16LittleEndian(wavPcm.AsSpan(0), 0);
    BinaryPrimitives.WriteInt16LittleEndian(wavPcm.AsSpan(2), 100);
    BinaryPrimitives.WriteInt16LittleEndian(wavPcm.AsSpan(4), -100);
    BinaryPrimitives.WriteInt16LittleEndian(wavPcm.AsSpan(6), 32767);
    var wav = PcmCodec.ToWavBlob(wavPcm, 1, 8000, 16);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wav) };

    using var output = new MemoryStream();
    new MaudFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var parsed = new MaudReader().Read(output.ToArray());

    Assert.That(parsed.SampleRate, Is.EqualTo(8000));
    Assert.That(parsed.NumChannels, Is.EqualTo(1));
    Assert.That(parsed.Data, Is.EqualTo(samples));
  }

  [Test]
  public void Create_FromStereoWavs_RoundTripsInterleaved() {
    byte[] Mono(params short[] s) {
      var b = new byte[s.Length * 2];
      for (var i = 0; i < s.Length; ++i) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2), s[i]);
      return PcmCodec.ToWavBlob(b, 1, 22050, 16);
    }
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("LEFT.wav", Mono(1, 2)),
      ArchiveInputInfo.InMemory("RIGHT.wav", Mono(-1, -2)),
    };

    using var output = new MemoryStream();
    new MaudFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var parsed = new MaudReader().Read(output.ToArray());

    Assert.That(parsed.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.Data, Is.EqualTo(Be16(1, -1, 2, -2))); // interleaved L,R
  }
}
