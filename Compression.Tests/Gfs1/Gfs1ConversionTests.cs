#pragma warning disable CS1591
using Compression.Lib;
using FileSystem.Gfs1;

namespace Compression.Tests.Gfs1;

[TestFixture]
public class Gfs1ConversionTests {

  [Test, Category("Conversion")]
  public void ConvertGfs1ToTar_AndBack_PreservesContents() {
    FormatRegistration.EnsureInitialized();

    var dir = Path.Combine(Path.GetTempPath(), "cwb_gfs1_conv_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try {
      var aBytes = "hello gfs1"u8.ToArray();
      var bBytes = new byte[256];
      for (var i = 0; i < bBytes.Length; ++i) bBytes[i] = (byte)i;

      var w = new Gfs1Writer();
      w.AddFile("a.txt", aBytes);
      w.AddFile("b.bin", bBytes);
      var srcPath = Path.Combine(dir, "src.gfs");
      File.WriteAllBytes(srcPath, w.Build());

      var tarPath = Path.Combine(dir, "dst.tar");
      ArchiveOperations.ConvertArchive(srcPath, tarPath);
      Assert.That(File.Exists(tarPath), Is.True);
      Assert.That(new FileInfo(tarPath).Length, Is.GreaterThan(0));

      var outDir = Path.Combine(dir, "out");
      Directory.CreateDirectory(outDir);
      ArchiveOperations.Extract(tarPath, outDir, null, null);

      string? aPath = null, bPath = null;
      foreach (var p in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)) {
        var name = Path.GetFileName(p);
        if (name == "a.txt") aPath = p;
        else if (name == "b.bin") bPath = p;
      }
      Assert.That(aPath, Is.Not.Null);
      Assert.That(bPath, Is.Not.Null);
      Assert.That(File.ReadAllBytes(aPath!), Is.EqualTo(aBytes));
      Assert.That(File.ReadAllBytes(bPath!), Is.EqualTo(bBytes));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }
}
