#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Avr;

namespace Compression.Tests.Avr;

[TestFixture]
public class AvrTests {

  // Builds a 128-byte AVR header followed by sample data.
  private static byte[] MakeAvr(int channels, int bits, bool signed, int rate, byte[] sampleData) {
    var file = new byte[AvrReader.HeaderSize + sampleData.Length];
    var s = file.AsSpan();
    "2BIT"u8.ToArray().CopyTo(file, 0);
    "TEST"u8.ToArray().CopyTo(file, 4);
    BinaryPrimitives.WriteUInt16BigEndian(s[12..], (ushort)(channels == 2 ? 0xFFFF : 0));
    BinaryPrimitives.WriteUInt16BigEndian(s[14..], (ushort)bits);
    BinaryPrimitives.WriteUInt16BigEndian(s[16..], (ushort)(signed ? 0xFFFF : 0));
    BinaryPrimitives.WriteUInt16BigEndian(s[18..], 0);
    BinaryPrimitives.WriteUInt16BigEndian(s[20..], 0xFFFF);
    // High byte of rate is a flags byte; set it to verify it is masked off.
    BinaryPrimitives.WriteUInt32BigEndian(s[22..], 0xAB000000u | (uint)(rate & 0x00FFFFFF));
    BinaryPrimitives.WriteUInt32BigEndian(s[26..], (uint)(sampleData.Length / (bits / 8) / channels));
    sampleData.CopyTo(file, AvrReader.HeaderSize);
    return file;
  }

  private static byte[] Be16(params short[] samples) {
    var b = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(i * 2), samples[i]);
    return b;
  }

  [Test]
  public void Reader_MasksFlagsByteFromRate() {
    var avr = MakeAvr(1, 16, true, 44100, Be16(0, 100));
    var parsed = new AvrReader().Read(avr);
    Assert.That(parsed.SampleRate, Is.EqualTo(44100));
    Assert.That(parsed.NumChannels, Is.EqualTo(1));
    Assert.That(parsed.BitsPerSample, Is.EqualTo(16));
  }

  [Test]
  public void Mono16Signed_ListsFullAndMonoChannel() {
    var avr = MakeAvr(1, 16, true, 22050, Be16(0, 256, -256));
    using var ms = new MemoryStream(avr);
    var entries = new AvrFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.avr").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "MONO.wav").Kind, Is.EqualTo("Channel"));
  }

  [Test]
  public void Mono16Signed_ChannelIsLittleEndianSignedWav() {
    var avr = MakeAvr(1, 16, true, 22050, Be16(0, 256, -256));
    using var output = new MemoryStream();
    new AvrFormatDescriptor().ExtractEntry(new MemoryStream(avr), "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16));

    var pcm = wav.AsSpan(44);
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm), Is.EqualTo(0));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[2..]), Is.EqualTo(256));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[4..]), Is.EqualTo(-256));
  }

  [Test]
  public void Stereo16Signed_SplitsIntoLeftAndRight() {
    // Interleaved BE: L0,R0,L1,R1.
    var avr = MakeAvr(2, 16, true, 44100, Be16(10, -10, 20, -20));
    using var ms = new MemoryStream(avr);
    var entries = new AvrFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);

    using var left = new MemoryStream();
    new AvrFormatDescriptor().ExtractEntry(new MemoryStream(avr), "LEFT.wav", left, null);
    var lp = left.ToArray().AsSpan(44);
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(lp), Is.EqualTo(10));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(lp[2..]), Is.EqualTo(20));
  }

  [Test]
  public void Mono8Unsigned_RebiasedToSigned8Wav() {
    // 8-bit unsigned AVR {128,138} → WAV 8-bit unsigned stays {128,138}.
    var avr = MakeAvr(1, 8, false, 8000, new byte[] { 128, 138 });
    using var output = new MemoryStream();
    new AvrFormatDescriptor().ExtractEntry(new MemoryStream(avr), "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(8));
    Assert.That(wav.AsSpan(44).ToArray(), Is.EqualTo(new byte[] { 128, 138 }));
  }

  [Test]
  public void Create_FromMonoWav_RoundTrips() {
    var wav = PcmCodec.ToWavBlob(Le16(0, 256, -256), 1, 22050, 16);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wav) };

    using var output = new MemoryStream();
    new AvrFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var parsed = new AvrReader().Read(output.ToArray());

    Assert.That(parsed.NumChannels, Is.EqualTo(1));
    Assert.That(parsed.BitsPerSample, Is.EqualTo(16));
    Assert.That(parsed.Signed, Is.True);
    Assert.That(parsed.SampleRate, Is.EqualTo(22050));

    var samples = parsed.SampleData;
    Assert.That(BinaryPrimitives.ReadInt16BigEndian(samples), Is.EqualTo(0));
    Assert.That(BinaryPrimitives.ReadInt16BigEndian(samples.AsSpan(2)), Is.EqualTo(256));
    Assert.That(BinaryPrimitives.ReadInt16BigEndian(samples.AsSpan(4)), Is.EqualTo(-256));
  }

  [Test]
  public void Create_FromStereoWavs_RoundTripsThroughReader() {
    var left = PcmCodec.ToWavBlob(Le16(10, 20), 1, 44100, 16);
    var right = PcmCodec.ToWavBlob(Le16(-10, -20), 1, 44100, 16);
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("LEFT.wav", left),
      ArchiveInputInfo.InMemory("RIGHT.wav", right),
    };

    using var output = new MemoryStream();
    new AvrFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    var avr = output.ToArray();

    // Re-read through the full descriptor and verify the split channels survive.
    using var l = new MemoryStream();
    new AvrFormatDescriptor().ExtractEntry(new MemoryStream(avr), "LEFT.wav", l, null);
    var lp = l.ToArray().AsSpan(44);
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(lp), Is.EqualTo(10));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(lp[2..]), Is.EqualTo(20));

    using var r = new MemoryStream();
    new AvrFormatDescriptor().ExtractEntry(new MemoryStream(avr), "RIGHT.wav", r, null);
    var rp = r.ToArray().AsSpan(44);
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(rp), Is.EqualTo(-10));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(rp[2..]), Is.EqualTo(-20));
  }

  private static byte[] Le16(params short[] samples) {
    var b = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2), samples[i]);
    return b;
  }
}
