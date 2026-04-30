using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Ba2;

[TestFixture]
public class Ba2Tests {

  [Test, Category("HappyPath")]
  public void Lookup3_KnownVector() {
    // Bob Jenkins' published lookup3 test phrase from his 2006 reference page.
    // hashlittle("Four score and seven years ago", 30, 0) == 0x17770551.
    var phrase = Encoding.ASCII.GetBytes("Four score and seven years ago");
    Assert.That(FileFormat.Ba2.BethesdaLookup3.Hash(phrase), Is.EqualTo(0x17770551u));

    // Empty input must return the seeded c (= 0xDEADBEEF) — special-cased before Final().
    Assert.That(FileFormat.Ba2.BethesdaLookup3.Hash(ReadOnlySpan<byte>.Empty), Is.EqualTo(0xDEADBEEFu));
  }

  [Test, Category("HappyPath")]
  public void Lookup3_LowercasingChangesHash() {
    var upper = FileFormat.Ba2.BethesdaLookup3.Hash("MESHES"u8);
    var lower = FileFormat.Ba2.BethesdaLookup3.Hash("meshes"u8);
    Assert.That(upper, Is.Not.EqualTo(lower), "Lookup3 is case-sensitive at the byte level — Bethesda relies on caller lowercasing.");
    Assert.That(FileFormat.Ba2.BethesdaLookup3.HashLower("MESHES"), Is.EqualTo(lower));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var body = "hello"u8.ToArray();

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Ba2.Ba2Writer(ms, leaveOpen: true))
      w.AddEntry("meshes\\test.nif", body);
    ms.Position = 0;

    var r = new FileFormat.Ba2.Ba2Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("meshes\\test.nif"));
    Assert.That(r.Entries[0].Ext, Is.EqualTo("nif"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(body.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(body));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var a = "alpha-payload"u8.ToArray();
    var b = "bravo body bytes"u8.ToArray();
    var c = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Ba2.Ba2Writer(ms, leaveOpen: true)) {
      w.AddEntry("textures\\effects\\smoke01.dds", a);
      w.AddEntry("sound\\fx\\step.wav", b);
      w.AddEntry("meshes\\armor\\helm.nif", c);
    }
    ms.Position = 0;

    var r = new FileFormat.Ba2.Ba2Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));

    Assert.That(r.Entries[0].Name, Is.EqualTo("textures\\effects\\smoke01.dds"));
    Assert.That(r.Entries[0].Ext, Is.EqualTo("dds"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(a));

    Assert.That(r.Entries[1].Name, Is.EqualTo("sound\\fx\\step.wav"));
    Assert.That(r.Entries[1].Ext, Is.EqualTo("wav"));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(b));

    Assert.That(r.Entries[2].Name, Is.EqualTo("meshes\\armor\\helm.nif"));
    Assert.That(r.Entries[2].Ext, Is.EqualTo("nif"));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(c));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_StoredVsCompressed() {
    // Highly compressible body — zlib should beat raw and we should see PackedSize > 0.
    var compressible = new byte[4096];
    Array.Fill(compressible, (byte)'A');

    // Tiny incompressible body — zlib overhead exceeds savings, writer must fall back to stored.
    var tiny = new byte[] { 0x37 };

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Ba2.Ba2Writer(ms, leaveOpen: true)) {
      w.AddEntry("data\\big.txt", compressible);
      w.AddEntry("data\\tiny.bin", tiny);
    }
    ms.Position = 0;

    var r = new FileFormat.Ba2.Ba2Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));

    Assert.That(r.Entries[0].PackedSize, Is.GreaterThan(0), "Compressible body should be stored as zlib.");
    Assert.That(r.Entries[0].PackedSize, Is.LessThan(compressible.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(compressible));

    Assert.That(r.Entries[1].PackedSize, Is.EqualTo(0), "Single-byte body must fall back to stored.");
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(tiny));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsDx10() {
    using var ms = new MemoryStream();
    var w = new BinaryWriter(ms);
    w.Write(Encoding.ASCII.GetBytes("BTDX"));
    w.Write(1u);                                // version
    w.Write(Encoding.ASCII.GetBytes("DX10"));   // type — texture archives are out of scope
    w.Write(0u);                                // file count
    w.Write(0UL);                               // name table offset
    ms.Position = 0;

    Assert.Throws<NotSupportedException>(() => _ = new FileFormat.Ba2.Ba2Reader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[24];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Ba2.Ba2Reader(ms));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Ba2.Ba2FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ba2"));
    Assert.That(d.DisplayName, Is.EqualTo("Bethesda Archive v2"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".ba2"));
    Assert.That(d.Extensions, Contains.Item(".ba2"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("BTDX"u8.ToArray()));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("gnrl"));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));

    var caps = d.Capabilities;
    Assert.That(caps.HasFlag(Compression.Registry.FormatCapabilities.CanList));
    Assert.That(caps.HasFlag(Compression.Registry.FormatCapabilities.CanExtract));
    Assert.That(caps.HasFlag(Compression.Registry.FormatCapabilities.CanCreate));
    Assert.That(caps.HasFlag(Compression.Registry.FormatCapabilities.CanTest));
    Assert.That(caps.HasFlag(Compression.Registry.FormatCapabilities.SupportsMultipleEntries));
  }

  [Test, Category("HappyPath")]
  public void Header_ShapeIsCorrect() {
    // Sanity: confirm the on-disk header layout matches the spec — magic at 0, version at 4,
    // type at 8, fileCount at 12, name-table-offset at 16. Important because writer back-patches
    // the offset at byte 16 and any drift would silently break detection.
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Ba2.Ba2Writer(ms, leaveOpen: true))
      w.AddEntry("a\\b.txt", "x"u8.ToArray());

    var bytes = ms.ToArray();
    Assert.That(Encoding.ASCII.GetString(bytes, 0, 4), Is.EqualTo("BTDX"));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)), Is.EqualTo(1u));
    Assert.That(Encoding.ASCII.GetString(bytes, 8, 4), Is.EqualTo("GNRL"));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)), Is.EqualTo(1u));
    var nameTableOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16, 8));
    Assert.That(nameTableOffset, Is.GreaterThan(24));
    Assert.That(nameTableOffset, Is.LessThan(bytes.Length));
  }

  [Test, Category("HappyPath")]
  public void HashesMatchPathConvention() {
    // dirHash hashes the directory portion (no trailing slash, lowercase),
    // nameHash hashes the basename without extension, ext is the literal lowercase suffix.
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Ba2.Ba2Writer(ms, leaveOpen: true))
      w.AddEntry("Textures\\Effects\\Smoke.DDS", "data"u8.ToArray());
    ms.Position = 0;

    var r = new FileFormat.Ba2.Ba2Reader(ms);
    var e = r.Entries[0];

    Assert.That(e.Ext, Is.EqualTo("dds"));
    Assert.That(e.DirHash, Is.EqualTo(FileFormat.Ba2.BethesdaLookup3.HashLower("textures\\effects")));
    Assert.That(e.NameHash, Is.EqualTo(FileFormat.Ba2.BethesdaLookup3.HashLower("smoke")));
  }
}
