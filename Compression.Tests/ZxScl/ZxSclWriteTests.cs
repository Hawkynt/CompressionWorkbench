using Compression.Registry;
using FileSystem.ZxScl;

namespace Compression.Tests.ZxScl;

[TestFixture]
public class ZxSclWriteTests {

  [Test, Category("RoundTrip")]
  public void Write_Build_StartsWithSinclairMagic() {
    var w = new ZxSclWriter();
    w.AddFile("HELLO.cod", "X"u8.ToArray());
    var scl = w.Build();
    Assert.That(scl.AsSpan(0, 8).ToArray(), Is.EqualTo(ZxSclReader.Magic));
  }

  [Test, Category("RoundTrip")]
  public void Write_ThenRead_ListsMatchingNamesAndSizes() {
    var d1 = "LOAD \"\""u8.ToArray();
    var d2 = new byte[300]; new Random(1).NextBytes(d2);
    var d3 = new byte[500]; new Random(2).NextBytes(d3);

    var w = new ZxSclWriter();
    w.AddFile("program.bas", d1);      // BASIC
    w.AddFile("graphics.cod", d2);     // code block
    w.AddFile("data.dat", d3);         // data file
    var scl = w.Build();

    using var r = new ZxSclReader(new MemoryStream(scl));
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("program.bas"));
    Assert.That(r.Entries[0].FileType, Is.EqualTo('B'));
    Assert.That(r.Entries[1].Name, Is.EqualTo("graphics.cod"));
    Assert.That(r.Entries[1].FileType, Is.EqualTo('C'));
    Assert.That(r.Entries[2].Name, Is.EqualTo("data.dat"));
    Assert.That(r.Entries[2].FileType, Is.EqualTo('D'));
  }

  [Test, Category("RoundTrip")]
  public void Write_ThenExtract_PreservesLeadingBytes() {
    var payload = new byte[250];
    new Random(99).NextBytes(payload);
    var w = new ZxSclWriter();
    w.AddFile("PAYLOAD.cod", payload);
    var scl = w.Build();

    using var r = new ZxSclReader(new MemoryStream(scl));
    var extracted = r.Extract(r.Entries[0]);
    // Reader returns whole sectors; first payload.Length bytes must match.
    Assert.That(extracted.Length, Is.EqualTo(256));  // padded to sector
    Assert.That(extracted.AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload));
  }

  [Test, Category("RoundTrip")]
  public void Write_LongFilename_TruncatedToTail() {
    var longName = "VERYLONGPROGRAMNAME.bas";
    var w = new ZxSclWriter();
    w.AddFile(longName, "x"u8.ToArray());
    var scl = w.Build();

    using var r = new ZxSclReader(new MemoryStream(scl));
    // Base-name portion is 8 chars max, truncated from the TAIL of the input base name.
    // "VERYLONGPROGRAMNAME" (19 chars) -> "GRAMNAME" (last 8) + "." + "bas".
    Assert.That(r.Entries[0].Name, Is.EqualTo("GRAMNAME.bas"));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_ViaInterface_RoundTrips() {
    var tmp1 = Path.GetTempFileName();
    var tmp2 = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp1, "HELLO"u8.ToArray());
      var data2 = new byte[700]; new Random(5).NextBytes(data2);
      File.WriteAllBytes(tmp2, data2);

      var desc = new ZxSclFormatDescriptor();
      using var ms = new MemoryStream();
      ((IArchiveCreatable)desc).Create(ms,
        [new ArchiveInputInfo(tmp1, "HI.bas", false), new ArchiveInputInfo(tmp2, "DATA.cod", false)],
        new FormatCreateOptions());

      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(2));

      // Verify byte-exact extraction (file data is sector-padded — trim to original length).
      var extractDir = Path.Combine(Path.GetTempPath(), "zxscl_test_" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(extractDir);
      try {
        ms.Position = 0;
        desc.Extract(ms, extractDir, null, null);
        var ext1 = File.ReadAllBytes(Path.Combine(extractDir, "HI.bas"));
        Assert.That(ext1.AsSpan(0, 5).ToArray(), Is.EqualTo("HELLO"u8.ToArray()));
        var ext2 = File.ReadAllBytes(Path.Combine(extractDir, "DATA.cod"));
        Assert.That(ext2.AsSpan(0, data2.Length).ToArray(), Is.EqualTo(data2));
      } finally {
        Directory.Delete(extractDir, recursive: true);
      }
    } finally {
      File.Delete(tmp1);
      File.Delete(tmp2);
    }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_CanCreateFlag() {
    var d = new ZxSclFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.MaxTotalArchiveSize, Is.EqualTo(ZxSclReader.MaxPayloadSize));
  }
}
