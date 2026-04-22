using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Mdb;

namespace Compression.Tests.Mdb;

[TestFixture]
public class MdbTests {

  // Build a minimal 4-page Jet 4 MDB fixture (page_size=4096)
  private static byte[] BuildJet4Fixture(int pages = 3) {
    const int pageSize = 4096;
    var bytes = new byte[pageSize * pages];
    // Conventional first four bytes
    bytes[0] = 0x00;
    bytes[1] = 0x01;
    bytes[2] = 0x00;
    bytes[3] = 0x00;
    var sig = Encoding.ASCII.GetBytes("Standard Jet DB");
    Array.Copy(sig, 0, bytes, 4, sig.Length);
    bytes[0x14] = 1; // Jet 4 version byte
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x2C, 4), 2u); // MSysObjects root page
    return bytes;
  }

  private static byte[] BuildAccDbFixture(int pages = 3) {
    const int pageSize = 4096;
    var bytes = new byte[pageSize * pages];
    bytes[0] = 0x00;
    bytes[1] = 0x01;
    bytes[2] = 0x00;
    bytes[3] = 0x00;
    var sig = Encoding.ASCII.GetBytes("Standard ACE DB");
    Array.Copy(sig, 0, bytes, 4, sig.Length);
    bytes[0x14] = 2;
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x2C, 4), 5u);
    return bytes;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new MdbFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Mdb"));
    Assert.That(d.Extensions, Contains.Item(".mdb"));
    Assert.That(d.Extensions, Contains.Item(".accdb"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void List_Jet4_ReturnsAllEntries() {
    var d = new MdbFormatDescriptor();
    using var ms = new MemoryStream(BuildJet4Fixture(pages: 3));
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Has.Member("FULL.mdb"));
    Assert.That(names, Has.Member("metadata.ini"));
    Assert.That(names, Has.Member("page_00_header.bin"));
    Assert.That(names, Has.Member("msysobjects_pointer.txt"));
    Assert.That(names, Has.Member("pages/page_00001.bin"));
    Assert.That(names, Has.Member("pages/page_00002.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_Jet4_WritesFiles() {
    var d = new MdbFormatDescriptor();
    var fixture = BuildJet4Fixture(pages: 3);
    var tmp = Path.Combine(Path.GetTempPath(), "mdb-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(fixture);
      d.Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "FULL.mdb")));
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")));
      Assert.That(File.Exists(Path.Combine(tmp, "page_00_header.bin")));
      Assert.That(File.Exists(Path.Combine(tmp, "msysobjects_pointer.txt")));
      Assert.That(File.Exists(Path.Combine(tmp, "pages", "page_00001.bin")));

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("format=jet4"));
      Assert.That(ini, Does.Contain("page_size=4096"));

      var msys = File.ReadAllText(Path.Combine(tmp, "msysobjects_pointer.txt"));
      Assert.That(msys, Does.Contain("msysobjects_root_page=2"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_AccDb_WritesAccdbExt() {
    var d = new MdbFormatDescriptor();
    var fixture = BuildAccDbFixture(pages: 2);
    var tmp = Path.Combine(Path.GetTempPath(), "accdb-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(fixture);
      d.Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "FULL.accdb")));
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("format=accdb"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_RobustOnGarbage() {
    var d = new MdbFormatDescriptor();
    var junk = new byte[64];
    Array.Fill(junk, (byte)0x55);
    var tmp = Path.Combine(Path.GetTempPath(), "mdb-junk-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(junk);
      Assert.DoesNotThrow(() => d.Extract(ms, tmp, null, null));
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")));
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("parse_status=partial"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void List_DoesNotThrowOnGarbage() {
    var d = new MdbFormatDescriptor();
    using var ms = new MemoryStream(new byte[8]);
    Assert.DoesNotThrow(() => d.List(ms, null));
  }
}
