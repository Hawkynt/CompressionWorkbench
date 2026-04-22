using System.Text;
using Compression.Registry;
using FileFormat.WwiseBnk;

namespace Compression.Tests.WwiseBnk;

[TestFixture]
public class WwiseBnkTests {

  private static void WriteU32(Stream s, uint v) {
    Span<byte> b = stackalloc byte[4];
    b[0] = (byte)v; b[1] = (byte)(v >> 8); b[2] = (byte)(v >> 16); b[3] = (byte)(v >> 24);
    s.Write(b);
  }

  private static void WriteChunk(Stream s, string tag, byte[] data) {
    s.Write(Encoding.ASCII.GetBytes(tag));
    WriteU32(s, (uint)data.Length);
    s.Write(data);
  }

  /// <summary>
  /// Build a synthetic Wwise bank: BKHD + DIDX (one WEM entry) + DATA (one WEM blob) + HIRC (one object).
  /// </summary>
  private static byte[] BuildMinimalBnk(out uint wemId, out byte[] wemData, out uint bankVersion, out uint bankId) {
    bankVersion = 140;
    bankId = 0xABCDEF12;
    wemId = 0x11112222u;
    wemData = "FakeWEMBytes"u8.ToArray();

    var ms = new MemoryStream();

    // BKHD: version + bankId + some padding
    var bkhd = new MemoryStream();
    WriteU32(bkhd, bankVersion);
    WriteU32(bkhd, bankId);
    WriteU32(bkhd, 0); // language id
    WriteU32(bkhd, 0); // alignment
    WriteChunk(ms, "BKHD", bkhd.ToArray());

    // DIDX: 12-byte entry (wemId, offset, size)
    var didx = new MemoryStream();
    WriteU32(didx, wemId);
    WriteU32(didx, 0); // offset relative to DATA start
    WriteU32(didx, (uint)wemData.Length);
    WriteChunk(ms, "DIDX", didx.ToArray());

    // DATA: just the blob
    WriteChunk(ms, "DATA", wemData);

    // HIRC: count=1, one object: type=1 (sound), size=12 (4 id + 8 payload), id=0x99887766
    var hirc = new MemoryStream();
    WriteU32(hirc, 1); // count
    hirc.WriteByte(0x02); // type = sound SFX
    WriteU32(hirc, 12); // size
    WriteU32(hirc, 0x99887766); // id
    hirc.Write(new byte[8]); // payload
    WriteChunk(ms, "HIRC", hirc.ToArray());

    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new WwiseBnkFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("WwiseBnk"));
    Assert.That(d.Extensions, Contains.Item(".bnk"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("BKHD"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesAllChunks() {
    var bytes = BuildMinimalBnk(out var wemId, out var wemData, out var ver, out var bankId);
    using var ms = new MemoryStream(bytes);
    var r = new WwiseBnkReader(ms);
    Assert.That(r.BankVersion, Is.EqualTo(ver));
    Assert.That(r.BankId, Is.EqualTo(bankId));
    Assert.That(r.Wems, Has.Count.EqualTo(1));
    Assert.That(r.Wems[0].WemId, Is.EqualTo(wemId));
    Assert.That(r.ExtractWem(r.Wems[0]), Is.EqualTo(wemData));
    Assert.That(r.HircObjects, Has.Count.EqualTo(1));
    Assert.That(r.HircObjects[0].Type, Is.EqualTo(2));
    Assert.That(r.HircObjects[0].Id, Is.EqualTo(0x99887766u));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsExpectedEntries() {
    var bytes = BuildMinimalBnk(out var wemId, out _, out _, out _);
    using var ms = new MemoryStream(bytes);
    var d = new WwiseBnkFormatDescriptor();
    var list = d.List(ms, null);
    Assert.That(list.Any(e => e.Name == "FULL.bnk"), Is.True);
    Assert.That(list.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(list.Any(e => e.Name == "hirc_objects.txt"), Is.True);
    Assert.That(list.Any(e => e.Name == $"wems/{wemId}.wem"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesFilesToDisk() {
    var bytes = BuildMinimalBnk(out var wemId, out var wemData, out _, out _);
    var dir = Path.Combine(Path.GetTempPath(), "wwisebnk_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(bytes);
      var d = new WwiseBnkFormatDescriptor();
      d.Extract(ms, dir, null, null);

      Assert.That(File.Exists(Path.Combine(dir, "FULL.bnk")), Is.True);
      Assert.That(File.Exists(Path.Combine(dir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(dir, "hirc_objects.txt")), Is.True);
      var wemPath = Path.Combine(dir, "wems", $"{wemId}.wem");
      Assert.That(File.Exists(wemPath), Is.True);
      Assert.That(File.ReadAllBytes(wemPath), Is.EqualTo(wemData));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }
}
