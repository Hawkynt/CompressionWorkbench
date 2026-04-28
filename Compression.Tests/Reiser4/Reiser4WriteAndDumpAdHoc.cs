// Dev-only ad-hoc helper: writes an image to D:\temp\our.r4 so we can
// validate it with `wsl fsck.reiser4 /tmp/our.r4`. Marked Explicit so it
// only runs when the developer asks for it.
using FileSystem.Reiser4;

namespace Compression.Tests.Reiser4;

[TestFixture]
public class Reiser4WriteAndDumpAdHoc {
  [Test, Explicit]
  public void DumpToTemp() {
    var w = new Reiser4Writer { BlockCount = 4096, Label = "OURFS", MkfsId = 0xCAFEBABEu };
    var img = w.Build();
    Directory.CreateDirectory(@"D:\temp");
    File.WriteAllBytes(@"D:\temp\our.r4", img);
    TestContext.Out.WriteLine($"Wrote {img.Length} bytes to D:\\temp\\our.r4");
  }
}
