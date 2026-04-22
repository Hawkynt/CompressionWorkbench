using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Sqlite;

namespace Compression.Tests.Sqlite;

[TestFixture]
public class SqliteTests {

  private static byte[] BuildFixture(int pageSize = 4096, int pages = 3) {
    var bytes = new byte[pageSize * pages];
    var magic = Encoding.ASCII.GetBytes("SQLite format 3\0");
    Array.Copy(magic, 0, bytes, 0, magic.Length);

    // page_size as uint16 BE (or 1 for 65536)
    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16, 2), (ushort)pageSize);
    bytes[18] = 1; // write version
    bytes[19] = 1; // read version
    bytes[20] = 0; // reserved
    bytes[21] = 64; // max embedded payload
    bytes[22] = 32; // min embedded payload
    bytes[23] = 32; // leaf payload

    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24, 4), 1); // file change counter
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(28, 4), (uint)pages); // db size pages
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(32, 4), 0); // first freelist trunk
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(36, 4), 0); // total freelist pages
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(40, 4), 0x12345678); // schema cookie
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(44, 4), 4); // schema format
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(56, 4), 1); // utf-8
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(60, 4), 7); // user version
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(68, 4), 0xCAFEBABE); // application id
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(96, 4), 3046001); // sqlite version
    return bytes;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new SqliteFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Sqlite"));
    Assert.That(d.Extensions, Contains.Item(".sqlite"));
    Assert.That(d.Extensions, Contains.Item(".sqlite3"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsFullMetadataHeaderAndPages() {
    var d = new SqliteFormatDescriptor();
    using var ms = new MemoryStream(BuildFixture(pageSize: 4096, pages: 3));
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Has.Member("FULL.sqlite"));
    Assert.That(entries.Select(e => e.Name), Has.Member("metadata.ini"));
    Assert.That(entries.Select(e => e.Name), Has.Member("page_01_header.bin"));
    Assert.That(entries.Select(e => e.Name), Has.Member("freelist_trunks.txt"));
    Assert.That(entries.Select(e => e.Name), Has.Member("pages/page_0002.bin"));
    Assert.That(entries.Select(e => e.Name), Has.Member("pages/page_0003.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesExpectedFiles() {
    var d = new SqliteFormatDescriptor();
    var fixture = BuildFixture(pageSize: 4096, pages: 3);
    var tmp = Path.Combine(Path.GetTempPath(), "sqlite-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(fixture);
      d.Extract(ms, tmp, null, null);

      Assert.That(File.Exists(Path.Combine(tmp, "FULL.sqlite")));
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")));
      Assert.That(File.Exists(Path.Combine(tmp, "page_01_header.bin")));
      Assert.That(File.Exists(Path.Combine(tmp, "freelist_trunks.txt")));
      Assert.That(File.Exists(Path.Combine(tmp, "pages", "page_0002.bin")));
      Assert.That(File.Exists(Path.Combine(tmp, "pages", "page_0003.bin")));

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("parse_status=ok"));
      Assert.That(ini, Does.Contain("page_size=4096"));
      Assert.That(ini, Does.Contain("text_encoding=utf-8"));
      Assert.That(ini, Does.Contain("user_version=7"));

      var header = File.ReadAllBytes(Path.Combine(tmp, "page_01_header.bin"));
      Assert.That(header, Has.Length.EqualTo(100));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_RobustOnGarbage_WritesPartialMetadata() {
    var d = new SqliteFormatDescriptor();
    var junk = new byte[200];
    Array.Fill(junk, (byte)0xAA);
    var tmp = Path.Combine(Path.GetTempPath(), "sqlite-junk-" + Guid.NewGuid().ToString("N"));
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
    var d = new SqliteFormatDescriptor();
    using var ms = new MemoryStream(new byte[10]);
    Assert.DoesNotThrow(() => d.List(ms, null));
  }
}
