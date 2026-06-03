#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.G72x;
using FileFormat.Au;

namespace Compression.Tests.Audio;

[TestFixture]
public class AuTests {

  private static byte[] MakeMonoMuLawAu(string annotation = "") {
    // 24-byte header + optional annotation padded to 8-byte boundary + μ-law samples.
    var annoBytes = Encoding.ASCII.GetBytes(annotation);
    var padded = (annoBytes.Length + 7) & ~7;
    if (padded == 0) padded = 8; // always include at least an 8-byte "annotation" area per convention
    var dataOffset = 24 + padded;
    const int sampleCount = 16;
    var sound = new byte[sampleCount];
    for (var i = 0; i < sampleCount; ++i) sound[i] = (byte)(0xFF - i * 8);

    var file = new byte[dataOffset + sound.Length];
    var s = file.AsSpan();
    s[0] = (byte)'.'; s[1] = (byte)'s'; s[2] = (byte)'n'; s[3] = (byte)'d';
    BinaryPrimitives.WriteUInt32BigEndian(s[4..], (uint)dataOffset);
    BinaryPrimitives.WriteUInt32BigEndian(s[8..], (uint)sound.Length);
    BinaryPrimitives.WriteUInt32BigEndian(s[12..], 1); // μ-law
    BinaryPrimitives.WriteUInt32BigEndian(s[16..], 8000);
    BinaryPrimitives.WriteUInt32BigEndian(s[20..], 1); // mono
    annoBytes.CopyTo(s[24..]);
    sound.CopyTo(s[dataOffset..]);
    return file;
  }

  // Builds a mono .au with encoding 23 (G.721) by encoding a speech-like waveform.
  private static byte[] MakeMonoG721Au(out short[] original) {
    const int sampleCount = 400;
    original = new short[sampleCount];
    for (var i = 0; i < sampleCount; ++i) {
      var t = i / 8000.0;
      original[i] = (short)(7000 * Math.Sin(2 * Math.PI * 350 * t)
                            + 2500 * Math.Sin(2 * Math.PI * 900 * t));
    }
    var sound = G72xCodec.EncodeG721(original);

    const int dataOffset = 24;
    var file = new byte[dataOffset + sound.Length];
    var s = file.AsSpan();
    s[0] = (byte)'.'; s[1] = (byte)'s'; s[2] = (byte)'n'; s[3] = (byte)'d';
    BinaryPrimitives.WriteUInt32BigEndian(s[4..], dataOffset);
    BinaryPrimitives.WriteUInt32BigEndian(s[8..], (uint)sound.Length);
    BinaryPrimitives.WriteUInt32BigEndian(s[12..], 23);   // G.721 ADPCM
    BinaryPrimitives.WriteUInt32BigEndian(s[16..], 8000); // sample rate
    BinaryPrimitives.WriteUInt32BigEndian(s[20..], 1);    // mono
    sound.CopyTo(s[dataOffset..]);
    return file;
  }

  [Test]
  public void AuDescriptor_G721_SurfacesMonoWav() {
    var blob = MakeMonoG721Au(out _);
    using var ms = new MemoryStream(blob);
    var entries = new AuFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.au"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
  }

  [Test]
  public void AuDescriptor_G721_DecodesCloseToOriginal() {
    var blob = MakeMonoG721Au(out var original);
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new AuFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16)); // 16-bit out

    // PCM payload after the canonical 44-byte WAV header.
    long maxError = 0;
    for (var i = 50; i < original.Length; ++i) {
      var dec = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2));
      var e = Math.Abs(original[i] - dec);
      if (e > maxError) maxError = e;
    }
    Assert.That(maxError, Is.LessThan(2000), $"G.721 round-trip max error {maxError} too high");
  }

  [Test]
  public void AuReader_ParsesHeader() {
    var blob = MakeMonoMuLawAu("test tune");
    var parsed = new AuReader().Read(blob);
    Assert.That(parsed.Encoding, Is.EqualTo(1u));
    Assert.That(parsed.SampleRate, Is.EqualTo(8000));
    Assert.That(parsed.NumChannels, Is.EqualTo(1));
    Assert.That(parsed.SoundData.Length, Is.EqualTo(16));
    Assert.That(parsed.Annotation, Is.EqualTo("test tune"));
  }

  [Test]
  public void AuDescriptor_ListsFullPlusMono() {
    var blob = MakeMonoMuLawAu();
    using var ms = new MemoryStream(blob);
    var entries = new AuFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.au"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void AuDescriptor_ExtractsMonoWav_ContainsPcm() {
    var blob = MakeMonoMuLawAu();
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new AuFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    // Channels=1, sample rate=8000, bitsPerSample=16 (μ-law decoded up to 16-bit).
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(8000u));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16));
  }
}
