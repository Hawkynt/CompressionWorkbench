#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Lit;

namespace Compression.Tests.Lit;

[TestFixture]
public class LitTests {

  // Builds a minimally valid Microsoft Reader .lit container: 37-byte header
  // ("ITOLITLS" + 7 LE u32s + mask u8) followed by synthetic "directory" bytes.
  private static byte[] MakeMinimalLit() {
    const int headerSize = 37;
    var dirBytes = Encoding.ASCII.GetBytes("FAKE-LIT-DIRECTORY-CHUNK");
    var total = new byte[headerSize + dirBytes.Length];

    // Magic "ITOLITLS".
    Encoding.ASCII.GetBytes("ITOLITLS").CopyTo(total, 0);
    // version=1, header_len=0x28, unknown=0, header_len_dir=dirBytes.Length,
    // data_offset=headerSize, aOffset=0, bOffset=0, mask=0.
    BinaryPrimitives.WriteUInt32LittleEndian(total.AsSpan(8),  1u);
    BinaryPrimitives.WriteUInt32LittleEndian(total.AsSpan(12), 0x28u);
    BinaryPrimitives.WriteUInt32LittleEndian(total.AsSpan(16), 0u);
    BinaryPrimitives.WriteUInt32LittleEndian(total.AsSpan(20), (uint)dirBytes.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(total.AsSpan(24), (uint)headerSize);
    BinaryPrimitives.WriteUInt32LittleEndian(total.AsSpan(28), 0u);
    BinaryPrimitives.WriteUInt32LittleEndian(total.AsSpan(32), 0u);
    total[36] = 0;
    dirBytes.CopyTo(total, headerSize);
    return total;
  }

  [Category("HappyPath")]
  [Test]
  public void List_ReturnsCanonicalEntries() {
    var data = MakeMinimalLit();
    using var ms = new MemoryStream(data);
    var entries = new LitFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.lit"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "directory.bin"), Is.True);
  }

  [Category("HappyPath")]
  [Test]
  public void Extract_WritesFiles() {
    var data = MakeMinimalLit();
    var tmp = Path.Combine(Path.GetTempPath(), "lit_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new LitFormatDescriptor().Extract(ms, tmp, null, null);

      Assert.That(File.Exists(Path.Combine(tmp, "FULL.lit")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "directory.bin")), Is.True);

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("magic=ITOLITLS"));
      Assert.That(ini, Does.Contain("version=1"));
      Assert.That(ini, Does.Contain("header_len=40"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_MagicAndExtensions() {
    var d = new LitFormatDescriptor();
    Assert.That(d.Extensions, Does.Contain(".lit"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(Encoding.ASCII.GetString(d.MagicSignatures[0].Bytes), Is.EqualTo("ITOLITLS"));
  }
}
