using System.Buffers.Binary;
using FileSystem.Apfs;

namespace Compression.Tests.Apfs;

[TestFixture]
public class ApfsTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new ApfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Apfs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".apfs"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("NXSB"u8.ToArray()));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(32));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsWriteInterfaces() {
    var desc = new ApfsFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveCreatable>());
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveWriteConstraints>());
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new ApfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[8192];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new ApfsReader(ms));
  }

  // ── Fletcher-64 correctness ────────────────────────────────────────────

  [Test, Category("Spec")]
  public void Fletcher64_AllZeros_ReturnsZero() {
    var block = new byte[4096]; // all zeros
    var ck = ApfsFletcher64.Compute(block);
    // For all-zero content [8..4096] the sums remain 0, so:
    //   sum1 = (~(0 + 0)) % 0xFFFFFFFF = 0xFFFFFFFF
    //   sum2 = (~(0 + 0xFFFFFFFF)) % 0xFFFFFFFF = 0
    // ck = (sum2 << 32) | sum1 = 0xFFFFFFFF
    // But ~(c1 + c2) of 0 → 0xFFFFFFFF... (~0UL = 0xFFFFFFFFFFFFFFFF),
    // then % 0xFFFFFFFF = 0 (since 0xFFFFFFFFFFFFFFFF mod 0xFFFFFFFF == 0).
    // So ck = 0.
    Assert.That(ck, Is.EqualTo(0UL));
  }

  [Test, Category("Spec")]
  public void Fletcher64_StampAndVerify_RoundTrips() {
    var rng = new Random(12345);
    var block = new byte[4096];
    rng.NextBytes(block);
    ApfsFletcher64.Stamp(block);
    Assert.That(ApfsFletcher64.Verify(block), Is.True);
    // Tampering breaks the checksum.
    block[100] ^= 0xFF;
    Assert.That(ApfsFletcher64.Verify(block), Is.False);
  }

  [Test, Category("Spec")]
  public void Writer_NxSuperblockHasValidFletcher64() {
    var w = new ApfsWriter();
    w.SetMinImageSize(4 * 1024 * 1024); // 4 MB for fast test
    w.AddFile("a.txt", "hello"u8.ToArray());
    var image = w.Build();

    var nxBlock = image.AsSpan(0, 4096);
    Assert.That(ApfsFletcher64.Verify(nxBlock), Is.True,
      "NX superblock Fletcher-64 checksum must verify.");
    // Magic at offset 32 stored LE as 0x4253584E.
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(nxBlock[32..]);
    Assert.That(magic, Is.EqualTo(0x4253584EU), "NXSB magic at offset 32.");
    // Individual magic bytes.
    Assert.That(nxBlock[32], Is.EqualTo((byte)'N'));
    Assert.That(nxBlock[33], Is.EqualTo((byte)'X'));
    Assert.That(nxBlock[34], Is.EqualTo((byte)'S'));
    Assert.That(nxBlock[35], Is.EqualTo((byte)'B'));
  }

  [Test, Category("Spec")]
  public void Writer_ApsbHasValidFletcher64AndMagic() {
    var w = new ApfsWriter();
    w.SetMinImageSize(4 * 1024 * 1024);
    w.AddFile("a.txt", "hello"u8.ToArray());
    var image = w.Build();

    // APSB is at block 5 per our layout.
    var apsb = image.AsSpan(5 * 4096, 4096);
    Assert.That(ApfsFletcher64.Verify(apsb), Is.True, "APSB Fletcher-64 must verify.");
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(apsb[32..]);
    Assert.That(magic, Is.EqualTo(0x42535041U), "APSB magic at offset 32.");
    Assert.That(apsb[32], Is.EqualTo((byte)'A'));
    Assert.That(apsb[33], Is.EqualTo((byte)'P'));
    Assert.That(apsb[34], Is.EqualTo((byte)'S'));
    Assert.That(apsb[35], Is.EqualTo((byte)'B'));
  }

  [Test, Category("Spec")]
  public void Writer_NxIncompatFeatures_SetsVersion2() {
    var w = new ApfsWriter();
    w.SetMinImageSize(4 * 1024 * 1024);
    var image = w.Build();
    var feats = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(64));
    Assert.That(feats & FileSystem.Apfs.ApfsConstants.NX_INCOMPAT_VERSION2,
      Is.EqualTo(FileSystem.Apfs.ApfsConstants.NX_INCOMPAT_VERSION2),
      "nx_incompatible_features must have NX_INCOMPAT_VERSION2 bit set.");
  }

  // ── Round-trip tests ───────────────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var content = "Hello, APFS!"u8.ToArray();
    var w = new ApfsWriter();
    w.SetMinImageSize(4 * 1024 * 1024);
    w.AddFile("hello.txt", content);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new ApfsReader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(files[0].Size, Is.EqualTo(content.Length));
    Assert.That(r.Extract(files[0]), Is.EqualTo(content));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var d1 = "alpha"u8.ToArray();
    var d2 = new byte[2048];
    new Random(77).NextBytes(d2);
    var d3 = "gamma-file-content"u8.ToArray();

    var w = new ApfsWriter();
    w.SetMinImageSize(4 * 1024 * 1024);
    w.AddFile("a.bin", d1);
    w.AddFile("b.bin", d2);
    w.AddFile("c.bin", d3);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new ApfsReader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, e => e);
    Assert.That(files, Has.Count.EqualTo(3));
    Assert.That(r.Extract(files["a.bin"]), Is.EqualTo(d1));
    Assert.That(r.Extract(files["b.bin"]), Is.EqualTo(d2));
    Assert.That(r.Extract(files["c.bin"]), Is.EqualTo(d3));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_ThreeDifferentSizes() {
    var tiny = new byte[] { 1, 2, 3, 4 };
    var medium = new byte[4096]; // exactly 1 block
    var big = new byte[20000]; // multi-block, non-aligned
    new Random(1).NextBytes(medium);
    new Random(2).NextBytes(big);

    var w = new ApfsWriter();
    w.SetMinImageSize(4 * 1024 * 1024);
    w.AddFile("tiny.bin", tiny);
    w.AddFile("one_block.bin", medium);
    w.AddFile("many_blocks.bin", big);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new ApfsReader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, e => e);
    Assert.That(files, Has.Count.EqualTo(3));
    Assert.That(r.Extract(files["tiny.bin"]), Is.EqualTo(tiny));
    Assert.That(r.Extract(files["one_block.bin"]), Is.EqualTo(medium));
    Assert.That(r.Extract(files["many_blocks.bin"]), Is.EqualTo(big));
  }

  [Test, Category("HappyPath")]
  public void Writer_EmptyVolume_NoFiles() {
    var w = new ApfsWriter();
    w.SetMinImageSize(4 * 1024 * 1024);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new ApfsReader(ms);
    Assert.That(r.Entries.Where(e => !e.IsDirectory).Count(), Is.EqualTo(0));
  }
}
