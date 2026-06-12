using FileSystem.Hammer;

namespace Compression.Tests.Hammer;

/// <summary>
/// Manual oracle harness: writes a real HAMMER image to <c>/tmp/hammer_verify.img</c>
/// so it can be attached to a DragonFly BSD verification VM
/// (<c>mount_hammer /dev/vbd1 /mnt/h &amp;&amp; hammer info /mnt/h</c>, both rc=0).
/// Ignored in CI — run explicitly when validating against DragonFly.
/// </summary>
[TestFixture]
public class HammerImageDumpTests {

  [Test, Ignore("manual: DragonFly oracle verification only")]
  public void WriteVerifyImage() {
    var w = new HammerWriter { Label = "test" };
    w.AddFile("hello.txt", "hello hammer\n"u8.ToArray());
    w.AddFile("readme.md", "# HAMMER\nfull read/write\n"u8.ToArray());
    var mid = new byte[5000];
    for (var i = 0; i < mid.Length; ++i) mid[i] = (byte)('A' + (i % 26));
    w.AddFile("mid.txt", mid);
    var big = new byte[40000];
    for (var i = 0; i < big.Length; ++i) big[i] = (byte)(i * 37 + 11);
    w.AddFile("big.bin", big);

    using var fs = File.Create("/tmp/hammer_verify.img");
    w.WriteTo(fs);

    Assert.That(fs.Length, Is.GreaterThanOrEqualTo(16 * 1024 * 1024));
  }
}
