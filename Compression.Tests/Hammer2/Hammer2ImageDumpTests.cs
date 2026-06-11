using FileSystem.Hammer2;

namespace Compression.Tests.Hammer2;

/// <summary>
/// Manual oracle harness: writes a real HAMMER2 image to
/// <c>/tmp/hammer2_verify.img</c> so it can be attached to a DragonFly BSD
/// verification VM (<c>mount_hammer2 /dev/vbd1@LOCAL /mnt &amp;&amp; ls /mnt</c>,
/// both rc=0). Ignored in CI — run explicitly when validating against DragonFly.
/// </summary>
[TestFixture]
public class Hammer2ImageDumpTests {

  [Test, Ignore("manual: DragonFly oracle verification only")]
  public void WriteVerifyImage() {
    var w = new Hammer2Writer { Label = "test" };
    w.AddFile("hello.txt", "hello hammer2\n"u8.ToArray());

    using var fs = File.Create("/tmp/hammer2_verify.img");
    w.WriteTo(fs);

    Assert.That(fs.Length, Is.GreaterThanOrEqualTo(32 * 1024 * 1024));
  }
}
