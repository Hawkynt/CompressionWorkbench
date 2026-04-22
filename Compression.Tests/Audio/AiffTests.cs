#pragma warning disable CS1591
using System.Buffers.Binary;
using FileFormat.Aiff;

namespace Compression.Tests.Audio;

[TestFixture]
public class AiffTests {

  // ── Helpers ──────────────────────────────────────────────────────────

  /// <summary>
  /// Encodes a non-negative integer sample rate into the 80-bit IEEE 754 extended
  /// form that AIFF COMM expects. Only covers positive finite values we actually use
  /// in the synthetic fixtures.
  /// </summary>
  private static void WriteExtendedFloat(Span<byte> dst, long value) {
    if (value == 0) { dst.Clear(); return; }
    // Locate high bit.
    var v = (ulong)value;
    var hiBit = 63;
    while ((v & (1UL << hiBit)) == 0) --hiBit;
    var exponent = 16383 + hiBit;
    // Shift mantissa so the high bit is bit 63 of the mantissa.
    var mantissa = v << (63 - hiBit);
    dst[0] = (byte)((exponent >> 8) & 0xFF);
    dst[1] = (byte)(exponent & 0xFF);
    for (var i = 0; i < 8; ++i)
      dst[2 + i] = (byte)((mantissa >> ((7 - i) * 8)) & 0xFF);
  }

  private static byte[] MakeStereoAiff() {
    // 4 sample frames, 16-bit PCM, stereo, 44100 Hz.
    const int frames = 4;
    const int channels = 2;
    const int bits = 16;
    var bytesPerFrame = channels * bits / 8;
    var soundData = new byte[frames * bytesPerFrame];
    // Big-endian 16-bit samples: L0=100, R0=-100, L1=200, R1=-200 …
    for (var i = 0; i < frames; ++i) {
      var lv = (short)((i + 1) * 100);
      var rv = (short)(-(i + 1) * 100);
      BinaryPrimitives.WriteInt16BigEndian(soundData.AsSpan(i * 4), lv);
      BinaryPrimitives.WriteInt16BigEndian(soundData.AsSpan(i * 4 + 2), rv);
    }

    // SSND chunk body: 4-byte offset (0) + 4-byte blockSize (0) + soundData.
    var ssndBody = new byte[8 + soundData.Length];
    soundData.CopyTo(ssndBody.AsSpan(8));

    // COMM chunk body: 2B channels + 4B frames + 2B bits + 10B sample-rate float = 18B.
    var commBody = new byte[18];
    BinaryPrimitives.WriteInt16BigEndian(commBody.AsSpan(0), (short)channels);
    BinaryPrimitives.WriteUInt32BigEndian(commBody.AsSpan(2), (uint)frames);
    BinaryPrimitives.WriteInt16BigEndian(commBody.AsSpan(6), (short)bits);
    WriteExtendedFloat(commBody.AsSpan(8, 10), 44100);

    // Assemble FORM/AIFF.
    var totalInnerSize = 4 /* AIFF */ + (8 + commBody.Length) + (8 + ssndBody.Length);
    var file = new byte[8 + totalInnerSize];
    var s = file.AsSpan();
    "FORM"u8.CopyTo(s);
    BinaryPrimitives.WriteUInt32BigEndian(s[4..], (uint)totalInnerSize);
    "AIFF"u8.CopyTo(s[8..]);
    var p = 12;
    "COMM"u8.CopyTo(s[p..]); p += 4;
    BinaryPrimitives.WriteUInt32BigEndian(s[p..], (uint)commBody.Length); p += 4;
    commBody.CopyTo(s[p..]); p += commBody.Length;
    "SSND"u8.CopyTo(s[p..]); p += 4;
    BinaryPrimitives.WriteUInt32BigEndian(s[p..], (uint)ssndBody.Length); p += 4;
    ssndBody.CopyTo(s[p..]);
    return file;
  }

  // ── Tests ────────────────────────────────────────────────────────────

  [Test]
  public void AiffReader_ParsesCommAndSsnd() {
    var blob = MakeStereoAiff();
    var parsed = new AiffReader().Read(blob);
    Assert.That(parsed.NumChannels, Is.EqualTo(2));
    Assert.That(parsed.SampleRate, Is.EqualTo(44100));
    Assert.That(parsed.BitsPerSample, Is.EqualTo(16));
    Assert.That(parsed.SampleFrames, Is.EqualTo(4));
    Assert.That(parsed.IsAifc, Is.False);
    Assert.That(parsed.SoundData.Length, Is.EqualTo(4 * 4));
  }

  [Test]
  public void AiffDescriptor_SplitsChannels() {
    var blob = MakeStereoAiff();
    using var ms = new MemoryStream(blob);
    var entries = new AiffFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.aif"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void AiffDescriptor_ExtractedChannelIsMonoWav() {
    var blob = MakeStereoAiff();
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new AiffFormatDescriptor().ExtractEntry(ms, "LEFT.wav", output, null);
    var wav = output.ToArray();
    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    // Channels at offset 22 in the RIFF fmt chunk.
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1));
    // Sample rate preserved at offset 24.
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(44100u));
  }

  [Test]
  public void ExtendedFloat_DecodesBackToRoundNumbers() {
    Span<byte> buf = stackalloc byte[10];
    WriteExtendedFloat(buf, 8000);
    Assert.That(AiffReader.Decode80BitFloatToInt(buf), Is.EqualTo(8000));
    WriteExtendedFloat(buf, 48000);
    Assert.That(AiffReader.Decode80BitFloatToInt(buf), Is.EqualTo(48000));
  }
}
