#pragma warning disable CS1591
using System.Buffers.Binary;
using FileFormat.EspsSd;

namespace Compression.Tests.Audio;

[TestFixture]
public class EspsSdTests {

  // Builds an ESPS .sd file. headerSize bytes of header (with the check code at +16
  // and the data offset at +8) followed by 16-bit samples in the chosen byte order.
  // When recordFreq is non-null the "record_freq" tag + its IEEE double are embedded.
  private static byte[] MakeEsps(bool bigEndian, double? recordFreq, short[] samples) {
    const int headerSize = 64;
    var sampleBytes = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i) {
      if (bigEndian) BinaryPrimitives.WriteInt16BigEndian(sampleBytes.AsSpan(i * 2), samples[i]);
      else BinaryPrimitives.WriteInt16LittleEndian(sampleBytes.AsSpan(i * 2), samples[i]);
    }

    var file = new byte[headerSize + sampleBytes.Length];
    var s = file.AsSpan();

    // data offset @ +8
    if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(s[8..], headerSize);
    else BinaryPrimitives.WriteUInt32LittleEndian(s[8..], headerSize);

    // check code @ +16
    if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(s[16..], EspsSdReader.CheckCode);
    else BinaryPrimitives.WriteUInt32LittleEndian(s[16..], EspsSdReader.CheckCode);

    if (recordFreq is { } freq) {
      var tag = "record_freq"u8;
      var pos = 24;
      tag.CopyTo(s[pos..]);
      pos += tag.Length;
      var bits = (ulong)BitConverter.DoubleToInt64Bits(freq);
      if (bigEndian) BinaryPrimitives.WriteUInt64BigEndian(s[pos..], bits);
      else BinaryPrimitives.WriteUInt64LittleEndian(s[pos..], bits);
    }

    sampleBytes.CopyTo(s[headerSize..]);
    return file;
  }

  [Test]
  public void BigEndian_WithRecordFreq_DecodesSamplesAndRate() {
    var blob = MakeEsps(bigEndian: true, recordFreq: 16000, new short[] { 0, 256, -256, 4096 });
    var parsed = new EspsSdReader().Read(blob);
    Assert.That(parsed.BigEndian, Is.True);
    Assert.That(parsed.SampleRate, Is.EqualTo(16000));
    Assert.That(parsed.RateFromHeader, Is.True);

    using var output = new MemoryStream();
    new EspsSdFormatDescriptor().ExtractEntry(new MemoryStream(blob), "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(16000u));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16));
    var pcm = wav.AsSpan(44);
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm), Is.EqualTo(0));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[2..]), Is.EqualTo(256));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[4..]), Is.EqualTo(-256));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[6..]), Is.EqualTo(4096));
  }

  [Test]
  public void LittleEndian_WithRecordFreq_DecodesSamplesAndRate() {
    var blob = MakeEsps(bigEndian: false, recordFreq: 8000, new short[] { 1, -1, 100, -100 });
    var parsed = new EspsSdReader().Read(blob);
    Assert.That(parsed.BigEndian, Is.False);
    Assert.That(parsed.SampleRate, Is.EqualTo(8000));

    using var output = new MemoryStream();
    new EspsSdFormatDescriptor().ExtractEntry(new MemoryStream(blob), "MONO.wav", output, null);
    var pcm = output.ToArray().AsSpan(44);
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[2..]), Is.EqualTo(-1));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[4..]), Is.EqualTo(100));
  }

  [Test]
  public void RecordFreqAbsent_DefaultsTo16000WithNote() {
    var blob = MakeEsps(bigEndian: true, recordFreq: null, new short[] { 0, 1, 2 });
    var parsed = new EspsSdReader().Read(blob);
    Assert.That(parsed.SampleRate, Is.EqualTo(16000));
    Assert.That(parsed.RateFromHeader, Is.False);

    using var metaStream = new MemoryStream();
    new EspsSdFormatDescriptor().ExtractEntry(new MemoryStream(blob), "metadata.ini", metaStream, null);
    var meta = System.Text.Encoding.UTF8.GetString(metaStream.ToArray());
    Assert.That(meta, Does.Contain("sample_rate=16000"));
    Assert.That(meta, Does.Contain("sample_rate_source=default"));
  }

  [Test]
  public void SurfacesFullAndMono() {
    var blob = MakeEsps(bigEndian: true, recordFreq: 16000, new short[] { 0, 1 });
    var entries = new EspsSdFormatDescriptor().List(new MemoryStream(blob), null);
    Assert.That(entries.Any(e => e.Name == "FULL.sd" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
  }

  [Test]
  public void MissingCheckCode_Throws() {
    var blob = new byte[64 + 4]; // all zero — no check code
    Assert.Throws<InvalidDataException>(() =>
      new EspsSdReader().Read(blob));
  }
}
