#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Mtm;

namespace Compression.Tests.Mtm;

[TestFixture]
public class MtmTests {

  // Two samples: #1 carries 8-bit unsigned data, #2 is empty.
  private const int Sample1Len = 8;

  private static byte[] MakeSyntheticMtm() {
    const int numSamples = 2;
    const int numTracks = 1;     // one stored track block (192 bytes)
    const byte lastPattern = 0;  // 1 pattern → grid = 1 * 32 * 2 = 64 bytes
    const ushort commentLen = 0;

    var headerSize = 66;
    var sampleHeaders = numSamples * 37;
    var orderTable = 128;
    var trackData = numTracks * 192;
    var patternGrid = (lastPattern + 1) * 32 * 2;
    var sampleData = Sample1Len;
    var total = headerSize + sampleHeaders + orderTable + trackData + patternGrid + commentLen + sampleData;

    var buf = new byte[total];
    buf[0] = (byte)'M'; buf[1] = (byte)'T'; buf[2] = (byte)'M'; buf[3] = 0x10;
    var name = Encoding.ASCII.GetBytes("SynthMtm");
    Buffer.BlockCopy(name, 0, buf, 4, name.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(24, 2), numTracks);
    buf[26] = lastPattern;
    buf[27] = 0;                 // lastOrder
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(28, 2), commentLen);
    buf[30] = numSamples;
    buf[31] = 0;                 // attribute
    buf[32] = 64;                // beatsPerTrack
    buf[33] = 4;                 // numChannels

    // Sample header 0 (with data).
    var sh0 = headerSize;
    var s0Name = Encoding.ASCII.GetBytes("UnsignedSmp");
    Buffer.BlockCopy(s0Name, 0, buf, sh0, s0Name.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sh0 + 22, 4), Sample1Len);
    buf[sh0 + 35] = 64;          // volume
    buf[sh0 + 36] = 0;           // attribute: 8-bit
    // Sample header 1 (empty: length 0).
    var sh1 = headerSize + 37;
    var s1Name = Encoding.ASCII.GetBytes("Empty");
    Buffer.BlockCopy(s1Name, 0, buf, sh1, s1Name.Length);

    // Sample data at the very end (8-bit unsigned ramp).
    var dataOff = headerSize + sampleHeaders + orderTable + trackData + patternGrid + commentLen;
    for (var i = 0; i < Sample1Len; ++i) buf[dataOff + i] = (byte)(i * 16);

    return buf;
  }

  [Test]
  public void List_SurfacesContainerMetadataTrackAndSampleWav() {
    var entries = new MtmFormatDescriptor().List(new MemoryStream(MakeSyntheticMtm()), null);
    Assert.That(entries.Any(e => e.Name == "FULL.mtm"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "patterns/track_01.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/01_") && e.Name.EndsWith(".wav")), Is.True);
    // Sample 2 is empty → not surfaced.
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/02_")), Is.False);
  }

  [Test]
  public void Extract_SampleWavCarriesUnsignedPcmVerbatim() {
    var tmp = Path.Combine(Path.GetTempPath(), "mtm_" + Guid.NewGuid().ToString("N"));
    try {
      new MtmFormatDescriptor().Extract(new MemoryStream(MakeSyntheticMtm()), tmp, null, null);
      var wavPath = Directory.GetFiles(Path.Combine(tmp, "samples")).Single();
      var wav = File.ReadAllBytes(wavPath);
      // RIFF/WAVE header + unsigned-8 data.
      Assert.That(Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
      Assert.That(Encoding.ASCII.GetString(wav, 8, 4), Is.EqualTo("WAVE"));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34, 2)), Is.EqualTo(8)); // bits
      var data = wav.AsSpan(44).ToArray();
      Assert.That(data.Length, Is.EqualTo(Sample1Len));
      for (var i = 0; i < Sample1Len; ++i)
        Assert.That(data[i], Is.EqualTo((byte)(i * 16)));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void GracefulFallback_GarbageYieldsFullOnly() {
    var entries = new MtmFormatDescriptor().List(new MemoryStream(Encoding.ASCII.GetBytes("not an mtm")), null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.mtm"));
  }
}
