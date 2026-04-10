namespace Compression.Tests.Vdi;

[TestFixture]
public class VdiTests {
  // Block size used in all tests to keep images small
  private const uint TestBlockSize = 4096;

  private static byte[] CreateVdi(byte[] diskData, uint blockSize = TestBlockSize) {
    using var ms = new MemoryStream();
    using var w = new FileFormat.Vdi.VdiWriter(ms, leaveOpen: true,
      virtualSize: diskData.Length, blockSize: blockSize);
    w.Write(diskData);
    return ms.ToArray();
  }

  // ── Round-trip tests ────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void RoundTrip_SmallDisk() {
    // 2 blocks worth of deterministic non-zero data
    var data = new byte[TestBlockSize * 2];
    for (int i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);

    var vdi = CreateVdi(data);

    using var ms = new MemoryStream(vdi);
    var r = new FileFormat.Vdi.VdiReader(ms);

    Assert.That(r.VirtualSize, Is.EqualTo(data.Length));
    Assert.That(r.AllocatedBlockCount, Is.EqualTo(2u));

    var extracted = r.ExtractDisk();
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_SparseBlocks() {
    // 4 blocks: blocks 0 and 2 are non-zero, blocks 1 and 3 are zero
    var data = new byte[TestBlockSize * 4];

    // Fill blocks 0 and 2 with deterministic patterns
    for (int i = 0; i < (int)TestBlockSize; i++) {
      data[i] = (byte)(0xAA ^ (i & 0xFF));                           // block 0
      data[(int)TestBlockSize * 2 + i] = (byte)(0x55 ^ (i & 0xFF)); // block 2
    }
    // blocks 1 and 3 remain zero

    var vdi = CreateVdi(data);

    using var ms = new MemoryStream(vdi);
    var r = new FileFormat.Vdi.VdiReader(ms);

    Assert.That(r.VirtualSize, Is.EqualTo(data.Length));
    Assert.That(r.BlockCount, Is.EqualTo(4u));
    // Only 2 blocks should be physically allocated
    Assert.That(r.AllocatedBlockCount, Is.EqualTo(2u));

    var extracted = r.ExtractDisk();
    Assert.That(extracted.Length, Is.EqualTo(data.Length));

    // Verify non-zero blocks round-tripped correctly
    for (int i = 0; i < (int)TestBlockSize; i++) {
      Assert.That(extracted[i], Is.EqualTo(data[i]),
        $"Block 0 byte {i} mismatch");
      Assert.That(extracted[(int)TestBlockSize * 2 + i],
        Is.EqualTo(data[(int)TestBlockSize * 2 + i]),
        $"Block 2 byte {i} mismatch");
    }

    // Verify zero blocks are still zero
    for (int i = 0; i < (int)TestBlockSize; i++) {
      Assert.That(extracted[(int)TestBlockSize + i], Is.EqualTo(0),
        $"Block 1 byte {i} should be zero");
      Assert.That(extracted[(int)TestBlockSize * 3 + i], Is.EqualTo(0),
        $"Block 3 byte {i} should be zero");
    }
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleBlock_NonZero() {
    var data = new byte[TestBlockSize];
    for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);

    var vdi = CreateVdi(data);

    using var ms = new MemoryStream(vdi);
    var r = new FileFormat.Vdi.VdiReader(ms);
    Assert.That(r.VirtualSize, Is.EqualTo(data.Length));
    Assert.That(r.AllocatedBlockCount, Is.EqualTo(1u));

    var extracted = r.ExtractDisk();
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_AllZeroDisk_NoDataBlocks() {
    var data = new byte[TestBlockSize * 3]; // all zeros

    var vdi = CreateVdi(data);

    using var ms = new MemoryStream(vdi);
    var r = new FileFormat.Vdi.VdiReader(ms);

    Assert.That(r.VirtualSize, Is.EqualTo(data.Length));
    Assert.That(r.AllocatedBlockCount, Is.EqualTo(0u));

    var extracted = r.ExtractDisk();
    Assert.That(extracted, Is.EqualTo(data));
  }

  // ── Descriptor tests ────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Vdi.VdiFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Vdi"));
    Assert.That(desc.DisplayName, Is.EqualTo("VDI"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".vdi"));
    Assert.That(desc.Extensions, Contains.Item(".vdi"));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(64));
    Assert.That(desc.Description, Is.EqualTo("VirtualBox disk image"));
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList),    Is.True);
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate),  Is.True);
  }

  // ── Error-handling tests ─────────────────────────────────────────────────────

  [Test, Category("ErrorHandling")]
  public void BadMagic_Throws() {
    var bad = new byte[1024]; // all zeros → signature will be 0, not 0xBEDA107F
    using var ms = new MemoryStream(bad);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Vdi.VdiReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void TooSmall_Throws() {
    var tiny = new byte[100];
    using var ms = new MemoryStream(tiny);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Vdi.VdiReader(ms));
  }
}
