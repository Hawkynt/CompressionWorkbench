using Compression.Registry;
using FileSystem.Bbc;

namespace Compression.Tests.Bbc;

[TestFixture]
public class BbcWriteTests {

  [Test, Category("RoundTrip")]
  public void Write_Build_ProducesCanonicalSize() {
    var w = new BbcWriter();
    w.AddFile("HELLO", "X"u8.ToArray());
    var img = w.Build();
    Assert.That(img.Length, Is.EqualTo(BbcWriter.DiskSize40));  // 102 400
  }

  [Test, Category("RoundTrip")]
  public void Write_ThenRead_ListsMatchingNamesAndSizes() {
    var d1 = "FIRST"u8.ToArray();
    var d2 = new byte[300]; new Random(1).NextBytes(d2);
    var d3 = new byte[600]; new Random(2).NextBytes(d3);

    var w = new BbcWriter();
    w.AddFile("ALPHA", d1);
    w.AddFile("BRAVO", d2);
    w.AddFile("CHARLIE", d3);
    var img = w.Build();

    using var r = new BbcReader(new MemoryStream(img));
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("ALPHA"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(d1.Length));
    Assert.That(r.Entries[1].Name, Is.EqualTo("BRAVO"));
    Assert.That(r.Entries[1].Size, Is.EqualTo(d2.Length));
    // "CHARLIE" is 7 chars — fits DFS exactly.
    Assert.That(r.Entries[2].Name, Is.EqualTo("CHARLIE"));
    Assert.That(r.Entries[2].Size, Is.EqualTo(d3.Length));
  }

  [Test, Category("RoundTrip")]
  public void Write_ThenExtract_RoundTripsByteExact() {
    var payload = new byte[1234];
    new Random(13).NextBytes(payload);

    var w = new BbcWriter();
    w.AddFile("PAYLD", payload);
    var img = w.Build();

    using var r = new BbcReader(new MemoryStream(img));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(payload));
  }

  [Test, Category("RoundTrip")]
  public void Write_LongFilename_TruncatedToTail7() {
    var longName = "ABCDEFGHIJKLMNOP";  // 16 chars
    var w = new BbcWriter();
    w.AddFile(longName, "x"u8.ToArray());
    var img = w.Build();

    using var r = new BbcReader(new MemoryStream(img));
    Assert.That(r.Entries[0].Name.Length, Is.EqualTo(7));
    Assert.That(longName, Does.EndWith(r.Entries[0].Name));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_ViaInterface_RoundTrips() {
    var tmp1 = Path.GetTempFileName();
    var tmp2 = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp1, "Hello BBC Micro!"u8.ToArray());
      var data2 = new byte[512]; new Random(5).NextBytes(data2);
      File.WriteAllBytes(tmp2, data2);

      var desc = new BbcFormatDescriptor();
      using var ms = new MemoryStream();
      ((IArchiveCreatable)desc).Create(ms,
        [new ArchiveInputInfo(tmp1, "HI", false), new ArchiveInputInfo(tmp2, "BIN", false)],
        new FormatCreateOptions());

      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(2));

      // Verify byte-exact extraction.
      var extractDir = Path.Combine(Path.GetTempPath(), "bbc_test_" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(extractDir);
      try {
        ms.Position = 0;
        desc.Extract(ms, extractDir, null, null);
        // Extractor strips the "$." prefix for root-dir files.
        Assert.That(File.ReadAllBytes(Path.Combine(extractDir, "HI")),
          Is.EqualTo(File.ReadAllBytes(tmp1)));
        Assert.That(File.ReadAllBytes(Path.Combine(extractDir, "BIN")),
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
    var d = new BbcFormatDescriptor();
    Assert.That(d.CanonicalSizes, Does.Contain((long)BbcWriter.DiskSize40));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("ErrorHandling")]
  public void Create_OverflowingInput_Throws() {
    var tooBig = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tooBig, new byte[BbcWriter.DiskSize40 + 1]);
      var desc = new BbcFormatDescriptor();
      using var ms = new MemoryStream();
      Assert.Throws<InvalidOperationException>(() =>
        ((IArchiveCreatable)desc).Create(ms,
          [new ArchiveInputInfo(tooBig, "HUGE", false)],
          new FormatCreateOptions()));
    } finally {
      File.Delete(tooBig);
    }
  }
}
