using Compression.Lib;
using Compression.Registry;
using FileFormat.AppleSparse;

namespace Compression.Tests.AppleSparse;

[TestFixture]
public class SparseimageTests {

  [OneTimeSetUp]
  public void EnsureRegistry() => FormatRegistration.EnsureInitialized();

  // ── Descriptor ─────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new SparseimageFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(desc.Id, Is.EqualTo("Sparseimage"));
      Assert.That(desc.DefaultExtension, Is.EqualTo(".sparseimage"));
      Assert.That(desc.Extensions, Does.Contain(".sparseimage"));
      Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
      Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("sprs"u8.ToArray()));
      Assert.That(desc.Category, Is.EqualTo(FormatCategory.Archive));
      Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(desc.Family, Is.EqualTo(AlgorithmFamily.Archive));
    });
  }

  // ── Reader/Writer round-trip ──────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Writer_Produces_SprsMagic() {
    var w = new SparseimageWriter();
    w.SetDiskData(new byte[2048]);
    var bytes = w.Build();
    Assert.That(bytes.AsSpan(0, 4).ToArray(), Is.EqualTo("sprs"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_NonZeroData() {
    var data = new byte[SparseimageReader.SectorSize * 100];
    new Random(11).NextBytes(data);
    var w = new SparseimageWriter();
    w.SetSectorsPerBand(8); // 4 KB band — small so the test exercises many bands
    w.SetDiskData(data);
    var bytes = w.Build();

    using var ms = new MemoryStream(bytes);
    using var r = new SparseimageReader(ms, leaveOpen: true);
    Assert.That(r.VirtualSize, Is.GreaterThanOrEqualTo(data.Length));

    var extracted = r.ExtractDisk();
    // Padded to band boundary
    Assert.That(extracted.AsSpan(0, data.Length).ToArray(), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MixedSparseAndDataBands() {
    // 3 bands: band 0 = zeros, band 1 = random, band 2 = zeros
    const int sectorsPerBand = 8;
    var bandBytes = sectorsPerBand * SparseimageReader.SectorSize;
    var data = new byte[bandBytes * 3];
    new Random(7).NextBytes(data.AsSpan(bandBytes, bandBytes));

    var w = new SparseimageWriter();
    w.SetSectorsPerBand(sectorsPerBand);
    w.SetDiskData(data);
    var bytes = w.Build();

    // Sparse layout should be smaller than the virtual disk
    Assert.That(bytes.Length, Is.LessThan(data.Length + bandBytes /*headroom*/),
      "Sparse layout should not store the two empty bands");

    using var ms = new MemoryStream(bytes);
    using var r = new SparseimageReader(ms, leaveOpen: true);
    var extracted = r.ExtractDisk();
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_AllZeroData_ProducesAllSparseBat() {
    var data = new byte[8192];
    var w = new SparseimageWriter();
    w.SetSectorsPerBand(8);
    w.SetDiskData(data);
    var bytes = w.Build();

    using var ms = new MemoryStream(bytes);
    using var r = new SparseimageReader(ms, leaveOpen: true);
    var extracted = r.ExtractDisk();
    Assert.That(extracted, Is.EqualTo(data));
    // With all-zero input no physical bands should be stored: size = header + BAT (padded).
    Assert.That(bytes.Length, Is.LessThanOrEqualTo(SparseimageReader.HeaderSize + 4096));
  }

  [Test, Category("RoundTrip")]
  public void Stream_Read_Matches_ExtractDisk() {
    var data = new byte[4096];
    new Random(42).NextBytes(data);
    var w = new SparseimageWriter();
    w.SetSectorsPerBand(8);
    w.SetDiskData(data);
    var bytes = w.Build();

    using var ms = new MemoryStream(bytes);
    using var s = SparseimageStream.TryOpen(ms);
    Assert.That(s, Is.Not.Null);
    var buf = new byte[s!.Length];
    s.Position = 0;
    s.ReadExactly(buf);
    Assert.That(buf.AsSpan(0, data.Length).ToArray(), Is.EqualTo(data));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new SparseimageReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[SparseimageReader.HeaderSize];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new SparseimageReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Stream_TryOpen_ReturnsNull_OnBadMagic() {
    using var ms = new MemoryStream(new byte[SparseimageReader.HeaderSize]);
    Assert.That(SparseimageStream.TryOpen(ms), Is.Null);
  }

  // ── Descriptor list/extract ───────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_List_RawFallback_YieldsDiskImg() {
    var w = new SparseimageWriter();
    w.SetSectorsPerBand(8);
    w.SetDiskData(new byte[]{ 1, 2, 3, 4 });
    var bytes = w.Build();
    using var ms = new MemoryStream(bytes);
    var desc = new SparseimageFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Is.Not.Empty);
    Assert.That(entries.Any(e => e.Name == "disk.img"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Extract_RawFallback_WritesDiskImg() {
    var disk = new byte[]{ 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 };
    var w = new SparseimageWriter();
    w.SetSectorsPerBand(2); // 1 KB band
    w.SetDiskData(disk);
    var bytes = w.Build();
    using var ms = new MemoryStream(bytes);

    var tmp = Path.Combine(Path.GetTempPath(), "cwb_sparseimg_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmp);
      var desc = new SparseimageFormatDescriptor();
      desc.Extract(ms, tmp, null, null);
      var diskImg = Path.Combine(tmp, "disk.img");
      Assert.That(File.Exists(diskImg), Is.True);
      var content = File.ReadAllBytes(diskImg);
      Assert.That(content.AsSpan(0, disk.Length).ToArray(), Is.EqualTo(disk));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_RoundTrip() {
    var disk = new byte[8192];
    new Random(33).NextBytes(disk);
    var desc = new SparseimageFormatDescriptor();
    var input = ArchiveInputInfo.InMemory("disk.img", disk);
    using var ms = new MemoryStream();
    desc.Create(ms, [input], new FormatCreateOptions());
    Assert.That(ms.Length, Is.GreaterThan(0));

    ms.Position = 0;
    using var r = new SparseimageReader(ms, leaveOpen: true);
    var extracted = r.ExtractDisk();
    Assert.That(extracted.AsSpan(0, disk.Length).ToArray(), Is.EqualTo(disk));
  }

  // ── Inner-FS delegation (FAT inside sparseimage) ──────────────────

  [Test, Category("HappyPath")]
  public void List_SparseimageWrappingFat_SeesInnerFiles() {
    using var img = BuildSparseimageWithFat("HELLO.TXT", "world"u8.ToArray(),
                                             "DATA.BIN", new byte[] { 1, 2, 3 });
    var desc = new SparseimageFormatDescriptor();
    var entries = desc.List(img, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(2));
    Assert.That(entries.Any(e => e.Name.Contains("HELLO", StringComparison.OrdinalIgnoreCase)), Is.True,
      "Should see HELLO.TXT from inner FAT");
    Assert.That(entries.Any(e => e.Name.Contains("DATA", StringComparison.OrdinalIgnoreCase)), Is.True,
      "Should see DATA.BIN from inner FAT");
  }

  [Test, Category("HappyPath")]
  public void Extract_SparseimageWrappingFat_ExtractsInnerFiles() {
    using var img = BuildSparseimageWithFat("HELLO.TXT", "world"u8.ToArray());
    var desc = new SparseimageFormatDescriptor();
    var tmp = Path.Combine(Path.GetTempPath(), "cwb_sparseimg_inner_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmp);
      desc.Extract(img, tmp, null, null);
      var extracted = Directory.GetFiles(tmp, "*", SearchOption.AllDirectories);
      Assert.That(extracted, Has.Length.GreaterThanOrEqualTo(1));
      var hello = extracted.FirstOrDefault(f => Path.GetFileName(f)
        .Contains("HELLO", StringComparison.OrdinalIgnoreCase));
      Assert.That(hello, Is.Not.Null);
      Assert.That(File.ReadAllBytes(hello!), Is.EqualTo("world"u8.ToArray()));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static MemoryStream BuildSparseimageWithFat(params object[] nameDataPairs) {
    var fatWriter = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < nameDataPairs.Length; i += 2) {
      var name = (string)nameDataPairs[i];
      var data = (byte[])nameDataPairs[i + 1];
      fatWriter.AddFile(name, data);
    }
    var fatImage = fatWriter.Build();

    var w = new SparseimageWriter();
    w.SetDiskData(fatImage);
    var bytes = w.Build();
    var ms = new MemoryStream();
    ms.Write(bytes);
    ms.Position = 0;
    return ms;
  }
}
