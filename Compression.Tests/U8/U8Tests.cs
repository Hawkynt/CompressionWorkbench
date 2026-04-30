using System.Buffers.Binary;

namespace Compression.Tests.U8;

[TestFixture]
public class U8Tests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "test data"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.U8.U8Writer(ms, leaveOpen: true))
      w.AddEntry("test.bin", data);
    ms.Position = 0;

    var r = new FileFormat.U8.U8Reader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Name, Is.EqualTo("test.bin"));
    Assert.That(files[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(files[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var d1 = new byte[64];
    var d2 = new byte[128];
    var d3 = new byte[256];
    Array.Fill(d1, (byte)0x11);
    Array.Fill(d2, (byte)0x22);
    Array.Fill(d3, (byte)0x33);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.U8.U8Writer(ms, leaveOpen: true)) {
      w.AddEntry("a/one.bin", d1);
      w.AddEntry("b/two.bin", d2);
      w.AddEntry("c/three.bin", d3);
    }
    ms.Position = 0;

    var r = new FileFormat.U8.U8Reader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
    Assert.That(files.Keys, Is.EquivalentTo(new[] { "a/one.bin", "b/two.bin", "c/three.bin" }));
    Assert.That(r.Extract(files["a/one.bin"]), Is.EqualTo(d1));
    Assert.That(r.Extract(files["b/two.bin"]), Is.EqualTo(d2));
    Assert.That(r.Extract(files["c/three.bin"]), Is.EqualTo(d3));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_NestedDirectories() {
    // Locks the parent/end-index encoding: the four files must round-trip with their
    // exact paths regardless of how the depth-first walk lays out the tree.
    var a = "alpha"u8.ToArray();
    var b = "bravo"u8.ToArray();
    var c = "charlie"u8.ToArray();
    var d = "delta"u8.ToArray();

    using var ms = new MemoryStream();
    using (var w = new FileFormat.U8.U8Writer(ms, leaveOpen: true)) {
      w.AddEntry("a.bin", a);
      w.AddEntry("sub1/b.bin", b);
      w.AddEntry("sub1/sub2/c.bin", c);
      w.AddEntry("sub3/d.bin", d);
    }
    ms.Position = 0;

    var r = new FileFormat.U8.U8Reader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);

    Assert.That(files.Keys, Is.EquivalentTo(new[] {
      "a.bin", "sub1/b.bin", "sub1/sub2/c.bin", "sub3/d.bin"
    }));
    Assert.That(r.Extract(files["a.bin"]), Is.EqualTo(a));
    Assert.That(r.Extract(files["sub1/b.bin"]), Is.EqualTo(b));
    Assert.That(r.Extract(files["sub1/sub2/c.bin"]), Is.EqualTo(c));
    Assert.That(r.Extract(files["sub3/d.bin"]), Is.EqualTo(d));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[64 * 1024];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i * 31);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.U8.U8Writer(ms, leaveOpen: true))
      w.AddEntry("big.bin", data);
    ms.Position = 0;

    var r = new FileFormat.U8.U8Reader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(files[0]), Is.EqualTo(data));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[64];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.U8.U8Reader(ms));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsU8() {
    var d = new FileFormat.U8.U8FormatDescriptor();
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x55, 0xAA, 0x38, 0x2D }));
  }

  [Test, Category("HappyPath")]
  public void Header_BigEndian() {
    var data = "x"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.U8.U8Writer(ms, leaveOpen: true))
      w.AddEntry("a.bin", data);

    var bytes = ms.ToArray();
    Assert.That(bytes[..4], Is.EqualTo(new byte[] { 0x55, 0xAA, 0x38, 0x2D }));
    Assert.That(bytes[4..8], Is.EqualTo(new byte[] { 0x00, 0x00, 0x00, 0x20 }));

    // Spot-check that NodeTableSize and DataOffset are also BE — both are non-zero
    // and sane (DataOffset should be a multiple of 0x20).
    var nodeTableSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4));
    var dataOffset = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12, 4));
    Assert.That(nodeTableSize, Is.GreaterThan(0u));
    Assert.That(dataOffset % 0x20u, Is.EqualTo(0u));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.U8.U8FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("U8"));
    Assert.That(d.DisplayName, Is.EqualTo("Nintendo U8"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".u8"));
    Assert.That(d.Extensions, Is.EquivalentTo(new[] { ".u8", ".arc" }));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("u8"));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.SupportsMultipleEntries), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.SupportsDirectories), Is.True);
  }
}
