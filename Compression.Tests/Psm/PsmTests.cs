#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Psm;

namespace Compression.Tests.Psm;

[TestFixture]
public class PsmTests {

  // Builds a minimal new-format PSM: "PSM " + "FILE" wrapper + TITL + PBOD + DSMP.
  private static byte[] MakeSyntheticPsm() {
    using var ms = new MemoryStream();
    void Chunk(string tag, byte[] payload) {
      ms.Write(Encoding.ASCII.GetBytes(tag));
      var len = new byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)payload.Length);
      ms.Write(len);
      ms.Write(payload);
      if (payload.Length % 2 == 1) ms.WriteByte(0);
    }
    ms.Write("PSM "u8);
    ms.Write("FILE"u8);
    Chunk("TITL", Encoding.ASCII.GetBytes("PsmSong"));
    Chunk("PBOD", [1, 2, 3, 4]);
    Chunk("DSMP", [0x10, 0x20, 0x30, 0x40]);
    return ms.ToArray();
  }

  private static byte[] MakeOldPsm() {
    var buf = new byte[64];
    buf[0] = (byte)'P'; buf[1] = (byte)'S'; buf[2] = (byte)'M'; buf[3] = 0xFE;
    var name = Encoding.ASCII.GetBytes("OldPsmName");
    Buffer.BlockCopy(name, 0, buf, 4, name.Length);
    return buf;
  }

  [Test]
  public void List_NewFormat_ExposesChunksPatternsSamples() {
    using var ms = new MemoryStream(MakeSyntheticPsm());
    var entries = new PsmFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.psm"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("patterns/pattern_")), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("samples/")), Is.True);
  }

  [Test]
  public void Extract_NewFormat_FullByteIdentical_TitleParsed() {
    var blob = MakeSyntheticPsm();
    var tmp = Path.Combine(Path.GetTempPath(), "psm_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new PsmFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.psm")), Is.EqualTo(blob));
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("variant = new"));
      Assert.That(meta, Does.Contain("title = PsmSong"));
      Assert.That(meta, Does.Contain("num_patterns = 1"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void List_OldFormat_DetectedAndNamed() {
    using var ms = new MemoryStream(MakeOldPsm());
    var entries = new PsmFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.psm"), Is.True);
    var meta = entries.First(e => e.Name == "metadata.ini");
    Assert.That(meta.OriginalSize, Is.GreaterThan(0));
  }

  [Test]
  public void List_Malformed_DoesNotThrow() {
    using var ms = new MemoryStream([(byte)'P', (byte)'S', (byte)'M', (byte)' ', 0xFF]);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new PsmFormatDescriptor().List(ms, null));
    Assert.That(entries.Any(e => e.Name == "FULL.psm"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Detection_MagicBothVariants() {
    var d = new PsmFormatDescriptor();
    Assert.That(d.MagicSignatures.Any(s => s.Bytes.SequenceEqual("PSM "u8.ToArray())), Is.True);
    Assert.That(d.MagicSignatures.Any(s => s.Bytes.SequenceEqual(new byte[] { 0x50, 0x53, 0x4D, 0xFE })), Is.True);
  }
}
