using Compression.Registry;
using FileFormat.Leveldb;

namespace Compression.Tests.Leveldb;

[TestFixture]
public class LeveldbTests {

  // Build a minimal SSTable: a "data" region followed by a 48-byte footer
  // containing two BlockHandles (metaindex then index) and the magic.
  private static byte[] BuildFixture(int dataLen = 256, int metaSize = 32, int idxOff = 0, int idxSize = 48) {
    // Footer layout: varints for metaindex(off,size) and index(off,size) padded to 40 bytes + 8 byte magic.
    const int footerSize = 48;

    // metaindex starts right after data; index is placed after metaindex conceptually.
    int metaOff = dataLen;
    if (idxOff == 0) idxOff = metaOff + metaSize;

    var footer = new byte[footerSize];
    int pos = 0;
    pos = WriteVarint(footer, pos, (ulong)metaOff);
    pos = WriteVarint(footer, pos, (ulong)metaSize);
    pos = WriteVarint(footer, pos, (ulong)idxOff);
    pos = WriteVarint(footer, pos, (ulong)idxSize);
    // Remaining bytes up to offset 40 stay zeroed as padding.
    // Magic at the last 8 bytes
    byte[] magic = [0x57, 0xFB, 0x80, 0x8B, 0x24, 0x75, 0x47, 0xDB];
    Array.Copy(magic, 0, footer, 40, 8);

    var result = new byte[dataLen + footerSize];
    for (int i = 0; i < dataLen; i++) result[i] = (byte)(i & 0xFF);
    Array.Copy(footer, 0, result, dataLen, footerSize);
    return result;
  }

  private static int WriteVarint(byte[] buf, int pos, ulong value) {
    while (value >= 0x80) {
      buf[pos++] = (byte)((value & 0x7F) | 0x80);
      value >>= 7;
    }
    buf[pos++] = (byte)value;
    return pos;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new LeveldbFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Leveldb"));
    Assert.That(d.Extensions, Contains.Item(".ldb"));
    Assert.That(d.Extensions, Contains.Item(".sst"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsFullFooterAndDataBlocks() {
    var d = new LeveldbFormatDescriptor();
    using var ms = new MemoryStream(BuildFixture());
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Has.Member("FULL.ldb"));
    Assert.That(names, Has.Member("metadata.ini"));
    Assert.That(names, Has.Member("footer.bin"));
    Assert.That(names, Has.Member("data_blocks.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesExpectedFiles() {
    var d = new LeveldbFormatDescriptor();
    var fixture = BuildFixture(dataLen: 128, metaSize: 16, idxSize: 32);
    var tmp = Path.Combine(Path.GetTempPath(), "ldb-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(fixture);
      d.Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "FULL.ldb")));
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")));
      Assert.That(File.Exists(Path.Combine(tmp, "footer.bin")));
      Assert.That(File.Exists(Path.Combine(tmp, "data_blocks.bin")));

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("parse_status=ok"));
      Assert.That(ini, Does.Contain("magic_ok=true"));
      Assert.That(ini, Does.Contain("metaindex_offset=128"));
      Assert.That(ini, Does.Contain("metaindex_size=16"));

      var footer = File.ReadAllBytes(Path.Combine(tmp, "footer.bin"));
      Assert.That(footer, Has.Length.EqualTo(48));

      var dataBlocks = File.ReadAllBytes(Path.Combine(tmp, "data_blocks.bin"));
      Assert.That(dataBlocks, Has.Length.EqualTo(128));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_RobustOnGarbage() {
    var d = new LeveldbFormatDescriptor();
    var junk = new byte[16];
    Array.Fill(junk, (byte)0x33);
    var tmp = Path.Combine(Path.GetTempPath(), "ldb-junk-" + Guid.NewGuid().ToString("N"));
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
    var d = new LeveldbFormatDescriptor();
    using var ms = new MemoryStream(new byte[4]);
    Assert.DoesNotThrow(() => d.List(ms, null));
  }
}
