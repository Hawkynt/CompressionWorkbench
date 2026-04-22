using Compression.Registry;
using FileSystem.Atari8;

namespace Compression.Tests.Atari8;

[TestFixture]
public class Atari8WriteTests {

  [Test, Category("RoundTrip")]
  public void Write_Build_ProducesSsSdCanonicalSize() {
    var w = new Atari8Writer();
    w.AddFile("HELLO.TXT", "HELLO"u8.ToArray());
    var img = w.Build();
    Assert.That(img.Length, Is.EqualTo(Atari8Writer.ImageSize));  // 92 176
    // ATR magic check.
    Assert.That(img[0], Is.EqualTo(0x96));
    Assert.That(img[1], Is.EqualTo(0x02));
  }

  [Test, Category("RoundTrip")]
  public void Write_ThenRead_ListsMatchingNamesAndSizes() {
    var d1 = "FIRST"u8.ToArray();
    var d2 = new byte[200]; for (var i = 0; i < d2.Length; i++) d2[i] = (byte)i;
    var d3 = new byte[450]; for (var i = 0; i < d3.Length; i++) d3[i] = (byte)(i * 3);

    var w = new Atari8Writer();
    w.AddFile("ONE.TXT", d1);
    w.AddFile("TWO.DAT", d2);
    w.AddFile("THREE.BIN", d3);
    var img = w.Build();

    using var r = new Atari8Reader(new MemoryStream(img));
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("ONE.TXT"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("TWO.DAT"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("THREE.BIN"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(d1.Length));
    Assert.That(r.Entries[1].Size, Is.EqualTo(d2.Length));
    Assert.That(r.Entries[2].Size, Is.EqualTo(d3.Length));
  }

  [Test, Category("RoundTrip")]
  public void Write_ThenExtract_RoundTripsByteExactSingleSector() {
    var payload = "ATARI 800"u8.ToArray();
    var w = new Atari8Writer();
    w.AddFile("HELLO.TXT", payload);
    var img = w.Build();

    using var r = new Atari8Reader(new MemoryStream(img));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(payload));
  }

  [Test, Category("RoundTrip")]
  public void Write_ThenExtract_RoundTripsByteExactMultiSector() {
    // > 125 (SectorSize - 3) bytes to force a multi-sector chain.
    var payload = new byte[1000];
    new Random(1).NextBytes(payload);
    var w = new Atari8Writer();
    w.AddFile("BIG.DAT", payload);
    var img = w.Build();

    using var r = new Atari8Reader(new MemoryStream(img));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(payload));
  }

  [Test, Category("RoundTrip")]
  public void Write_LongFilename_TruncatedToTail() {
    var longName = "VERYLONGFILENAME.EXT";
    var w = new Atari8Writer();
    w.AddFile(longName, "x"u8.ToArray());
    var img = w.Build();

    using var r = new Atari8Reader(new MemoryStream(img));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    // Base-name capped at 8, extension kept at 3 (head for ext, tail for base-name).
    Assert.That(r.Entries[0].Name, Is.EqualTo("FILENAME.EXT"));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_ViaInterface_RoundTrips() {
    var tmp1 = Path.GetTempFileName();
    var tmp2 = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp1, "abc"u8.ToArray());
      var data2 = new byte[400]; new Random(11).NextBytes(data2);
      File.WriteAllBytes(tmp2, data2);

      var desc = new Atari8FormatDescriptor();
      using var ms = new MemoryStream();
      ((IArchiveCreatable)desc).Create(ms,
        [new ArchiveInputInfo(tmp1, "HI.TXT", false), new ArchiveInputInfo(tmp2, "BIG.DAT", false)],
        new FormatCreateOptions());

      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(2));

      var extractDir = Path.Combine(Path.GetTempPath(), "atari_test_" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(extractDir);
      try {
        ms.Position = 0;
        desc.Extract(ms, extractDir, null, null);
        Assert.That(File.ReadAllBytes(Path.Combine(extractDir, "HI.TXT")),
          Is.EqualTo("abc"u8.ToArray()));
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
  public void Descriptor_ExposesCanonicalSize_AndCanCreateFlag() {
    var d = new Atari8FormatDescriptor();
    Assert.That(d.CanonicalSizes, Does.Contain((long)Atari8Writer.ImageSize));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }
}
