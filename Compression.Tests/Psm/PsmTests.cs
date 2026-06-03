#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Psm;

namespace Compression.Tests.Psm;

[TestFixture]
public class PsmTests {

  private const int Sample1Len = 6;
  private const int Sample1C2Freq = 22050;
  private static readonly sbyte[] DeltaBytes = [7, -2, 50, -100, 3, 12];

  private static void WriteChunk(MemoryStream ms, string id, byte[] body) {
    ms.Write(Encoding.ASCII.GetBytes(id));
    Span<byte> len = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)body.Length);
    ms.Write(len);
    ms.Write(body);
  }

  private static byte[] MakeDsmpBody() {
    var body = new byte[96 + Sample1Len];
    var name = Encoding.ASCII.GetBytes("DeltaSample");
    Buffer.BlockCopy(name, 0, body, 13, name.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(51, 4), Sample1Len);
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(70, 2), Sample1C2Freq);
    for (var i = 0; i < Sample1Len; ++i) body[96 + i] = (byte)DeltaBytes[i];
    return body;
  }

  private static byte[] MakeSyntheticPsm() {
    using var inner = new MemoryStream();
    WriteChunk(inner, "TITL", Encoding.ASCII.GetBytes("SynthPsm"));
    WriteChunk(inner, "SDFT", Encoding.ASCII.GetBytes("MAINSONG"));
    WriteChunk(inner, "PBOD", [1, 2, 3, 4]);
    WriteChunk(inner, "DSMP", MakeDsmpBody());
    var chunks = inner.ToArray();

    using var ms = new MemoryStream();
    ms.Write("PSM "u8);
    Span<byte> size = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(4 + chunks.Length));
    ms.Write(size);
    ms.Write("FILE"u8);
    ms.Write(chunks);
    return ms.ToArray();
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
  public void List_SurfacesContainerTitlePatternAndSampleWav() {
    var entries = new PsmFormatDescriptor().List(new MemoryStream(MakeSyntheticPsm()), null);
    Assert.That(entries.Any(e => e.Name == "FULL.psm"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "title.txt"), Is.True);
    Assert.That(entries.Any(e => e.Name == "patterns/pattern_00.bin"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/01_") && e.Name.EndsWith(".wav")), Is.True);
  }

  [Test]
  public void Extract_DeltaSampleDecodedToUnsigned8WithC2FreqRate() {
    var tmp = Path.Combine(Path.GetTempPath(), "psm_" + Guid.NewGuid().ToString("N"));
    try {
      new PsmFormatDescriptor().Extract(new MemoryStream(MakeSyntheticPsm()), tmp, null, null);
      var wav = File.ReadAllBytes(Directory.GetFiles(Path.Combine(tmp, "samples")).Single());
      Assert.That(Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34, 2)), Is.EqualTo(8));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24, 4)), Is.EqualTo((uint)Sample1C2Freq));
      var data = wav.AsSpan(44).ToArray();
      Assert.That(data, Is.EqualTo(ExpectedUnsigned8()));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void GracefulFallback_GarbageYieldsFullOnly() {
    var entries = new PsmFormatDescriptor().List(new MemoryStream(Encoding.ASCII.GetBytes("PSM NOPE")), null);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/")), Is.False);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.psm"));
  }
}
