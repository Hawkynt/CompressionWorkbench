namespace Compression.Tests.Vhd;

[TestFixture]
public class VhdTests {
  [Test, Category("RoundTrip")]
  public void RoundTrip_FixedVhd() {
    var data = new byte[512 * 10];
    new Random(42).NextBytes(data);
    var w = new FileFormat.Vhd.VhdWriter();
    w.SetDiskData(data);
    var vhd = w.Build();

    Assert.That(vhd.Length, Is.EqualTo(data.Length + 512));

    using var ms = new MemoryStream(vhd);
    var r = new FileFormat.Vhd.VhdReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("disk.img"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_EmptyDisk() {
    var w = new FileFormat.Vhd.VhdWriter();
    w.SetDiskData([]);
    var vhd = w.Build();
    Assert.That(vhd.Length, Is.EqualTo(512));

    using var ms = new MemoryStream(vhd);
    var r = new FileFormat.Vhd.VhdReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Footer_HasConectixMagic() {
    var w = new FileFormat.Vhd.VhdWriter();
    w.SetDiskData(new byte[1024]);
    var vhd = w.Build();
    Assert.That(System.Text.Encoding.ASCII.GetString(vhd, vhd.Length - 512, 8), Is.EqualTo("conectix"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Vhd.VhdFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Vhd"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".vhd"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Vhd.VhdReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[1024];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Vhd.VhdReader(ms));
  }

  // ── Dynamic VHD tests ──────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void RoundTrip_DynamicVhd_NonZeroData() {
    var data = new byte[512 * 20];
    new Random(99).NextBytes(data);
    var w = new FileFormat.Vhd.VhdWriter();
    w.SetDiskData(data);
    var vhd = w.BuildDynamic();

    using var ms = new MemoryStream(vhd);
    var r = new FileFormat.Vhd.VhdReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("disk.img"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_DynamicVhd_SparseBlocks_ReturnZeros() {
    // All-zero data should produce sparse BAT entries (0xFFFFFFFF)
    var data = new byte[512 * 10];
    var w = new FileFormat.Vhd.VhdWriter();
    w.SetDiskData(data);
    var vhd = w.BuildDynamic();

    using var ms = new MemoryStream(vhd);
    var r = new FileFormat.Vhd.VhdReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_DynamicVhd_MixedSparseAndDataBlocks() {
    // Create data large enough for multiple blocks (block size = 2MB default)
    const int blockSize = 0x00200000; // 2 MB
    var data = new byte[blockSize * 3];
    // Block 0: zeros (sparse)
    // Block 1: non-zero data
    new Random(77).NextBytes(data.AsSpan(blockSize, blockSize));
    // Block 2: zeros (sparse)

    var w = new FileFormat.Vhd.VhdWriter();
    w.SetDiskData(data);
    var vhd = w.BuildDynamic();

    // Dynamic VHD should be smaller than fixed since 2 of 3 blocks are sparse
    Assert.That(vhd.Length, Is.LessThan(data.Length));

    using var ms = new MemoryStream(vhd);
    var r = new FileFormat.Vhd.VhdReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted.Length, Is.EqualTo(data.Length));
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void DynamicVhd_HasCxsparseMagic() {
    var w = new FileFormat.Vhd.VhdWriter();
    w.SetDiskData(new byte[1024]);
    var vhd = w.BuildDynamic();
    // Dynamic VHD: footer copy at offset 0 ("conectix"), dynamic header at 512 ("cxsparse")
    Assert.That(System.Text.Encoding.ASCII.GetString(vhd, 0, 8), Is.EqualTo("conectix"));
    Assert.That(System.Text.Encoding.ASCII.GetString(vhd, 512, 8), Is.EqualTo("cxsparse"));
  }

  [Test, Category("HappyPath")]
  public void DynamicVhd_HasTrailingFooter() {
    var w = new FileFormat.Vhd.VhdWriter();
    w.SetDiskData(new byte[1024]);
    var vhd = w.BuildDynamic();
    // Trailing footer at last 512 bytes
    Assert.That(System.Text.Encoding.ASCII.GetString(vhd, vhd.Length - 512, 8), Is.EqualTo("conectix"));
  }

  [Test, Category("HappyPath")]
  public void DynamicVhd_DiskType_Is3() {
    var w = new FileFormat.Vhd.VhdWriter();
    w.SetDiskData(new byte[1024]);
    var vhd = w.BuildDynamic();
    // Footer at offset 0, disk type at offset 60 = 3 (dynamic)
    var diskType = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(vhd.AsSpan(60));
    Assert.That(diskType, Is.EqualTo(3));
  }

  [Test, Category("HappyPath")]
  public void DynamicVhd_ReportsVirtualSize() {
    var data = new byte[4096];
    new Random(42).NextBytes(data);
    var w = new FileFormat.Vhd.VhdWriter();
    w.SetDiskData(data);
    var vhd = w.BuildDynamic();

    using var ms = new MemoryStream(vhd);
    var r = new FileFormat.Vhd.VhdReader(ms);
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
  }
}
