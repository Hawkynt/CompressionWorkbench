#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Stm;

namespace Compression.Tests.Stm;

[TestFixture]
public class StmTests {

  private const int InstrumentCount = 31;
  private const int Sample1Len = 6;
  private const int Sample1C2Spd = 11025;

  private static byte[] MakeSyntheticStm() {
    const byte numPatterns = 1;
    var headerSize = 48;
    var instrTable = InstrumentCount * 32;
    var orderTable = 128;
    var patterns = numPatterns * 1024;
    var total = headerSize + instrTable + orderTable + patterns + Sample1Len;
    var buf = new byte[total];

    var songName = Encoding.ASCII.GetBytes("SynthStm");
    Buffer.BlockCopy(songName, 0, buf, 0, songName.Length);
    var tag = Encoding.ASCII.GetBytes("!Scream!");
    Buffer.BlockCopy(tag, 0, buf, 20, tag.Length);
    buf[28] = 0x1A;
    buf[29] = 2;      // fileType = module
    buf[30] = 2;      // verMajor
    buf[31] = 21;     // verMinor
    buf[32] = 96;     // initTempo
    buf[33] = numPatterns;
    buf[34] = 64;     // globalVolume

    // Instrument 0 (with data), the rest empty.
    var i0 = headerSize;
    var fn = Encoding.ASCII.GetBytes("SIGNED.SMP");
    Buffer.BlockCopy(fn, 0, buf, i0, fn.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(i0 + 16, 2), Sample1Len);   // length
    buf[i0 + 24] = 64;                                                              // volume
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(i0 + 26, 2), Sample1C2Spd); // c2spd

    // Sample data: 8-bit signed.
    var dataOff = headerSize + instrTable + orderTable + patterns;
    sbyte[] src = [-128, -1, 0, 1, 63, 127];
    for (var i = 0; i < Sample1Len; ++i) buf[dataOff + i] = (byte)src[i];

    return buf;
  }

  [Test]
  public void List_SurfacesContainerPatternAndSampleWav() {
    var entries = new StmFormatDescriptor().List(new MemoryStream(MakeSyntheticStm()), null);
    Assert.That(entries.Any(e => e.Name == "FULL.stm"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "patterns/pattern_00.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/01_") && e.Name.EndsWith(".wav")), Is.True);
  }

  [Test]
  public void Extract_SignedSampleConvertedToUnsigned8WithC2SpdRate() {
    var tmp = Path.Combine(Path.GetTempPath(), "stm_" + Guid.NewGuid().ToString("N"));
    try {
      new StmFormatDescriptor().Extract(new MemoryStream(MakeSyntheticStm()), tmp, null, null);
      var wav = File.ReadAllBytes(Directory.GetFiles(Path.Combine(tmp, "samples")).Single());
      Assert.That(Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34, 2)), Is.EqualTo(8));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24, 4)), Is.EqualTo((uint)Sample1C2Spd));
      var data = wav.AsSpan(44).ToArray();
      sbyte[] src = [-128, -1, 0, 1, 63, 127];
      Assert.That(data.Length, Is.EqualTo(Sample1Len));
      for (var i = 0; i < Sample1Len; ++i)
        Assert.That(data[i], Is.EqualTo((byte)(src[i] + 128)));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void GracefulFallback_GarbageYieldsFullOnly() {
    var entries = new StmFormatDescriptor().List(new MemoryStream(new byte[64]), null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.stm"));
  }
}
