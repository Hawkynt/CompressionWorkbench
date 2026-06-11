using System.Buffers.Binary;
using System.Text;
using FileFormat.Dar;

namespace Compression.Tests.Dar;

[TestFixture]
public class DarTests {

  // Build a minimal libdar-style slice: big-endian magic (0x0000007E), a printable
  // label, a format flag, then a payload region, a catalogue region, and a trailing
  // terminator carrying the catalogue start offset as a big-endian u64.
  private static byte[] BuildSyntheticDar() {
    using var ms = new MemoryStream();

    // Magic (big-endian).
    Span<byte> magic = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(magic, 0x0000007E);
    ms.Write(magic);

    // Label (printable) + format flag.
    ms.Write(Encoding.ASCII.GetBytes("DARLABEL01")); // 10 bytes -> ends at offset 14
    ms.WriteByte(0x08);                               // format flag at offset 14

    // Payload region.
    var payload = new byte[64];
    Array.Fill(payload, (byte)0xA5);
    ms.Write(payload);

    // Catalogue region begins here.
    var catalogueOffset = (int)ms.Length;
    var catalogue = Encoding.ASCII.GetBytes("CATALOGUE-TREE-PLACEHOLDER");
    ms.Write(catalogue);

    // Terminator: big-endian u64 pointing back to the catalogue start.
    Span<byte> term = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(term, (ulong)catalogueOffset);
    ms.Write(term);

    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new DarFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Dar"));
    Assert.That(d.Extensions, Contains.Item(".dar"));
    // Weak magic -> extension-driven detection only.
    Assert.That(d.MagicSignatures, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndCatalogue() {
    var img = BuildSyntheticDar();
    var d = new DarFormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.dar"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "catalogue.bin"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_FullByteIdenticalAndParsesHeader() {
    var img = BuildSyntheticDar();
    var d = new DarFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "dar_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, dir, null, null);

      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.dar"));
      Assert.That(full, Is.EqualTo(img));

      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("slice_magic_ok=1"));
      Assert.That(meta, Does.Contain("magic=0x0000007E"));
      Assert.That(meta, Does.Contain("label=DARLABEL01"));
      Assert.That(meta, Does.Contain("member_enumeration=deferred"));
      Assert.That(meta, Does.Contain("parse_status=ok"));

      var cat = File.ReadAllBytes(Path.Combine(dir, "catalogue.bin"));
      Assert.That(Encoding.ASCII.GetString(cat), Does.StartWith("CATALOGUE-TREE-PLACEHOLDER"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow() {
    var garbage = new byte[64];
    Array.Fill(garbage, (byte)0x33);
    var d = new DarFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "dar_bad_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(garbage);
      Assert.DoesNotThrow(() => d.List(ms, null));
      ms.Position = 0;
      Assert.DoesNotThrow(() => d.Extract(ms, dir, null, null));
      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.dar"));
      Assert.That(full, Is.EqualTo(garbage));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
