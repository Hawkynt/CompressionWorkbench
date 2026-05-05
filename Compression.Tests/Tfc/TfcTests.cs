using System.Buffers.Binary;

namespace Compression.Tests.Tfc;

[TestFixture]
public class TfcTests {

  [Test, Category("HappyPath")]
  public void Magic_LittleEndianBytes() {
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, 0x9E2A83C1u);
    Assert.That(buf[0], Is.EqualTo((byte)0xC1));
    Assert.That(buf[1], Is.EqualTo((byte)0x83));
    Assert.That(buf[2], Is.EqualTo((byte)0x2A));
    Assert.That(buf[3], Is.EqualTo((byte)0x9E));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleStoredBundle() {
    var data = new byte[256];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i & 0xFF);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Tfc.TfcWriter(ms, leaveOpen: true))
      w.AddBundle(data);
    ms.Position = 0;

    var r = new FileFormat.Tfc.TfcReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var e = r.Entries[0];
    Assert.That(e.Name, Is.EqualTo("bundle_00000.bin"));
    Assert.That(e.UncompressedSize, Is.EqualTo(data.Length));
    Assert.That(e.CompressedSize, Is.EqualTo(data.Length));
    Assert.That(e.IsCompressed, Is.False);
    Assert.That(e.BlockSize, Is.EqualTo(0x00020000u));

    // Single 128 KiB block: table is 8 bytes (one slot), then 256 raw bytes.
    var payload = r.Extract(e);
    Assert.That(payload, Has.Length.EqualTo(8 + data.Length));
    var slotComp   = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
    var slotUncomp = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
    Assert.That(slotComp, Is.EqualTo((uint)data.Length));
    Assert.That(slotUncomp, Is.EqualTo((uint)data.Length));
    Assert.That(payload.AsSpan(8).ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultiBlockBundle() {
    const int total = 200 * 1024;
    const uint blockSize = 128 * 1024;
    var data = new byte[total];
    for (var i = 0; i < total; ++i)
      data[i] = (byte)(i * 7 & 0xFF);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Tfc.TfcWriter(ms, leaveOpen: true))
      w.AddBundle(data, blockSize);
    ms.Position = 0;

    var r = new FileFormat.Tfc.TfcReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var e = r.Entries[0];
    Assert.That(e.UncompressedSize, Is.EqualTo(total));
    Assert.That(e.CompressedSize, Is.EqualTo(total));
    Assert.That(e.BlockSize, Is.EqualTo(blockSize));
    Assert.That(e.IsCompressed, Is.False);

    // 2 blocks: 128 KiB + (200-128) KiB = 128 KiB + 72 KiB. Table is 16 bytes.
    Assert.That(e.Size, Is.EqualTo(16 + total));

    var payload = r.Extract(e);
    var b0Comp = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
    var b0Unc  = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
    var b1Comp = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8, 4));
    var b1Unc  = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12, 4));

    Assert.That(b0Comp, Is.EqualTo(blockSize));
    Assert.That(b0Unc,  Is.EqualTo(blockSize));
    Assert.That(b1Comp, Is.EqualTo((uint)(total - blockSize)));
    Assert.That(b1Unc,  Is.EqualTo((uint)(total - blockSize)));
    Assert.That(payload.AsSpan(16).ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleBundles() {
    var d0 = new byte[64];
    var d1 = new byte[128];
    var d2 = new byte[256];
    Array.Fill(d0, (byte)0x11);
    Array.Fill(d1, (byte)0x22);
    Array.Fill(d2, (byte)0x33);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Tfc.TfcWriter(ms, leaveOpen: true)) {
      w.AddBundle(d0);
      w.AddBundle(d1);
      w.AddBundle(d2);
    }
    ms.Position = 0;

    var r = new FileFormat.Tfc.TfcReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("bundle_00000.bin"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("bundle_00001.bin"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("bundle_00002.bin"));
    Assert.That(r.Entries[0].UncompressedSize, Is.EqualTo(d0.Length));
    Assert.That(r.Entries[1].UncompressedSize, Is.EqualTo(d1.Length));
    Assert.That(r.Entries[2].UncompressedSize, Is.EqualTo(d2.Length));

    // Validate the actual payload bytes for each bundle (skip 8-byte single-block size table).
    Assert.That(r.Extract(r.Entries[0]).AsSpan(8).ToArray(), Is.EqualTo(d0));
    Assert.That(r.Extract(r.Entries[1]).AsSpan(8).ToArray(), Is.EqualTo(d1));
    Assert.That(r.Extract(r.Entries[2]).AsSpan(8).ToArray(), Is.EqualTo(d2));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[64];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Tfc.TfcReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Reader_HandlesPureStoredBundle() {
    var bundle = BuildBundle(blockSize: 128, compressedSize: 200, uncompressedSize: 200,
      blockTable: [(128, 128), (72, 72)],
      blockData: FilledArray(200, 0xAB));

    using var ms = new MemoryStream(bundle);
    var r = new FileFormat.Tfc.TfcReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].IsCompressed, Is.False);
    Assert.That(r.Entries[0].UncompressedSize, Is.EqualTo(200));
    Assert.That(r.Entries[0].CompressedSize, Is.EqualTo(200));
  }

  [Test, Category("HappyPath")]
  public void Reader_HandlesCompressedFlagging() {
    var compressedBlobs = new byte[120];
    for (var i = 0; i < compressedBlobs.Length; ++i)
      compressedBlobs[i] = (byte)(0xC0 | (i & 0x0F));

    var bundle = BuildBundle(blockSize: 128, compressedSize: 120, uncompressedSize: 200,
      blockTable: [(80, 128), (40, 72)],
      blockData: compressedBlobs);

    using var ms = new MemoryStream(bundle);
    var r = new FileFormat.Tfc.TfcReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var e = r.Entries[0];
    Assert.That(e.IsCompressed, Is.True);
    Assert.That(e.CompressedSize, Is.EqualTo(120));
    Assert.That(e.UncompressedSize, Is.EqualTo(200));

    // Extract returns table + raw compressed bytes — NO decompression.
    var payload = r.Extract(e);
    Assert.That(payload, Has.Length.EqualTo(16 + 120));
    Assert.That(payload.AsSpan(16).ToArray(), Is.EqualTo(compressedBlobs));
  }

  [Test, Category("ErrorHandling")]
  public void Writer_BlockSizeMustBePositive() {
    using var ms = new MemoryStream();
    using var w = new FileFormat.Tfc.TfcWriter(ms, leaveOpen: true);
    Assert.Throws<ArgumentException>(() => w.AddBundle(new byte[16], 0));
  }

  [Test, Category("HappyPath")]
  public void Reader_StopsAtNonMagicAfterValidBundle() {
    var data = new byte[64];
    Array.Fill(data, (byte)0x55);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Tfc.TfcWriter(ms, leaveOpen: true))
      w.AddBundle(data);

    // Append 32 bytes of garbage — reader must clean-EOF rather than throw.
    var garbage = new byte[32];
    Array.Fill(garbage, (byte)0xEE);
    ms.Write(garbage, 0, garbage.Length);

    ms.Position = 0;
    var r = new FileFormat.Tfc.TfcReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].UncompressedSize, Is.EqualTo(data.Length));
  }

  [Test, Category("HappyPath")]
  public void Capabilities_IncludesCanCreate() {
    var d = new FileFormat.Tfc.TfcFormatDescriptor();
    Assert.That(d, Is.InstanceOf<Compression.Registry.IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Tfc.TfcFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Tfc"));
    Assert.That(d.DisplayName, Is.EqualTo("Mass Effect TFC"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".tfc"));
    Assert.That(d.Extensions, Contains.Item(".tfc"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0xC1, 0x83, 0x2A, 0x9E }));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("tfc"));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));

    // Round-trip a single bundle through the descriptor APIs.
    var data = new byte[96];
    Array.Fill(data, (byte)0x77);
    var tempFile = Path.Combine(Path.GetTempPath(), $"tfc_descriptor_{Guid.NewGuid():N}.bin");
    File.WriteAllBytes(tempFile, data);
    try {
      using var ms = new MemoryStream();
      d.Create(ms, [new Compression.Registry.ArchiveInputInfo(tempFile, "mip0.bin", IsDirectory: false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var listed = d.List(ms, password: null);
      Assert.That(listed, Has.Count.EqualTo(1));
      Assert.That(listed[0].Name, Is.EqualTo("bundle_00000.bin"));
      Assert.That(listed[0].OriginalSize, Is.EqualTo(data.Length));
      Assert.That(listed[0].CompressedSize, Is.EqualTo(data.Length));
      Assert.That(listed[0].Method, Is.EqualTo("Stored"));
    } finally {
      File.Delete(tempFile);
    }
  }

  private static byte[] BuildBundle(uint blockSize, uint compressedSize, uint uncompressedSize,
                                    (uint Comp, uint Unc)[] blockTable, byte[] blockData) {
    var size = 16 + blockTable.Length * 8 + blockData.Length;
    var buf = new byte[size];
    var span = buf.AsSpan();

    BinaryPrimitives.WriteUInt32LittleEndian(span[..4],   0x9E2A83C1u);
    BinaryPrimitives.WriteUInt32LittleEndian(span[4..8],  blockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(span[8..12], compressedSize);
    BinaryPrimitives.WriteUInt32LittleEndian(span[12..16], uncompressedSize);

    var pos = 16;
    foreach (var (comp, unc) in blockTable) {
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(pos, 4), comp);
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(pos + 4, 4), unc);
      pos += 8;
    }

    blockData.AsSpan().CopyTo(span[pos..]);
    return buf;
  }

  private static byte[] FilledArray(int length, byte value) {
    var buf = new byte[length];
    Array.Fill(buf, value);
    return buf;
  }
}
