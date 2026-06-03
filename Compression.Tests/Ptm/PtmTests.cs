#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Ptm;

namespace Compression.Tests.Ptm;

[TestFixture]
public class PtmTests {

  private const int HeaderSize = 608;
  private const int InstrumentSize = 80;
  private const int Sample1Len = 6;
  private const int Sample1C4Spd = 8363;

  // Delta-encoded bytes; the running-sum decode must reproduce these signed values.
  private static readonly sbyte[] DeltaBytes = [10, 5, -3, 20, -30, 1];

  private static byte[] MakeSyntheticPtm() {
    const int nInstruments = 1;
    var dataOffset = HeaderSize + nInstruments * InstrumentSize;
    var total = dataOffset + Sample1Len;
    var buf = new byte[total];

    var name = Encoding.ASCII.GetBytes("SynthPtm");
    Buffer.BlockCopy(name, 0, buf, 0, name.Length);
    buf[28] = 0x1A;
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(32, 2), 1);            // nOrders
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(34, 2), nInstruments); // nInstruments
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(36, 2), 1);            // nPatterns
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(38, 2), 4);            // nChannels
    buf[44] = (byte)'P'; buf[45] = (byte)'T'; buf[46] = (byte)'M'; buf[47] = (byte)'F';

    // Instrument 0.
    var o = HeaderSize;
    buf[o] = 0x01; // type: sample present, 8-bit
    var dosName = Encoding.ASCII.GetBytes("DELTA.SMP");
    Buffer.BlockCopy(dosName, 0, buf, o + 1, dosName.Length);
    buf[o + 13] = 64;                                                        // volume
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o + 15, 2), Sample1C4Spd);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o + 19, 4), (uint)dataOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o + 23, 4), Sample1Len);
    var sName = Encoding.ASCII.GetBytes("DeltaSample");
    Buffer.BlockCopy(sName, 0, buf, o + 48, sName.Length);
    buf[o + 76] = (byte)'P'; buf[o + 77] = (byte)'T'; buf[o + 78] = (byte)'M'; buf[o + 79] = (byte)'S';

    // Delta-encoded sample data.
    for (var i = 0; i < Sample1Len; ++i) buf[dataOffset + i] = (byte)DeltaBytes[i];

    return buf;
  }

  private static byte[] ExpectedUnsigned8() {
    var result = new byte[Sample1Len];
    sbyte acc = 0;
    for (var i = 0; i < Sample1Len; ++i) {
      acc = unchecked((sbyte)(acc + DeltaBytes[i]));
      result[i] = (byte)(acc + 128);
    }
    return result;
  }

  [Test]
  public void List_SurfacesContainerAndSampleWav() {
    var entries = new PtmFormatDescriptor().List(new MemoryStream(MakeSyntheticPtm()), null);
    Assert.That(entries.Any(e => e.Name == "FULL.ptm"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/01_") && e.Name.EndsWith(".wav")), Is.True);
  }

  [Test]
  public void Extract_DeltaSampleDecodedToUnsigned8() {
    var tmp = Path.Combine(Path.GetTempPath(), "ptm_" + Guid.NewGuid().ToString("N"));
    try {
      new PtmFormatDescriptor().Extract(new MemoryStream(MakeSyntheticPtm()), tmp, null, null);
      var wav = File.ReadAllBytes(Directory.GetFiles(Path.Combine(tmp, "samples")).Single());
      Assert.That(Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34, 2)), Is.EqualTo(8));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24, 4)), Is.EqualTo((uint)Sample1C4Spd));
      var data = wav.AsSpan(44).ToArray();
      Assert.That(data, Is.EqualTo(ExpectedUnsigned8()));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void GracefulFallback_GarbageYieldsFullOnly() {
    var entries = new PtmFormatDescriptor().List(new MemoryStream(new byte[64]), null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.ptm"));
  }
}
