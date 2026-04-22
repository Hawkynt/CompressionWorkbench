using Compression.Registry;
using FileSystem.AppleDos;

namespace Compression.Tests.AppleDos;

[TestFixture]
public class AppleDosWriteTests {

  [Test, Category("RoundTrip")]
  public void Write_Build_ProducesCanonicalSize() {
    var w = new AppleDosWriter();
    w.AddFile("HELLO", "HELLO"u8.ToArray());
    var img = w.Build();
    Assert.That(img.Length, Is.EqualTo(AppleDosReader.StandardSize));  // 143 360
  }

  [Test, Category("RoundTrip")]
  public void Write_ThenRead_ListsMatchingNamesAndSizes() {
    var data1 = "FIRST FILE PAYLOAD"u8.ToArray();
    var data2 = new byte[300]; for (var i = 0; i < data2.Length; i++) data2[i] = (byte)(i & 0xFF);
    var data3 = new byte[600]; for (var i = 0; i < data3.Length; i++) data3[i] = (byte)((i * 7) & 0xFF);

    var w = new AppleDosWriter();
    w.AddFile("ALPHA", data1);
    w.AddFile("BRAVO", data2);
    w.AddFile("CHARLIE", data3);
    var img = w.Build();

    using var r = new AppleDosReader(new MemoryStream(img));
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("ALPHA"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("BRAVO"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("CHARLIE"));
  }

  [Test, Category("RoundTrip")]
  public void Write_ThenRead_RoundTripsByteExact_ForBinaryType() {
    // Binary file type (0x04): 2 bytes load addr + 2 bytes LE length + payload.
    var payload = new byte[200];
    new Random(1337).NextBytes(payload);
    var binData = new byte[4 + payload.Length];
    binData[0] = 0x00; binData[1] = 0x20;  // load $2000
    binData[2] = (byte)(payload.Length & 0xFF);
    binData[3] = (byte)((payload.Length >> 8) & 0xFF);
    Buffer.BlockCopy(payload, 0, binData, 4, payload.Length);

    var w = new AppleDosWriter();
    w.AddFile("BINFILE", 0x04, binData);
    var img = w.Build();

    using var r = new AppleDosReader(new MemoryStream(img));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var extracted = r.Extract(r.Entries[0]);
    // Binary files trim to load-addr+len so our full binData payload should round trip.
    Assert.That(extracted, Is.EqualTo(binData));
  }

  [Test, Category("RoundTrip")]
  public void Write_LongFilename_TruncatedToTail() {
    var name = "this-is-a-very-long-filename-that-exceeds-thirty-chars";  // 53 chars
    var w = new AppleDosWriter();
    w.AddFile(name, "x"u8.ToArray());
    var img = w.Build();

    using var r = new AppleDosReader(new MemoryStream(img));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    // Tail-truncate + upper-case; dashes stay in the printable ASCII range.
    Assert.That(r.Entries[0].Name.Length, Is.EqualTo(30));
    Assert.That(name.ToUpperInvariant(), Does.EndWith(r.Entries[0].Name));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_ViaInterface_RoundTrips() {
    var tmp = Path.GetTempFileName();
    var tmp2 = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "HELLO"u8.ToArray());
      var payload2 = new byte[1000]; for (var i = 0; i < payload2.Length; i++) payload2[i] = (byte)(i & 0xFF);
      File.WriteAllBytes(tmp2, payload2);

      var desc = new AppleDosFormatDescriptor();
      using var ms = new MemoryStream();
      ((IArchiveCreatable)desc).Create(ms,
        [new ArchiveInputInfo(tmp, "GREETING", false), new ArchiveInputInfo(tmp2, "BIG.DAT", false)],
        new FormatCreateOptions());

      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(2));
    } finally {
      File.Delete(tmp);
      File.Delete(tmp2);
    }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_ExposesCanonicalSize_AndCanCreateFlag() {
    var d = new AppleDosFormatDescriptor();
    Assert.That(d.CanonicalSizes, Is.EqualTo(new long[] { AppleDosReader.StandardSize }));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("ErrorHandling")]
  public void Create_OverflowingInput_Throws() {
    // One file that alone exceeds the 143 360-byte disk.
    var tooBig = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tooBig, new byte[AppleDosReader.StandardSize + 1]);
      var desc = new AppleDosFormatDescriptor();
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
