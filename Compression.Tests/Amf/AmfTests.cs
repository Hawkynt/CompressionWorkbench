#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Amf;

namespace Compression.Tests.Amf;

[TestFixture]
public class AmfTests {

  private const int Sample1Len = 6;
  private const int Sample1C2Spd = 16000;

  private static byte[] MakeSyntheticAmf() {
    const byte version = 14;
    const byte numSamples = 1;
    const byte numOrders = 1;
    const ushort numTracks = 1;
    const byte numChannels = 4;

    var headerEnd = 41;
    var channelMap = numChannels;
    var orderLengths = numOrders * 2;
    var orderTracks = numOrders * numChannels * 2;
    var sampleHeaders = numSamples * 65;
    var total = headerEnd + channelMap + orderLengths + orderTracks + sampleHeaders + Sample1Len;
    var buf = new byte[total];

    buf[0] = (byte)'A'; buf[1] = (byte)'M'; buf[2] = (byte)'F'; buf[3] = version;
    var title = Encoding.ASCII.GetBytes("SynthAmf");
    Buffer.BlockCopy(title, 0, buf, 4, title.Length);
    buf[36] = numSamples;
    buf[37] = numOrders;
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(38, 2), numTracks);
    buf[40] = numChannels;

    var shOff = headerEnd + channelMap + orderLengths + orderTracks;
    // Sample header 0: type(1) name(32) dosName(13) index(4) length(4)@50 c2spd(2)@54 volume(1) loopStart(4) loopEnd(4)
    var sName = Encoding.ASCII.GetBytes("UnsignedSmp");
    Buffer.BlockCopy(sName, 0, buf, shOff + 1, sName.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(shOff + 50, 4), Sample1Len);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(shOff + 54, 2), Sample1C2Spd);
    buf[shOff + 56] = 64; // volume

    var dataOff = shOff + 65;
    for (var i = 0; i < Sample1Len; ++i) buf[dataOff + i] = (byte)(i * 32); // 8-bit unsigned

    return buf;
  }

  [Test]
  public void List_SurfacesContainerAndSampleWav() {
    var entries = new AmfFormatDescriptor().List(new MemoryStream(MakeSyntheticAmf()), null);
    Assert.That(entries.Any(e => e.Name == "FULL.amf"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/01_") && e.Name.EndsWith(".wav")), Is.True);
  }

  [Test]
  public void Extract_UnsignedSampleVerbatimWithC2SpdRate() {
    var tmp = Path.Combine(Path.GetTempPath(), "amf_" + Guid.NewGuid().ToString("N"));
    try {
      new AmfFormatDescriptor().Extract(new MemoryStream(MakeSyntheticAmf()), tmp, null, null);
      var wav = File.ReadAllBytes(Directory.GetFiles(Path.Combine(tmp, "samples")).Single());
      Assert.That(Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34, 2)), Is.EqualTo(8));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24, 4)), Is.EqualTo((uint)Sample1C2Spd));
      var data = wav.AsSpan(44).ToArray();
      Assert.That(data.Length, Is.EqualTo(Sample1Len));
      for (var i = 0; i < Sample1Len; ++i)
        Assert.That(data[i], Is.EqualTo((byte)(i * 32)));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void GracefulFallback_OldVersionYieldsFullOnly() {
    // Version 1 (< 10) → FULL + metadata only, no samples.
    var buf = new byte[40];
    buf[0] = (byte)'A'; buf[1] = (byte)'M'; buf[2] = (byte)'F'; buf[3] = 1;
    var entries = new AmfFormatDescriptor().List(new MemoryStream(buf), null);
    Assert.That(entries.Any(e => e.Name == "FULL.amf"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/")), Is.False);
  }

  [Test]
  public void GracefulFallback_GarbageYieldsFullOnly() {
    var entries = new AmfFormatDescriptor().List(new MemoryStream(Encoding.ASCII.GetBytes("XYZ junk")), null);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/")), Is.False);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.amf"));
  }
}
