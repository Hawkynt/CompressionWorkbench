#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Far;

namespace Compression.Tests.Far;

[TestFixture]
public class FarTests {

  private const int Sample1Len = 6;

  private static byte[] MakeSyntheticFar() {
    const int headerLen = 110; // arbitrary span covering header + orders + message
    const ushort messageLen = 5;

    // header[110] | bitfield[8] | sampleHeader[48] | sampleData[6]
    var total = headerLen + 8 + 48 + Sample1Len;
    var buf = new byte[total];

    buf[0] = (byte)'F'; buf[1] = (byte)'A'; buf[2] = (byte)'R'; buf[3] = 0xFE;
    var name = Encoding.ASCII.GetBytes("SynthFar");
    Buffer.BlockCopy(name, 0, buf, 4, name.Length);
    buf[44] = 13; buf[45] = 10; buf[46] = 26;        // eof marker
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(47, 2), headerLen);
    buf[49] = 0xD1;                                   // version
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(96, 2), messageLen);
    var msg = Encoding.ASCII.GetBytes("Hello");
    Buffer.BlockCopy(msg, 0, buf, 98, msg.Length);

    // Sample section at headerLen: bitfield with only slot 0 present.
    var bf = headerLen;
    buf[bf] = 0x01; // slot 0 present

    // 48-byte sample header for slot 0.
    var sh = bf + 8;
    var sName = Encoding.ASCII.GetBytes("SignedSmp");
    Buffer.BlockCopy(sName, 0, buf, sh, sName.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sh + 32, 4), Sample1Len);
    buf[sh + 37] = 64;       // volume
    buf[sh + 46] = 0;        // type: 8-bit

    // Sample data: 8-bit SIGNED values.
    var dataOff = sh + 48;
    sbyte[] src = [-128, -64, 0, 63, 64, 127];
    for (var i = 0; i < Sample1Len; ++i) buf[dataOff + i] = (byte)src[i];

    return buf;
  }

  [Test]
  public void List_SurfacesContainerMessageAndSampleWav() {
    var entries = new FarFormatDescriptor().List(new MemoryStream(MakeSyntheticFar()), null);
    Assert.That(entries.Any(e => e.Name == "FULL.far"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "message.txt"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/01_") && e.Name.EndsWith(".wav")), Is.True);
  }

  [Test]
  public void Extract_SignedSampleConvertedToUnsigned8Wav() {
    var tmp = Path.Combine(Path.GetTempPath(), "far_" + Guid.NewGuid().ToString("N"));
    try {
      new FarFormatDescriptor().Extract(new MemoryStream(MakeSyntheticFar()), tmp, null, null);
      var wav = File.ReadAllBytes(Directory.GetFiles(Path.Combine(tmp, "samples")).Single());
      Assert.That(Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34, 2)), Is.EqualTo(8));
      var data = wav.AsSpan(44).ToArray();
      sbyte[] src = [-128, -64, 0, 63, 64, 127];
      Assert.That(data.Length, Is.EqualTo(Sample1Len));
      for (var i = 0; i < Sample1Len; ++i)
        Assert.That(data[i], Is.EqualTo((byte)(src[i] + 128)));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void GracefulFallback_GarbageYieldsFullOnly() {
    var entries = new FarFormatDescriptor().List(new MemoryStream(Encoding.ASCII.GetBytes("not a far file")), null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.far"));
  }
}
