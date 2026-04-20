using FileFormat.Msi;

namespace Compression.Tests.Msi;

[TestFixture]
public class CfbWriterTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTripsThroughMsiReader() {
    var w = new CfbWriter();
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new MsiReader(ms);
    // Just the root storage entry, no streams.
    Assert.That(r.Entries.Where(e => !e.IsDirectory), Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleStream_RoundTripsExactBytes() {
    var payload = "hello cfb world"u8.ToArray();
    var w = new CfbWriter();
    w.AddStream("hello.txt", payload);

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new MsiReader(ms);
    var streams = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(streams, Has.Count.EqualTo(1));
    Assert.That(streams[0].FullPath, Is.EqualTo("hello.txt"));
    Assert.That(r.Extract(streams[0]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void MultipleStreams_AllRoundTrip() {
    var p1 = new byte[100];
    var p2 = new byte[200];
    var p3 = new byte[300];
    new Random(1).NextBytes(p1);
    new Random(2).NextBytes(p2);
    new Random(3).NextBytes(p3);

    var w = new CfbWriter();
    w.AddStream("a.bin", p1);
    w.AddStream("b.bin", p2);
    w.AddStream("c.bin", p3);

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new MsiReader(ms);
    var streams = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.FullPath);
    Assert.That(streams, Has.Count.EqualTo(3));
    Assert.That(r.Extract(streams["a.bin"]), Is.EqualTo(p1));
    Assert.That(r.Extract(streams["b.bin"]), Is.EqualTo(p2));
    Assert.That(r.Extract(streams["c.bin"]), Is.EqualTo(p3));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void StreamSpanningManySectors_RoundTrips() {
    // 50 KB = 100 sectors; forces multi-sector chain.
    var payload = new byte[50_000];
    new Random(42).NextBytes(payload);
    var w = new CfbWriter();
    w.AddStream("big.bin", payload);

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new MsiReader(ms);
    var stream = r.Entries.First(e => !e.IsDirectory);
    Assert.That(r.Extract(stream), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EmptyStream_RoundTripsAsZeroBytes() {
    var w = new CfbWriter();
    w.AddStream("empty.txt", []);
    w.AddStream("nonempty.txt", "x"u8.ToArray());

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new MsiReader(ms);
    var byName = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.FullPath);
    Assert.That(r.Extract(byName["empty.txt"]), Is.Empty);
    Assert.That(r.Extract(byName["nonempty.txt"]), Is.EqualTo("x"u8.ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void NameLongerThan31_Throws() {
    var w = new CfbWriter();
    Assert.Throws<ArgumentException>(() => w.AddStream(new string('a', 32), [1]));
  }

  [Test, Category("ErrorHandling")]
  public void EmptyName_Throws() {
    var w = new CfbWriter();
    Assert.Throws<ArgumentException>(() => w.AddStream("", [1]));
  }

  [Test, Category("ErrorHandling")]
  public void TooLargePayload_Throws() {
    // 109 FAT sectors × 128 entries × 512 bytes ≈ 6.8 MB capacity.
    // 8 MB single payload busts the limit.
    var w = new CfbWriter();
    w.AddStream("huge.bin", new byte[8 * 1024 * 1024]);
    using var ms = new MemoryStream();
    Assert.Throws<InvalidOperationException>(() => w.WriteTo(ms));
  }

  [Test, Category("HappyPath")]
  public void WrittenFile_HasCfbMagic() {
    var w = new CfbWriter();
    w.AddStream("a.txt", "x"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var bytes = ms.ToArray();
    Assert.That(bytes[..8], Is.EqualTo(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }));
  }
}
