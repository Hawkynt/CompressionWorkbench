using Compression.Registry;
using FileSystem.ProDos;

namespace Compression.Tests.ProDos;

[TestFixture]
public class ProDosWriteTests {

  [Test, Category("RoundTrip")]
  public void Write_Build_ProducesFloppySize() {
    var w = new ProDosWriter();
    w.AddFile("HELLO", "HELLO"u8.ToArray());
    var img = w.Build();
    Assert.That(img.Length, Is.EqualTo(ProDosWriter.FloppyTotalBlocks * ProDosReader.BlockSize));  // 143 360
  }

  [Test, Category("RoundTrip")]
  public void Write_ThenRead_ListsMatchingNamesAndSizes() {
    var data1 = "PRODOS RULES"u8.ToArray();
    var data2 = new byte[300]; new Random(42).NextBytes(data2);
    var data3 = new byte[700]; new Random(84).NextBytes(data3);

    var w = new ProDosWriter();
    w.AddFile("ONE", data1);
    w.AddFile("TWO", data2);
    w.AddFile("THREE", data3);
    var img = w.Build();

    using var r = new ProDosReader(new MemoryStream(img));
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("ONE"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data1.Length));
    Assert.That(r.Entries[1].Name, Is.EqualTo("TWO"));
    Assert.That(r.Entries[1].Size, Is.EqualTo(data2.Length));
    Assert.That(r.Entries[2].Name, Is.EqualTo("THREE"));
    Assert.That(r.Entries[2].Size, Is.EqualTo(data3.Length));
  }

  [Test, Category("RoundTrip")]
  public void Write_Seedling_RoundTripsByteExact() {
    var content = "ProDOS seedling bytes"u8.ToArray();
    var w = new ProDosWriter();
    w.AddFile("FILE1", content);
    var img = w.Build();

    using var r = new ProDosReader(new MemoryStream(img));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("RoundTrip")]
  public void Write_Sapling_RoundTripsByteExact() {
    // > 512 bytes to force sapling storage (index block + multiple data blocks).
    var content = new byte[5000];
    new Random(99).NextBytes(content);
    var w = new ProDosWriter();
    w.AddFile("BIG.FILE", content);
    var img = w.Build();

    using var r = new ProDosReader(new MemoryStream(img));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].StorageType, Is.EqualTo(2));  // sapling
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("RoundTrip")]
  public void Write_LongFilename_TruncatedToTail() {
    var name = "ThisIsAReallyLongProDosName"; // 27 chars
    var w = new ProDosWriter();
    w.AddFile(name, "x"u8.ToArray());
    var img = w.Build();

    using var r = new ProDosReader(new MemoryStream(img));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name.Length, Is.LessThanOrEqualTo(15));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_ViaInterface_RoundTrips() {
    var tmp1 = Path.GetTempFileName();
    var tmp2 = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp1, "Hello"u8.ToArray());
      var data2 = new byte[900]; new Random(7).NextBytes(data2);
      File.WriteAllBytes(tmp2, data2);

      var desc = new ProDosFormatDescriptor();
      using var ms = new MemoryStream();
      ((IArchiveCreatable)desc).Create(ms,
        [new ArchiveInputInfo(tmp1, "GREETING", false), new ArchiveInputInfo(tmp2, "BIG.DAT", false)],
        new FormatCreateOptions());

      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(2));

      // Verify byte-exact extraction by writing to a temp dir.
      var extractDir = Path.Combine(Path.GetTempPath(), "prodos_test_" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(extractDir);
      try {
        ms.Position = 0;
        desc.Extract(ms, extractDir, null, null);
        // The reader upper-cases and sanitizes names: "GREETING" -> "GREETING", "BIG.DAT" -> "BIG.DAT"
        Assert.That(File.ReadAllBytes(Path.Combine(extractDir, "GREETING")),
          Is.EqualTo(File.ReadAllBytes(tmp1)));
        Assert.That(File.ReadAllBytes(Path.Combine(extractDir, "BIG.DAT")),
          Is.EqualTo(data2));
      } finally {
        Directory.Delete(extractDir, recursive: true);
      }
    } finally {
      File.Delete(tmp1);
      File.Delete(tmp2);
    }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_ExposesCanonicalSizes_AndCanCreateFlag() {
    var d = new ProDosFormatDescriptor();
    Assert.That(d.CanonicalSizes, Does.Contain(143360L));
    Assert.That(d.CanonicalSizes, Does.Contain(819200L));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }
}
