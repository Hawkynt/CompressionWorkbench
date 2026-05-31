namespace Compression.Tests.Adf;

[TestFixture]
public class AdfWriterTests {

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello Amiga!"u8.ToArray();
    var w = new FileSystem.Adf.AdfWriter();
    w.AddFile("hello.txt", data);
    var disk = w.Build();

    Assert.That(disk.Length, Is.EqualTo(901120));

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Adf.AdfReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("hello.txt"));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileSystem.Adf.AdfWriter();
    w.AddFile("file1", "First"u8.ToArray());
    w.AddFile("file2", "Second"u8.ToArray());
    w.AddFile("file3", "Third"u8.ToArray());
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Adf.AdfReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[5000];
    new Random(42).NextBytes(data);
    var w = new FileSystem.Adf.AdfWriter();
    w.AddFile("bigfile", data);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Adf.AdfReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void FFS_Detected() {
    var w = new FileSystem.Adf.AdfWriter();
    w.AddFile("test", new byte[10]);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Adf.AdfReader(ms);
    Assert.That(r.IsFfs, Is.True);
  }

  [Test, Category("RoundTrip")]
  public void EmptyDisk() {
    var w = new FileSystem.Adf.AdfWriter();
    var disk = w.Build();
    Assert.That(disk.Length, Is.EqualTo(901120));

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Adf.AdfReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_ViaInterface() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, new byte[10]);
      var desc = new FileSystem.Adf.AdfFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)desc).Create(ms, [new Compression.Registry.ArchiveInputInfo(tmpFile, "TEST", false)], new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
    } finally {
      File.Delete(tmpFile);
    }
  }

  // ── Timestamp tests ───────────────────────────────────────────────────────

  [Test, Category("Spec")]
  public void Timestamps_AreNonZero_InFileHeader() {
    // File header block is always allocated at sector 882 (first free sector
    // after root=880 and bitmap=881 on a fresh empty disk).
    var w = new FileSystem.Adf.AdfWriter();
    w.AddFile("test.txt", new byte[10]);
    var disk = w.Build();

    const int headerSector = 882;
    var days = ReadUInt32BE(disk, headerSector * 512 + 420);
    Assert.That(days, Is.GreaterThan(0u),
      "File header timestamp (days since 1978-01-01) should be non-zero");
  }

  [Test, Category("Spec")]
  public void Timestamps_RoundTrip_WhenProvided() {
    var target = new DateTime(2024, 3, 15, 12, 30, 0);
    var w = new FileSystem.Adf.AdfWriter();
    w.AddFile("test.txt", new byte[10], target);
    var disk = w.Build();

    // Amiga epoch = Jan 1, 1978.
    var epoch = new DateTime(1978, 1, 1);
    var expectedDays = (uint)(target - epoch).Days;
    var expectedMins = (uint)(target.Hour * 60 + target.Minute);

    const int headerSector = 882;
    var days = ReadUInt32BE(disk, headerSector * 512 + 420);
    var mins = ReadUInt32BE(disk, headerSector * 512 + 424);
    Assert.That(days, Is.EqualTo(expectedDays));
    Assert.That(mins, Is.EqualTo(expectedMins));
  }

  [Test, Category("Spec")]
  public void RootBlock_HasNonZeroTimestamp() {
    var w = new FileSystem.Adf.AdfWriter();
    w.AddFile("f", new byte[1]);
    var disk = w.Build();

    // Root block last-alteration timestamp at offset 420 within sector 880.
    var days = ReadUInt32BE(disk, 880 * 512 + 420);
    Assert.That(days, Is.GreaterThan(0u), "Root block modification timestamp must be non-zero");
  }

  // ── Disk-full test ────────────────────────────────────────────────────────

  [Test, Category("ErrorHandling")]
  public void DiskFull_ThrowsInsteadOfSilentlyTruncating() {
    // 1755 data sectors + 1 header sector = 1756 = all 1756 usable sectors
    // consumed by the first file. The second file cannot fit.
    var w = new FileSystem.Adf.AdfWriter();
    w.AddFile("big", new byte[1755 * 512]);
    w.AddFile("overflow", new byte[1]);

    var ex = Assert.Throws<InvalidOperationException>(() => w.Build());
    Assert.That(ex!.Message, Does.Contain("full"),
      "Exception should mention that the disk is full");
  }

  private static uint ReadUInt32BE(byte[] data, int offset) =>
    (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
}
