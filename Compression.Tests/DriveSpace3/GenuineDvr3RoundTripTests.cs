using FileSystem.DriveSpace3;

namespace Compression.Tests.DriveSpace3;

/// <summary>
/// Read half of the genuine DriveSpace 3 path: <see cref="GenuineDvr3Reader"/>
/// must read back, byte-exact, what <see cref="GenuineDvr3Writer"/> wrote — over
/// the genuine MSDBL6.0 layout that the dmsdos driver independently certifies as
/// a real DriveSpace 3 CVF (see <c>DriveSpace3GenuineDmsdosTests</c>). Together
/// they give a full read/write path over the genuine on-disk format.
/// </summary>
[TestFixture]
public class GenuineDvr3RoundTripTests {

  [Test, Category("RoundTrip")]
  public void Writer_Then_Reader_IsByteExact_AcrossSizes() {
    var rnd = new Random(20260618);
    var inputs = new (string Name, byte[] Data)[] {
      ("EMPTY.TXT", []),
      ("TINY.TXT", "hi"u8.ToArray()),
      ("ONE.BIN", Make(rnd, 4096)),
      ("EXACT.BIN", Make(rnd, 32768)),           // exactly one 32 KB cluster
      ("MULTI.BIN", Make(rnd, 32768 * 2 + 1234)), // spans 3 clusters
    };

    var w = new GenuineDvr3Writer();
    foreach (var (n, d) in inputs) w.AddFile(n, d);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    using var r = new GenuineDvr3Reader(ms);

    Assert.That(r.Entries.Select(e => e.Name),
      Is.EquivalentTo(inputs.Select(i => i.Name)));

    foreach (var (name, expected) in inputs) {
      var entry = r.Entries.First(e => e.Name == name);
      Assert.That(r.Extract(entry), Is.EqualTo(expected), $"{name} did not round-trip");
    }
  }

  private static byte[] Make(Random rnd, int n) {
    var b = new byte[n];
    rnd.NextBytes(b);
    return b;
  }
}
