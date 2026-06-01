#pragma warning disable CS1591
using Compression.Lib;
using FileSystem.Efs;

namespace Compression.Tests.Efs;

[TestFixture]
public class EfsConversionTests {

  /// <summary>
  /// Builds an EFS image with two files, converts EFS → TAR, then re-reads
  /// the TAR through our own pipeline and verifies the files round-trip
  /// byte-for-byte.
  /// </summary>
  [Test, Category("Conversion")]
  public void ConvertEfsToTar_AndBack_PreservesContents() {
    FormatRegistration.EnsureInitialized();

    var dir = Path.Combine(Path.GetTempPath(), "cwb_efs_conv_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try {
      var aBytes = "hello efs"u8.ToArray();
      var bBytes = new byte[256];
      for (var i = 0; i < bBytes.Length; ++i) bBytes[i] = (byte)i;

      // Build a real EFS image directly.
      var w = new EfsWriter();
      w.AddFile("a.txt", aBytes);
      w.AddFile("b.bin", bBytes);
      var efsPath = Path.Combine(dir, "src.efs");
      File.WriteAllBytes(efsPath, w.Build());

      // Convert EFS -> TAR.
      var tarPath = Path.Combine(dir, "dst.tar");
      ArchiveOperations.ConvertArchive(efsPath, tarPath);
      Assert.That(File.Exists(tarPath), Is.True);
      Assert.That(new FileInfo(tarPath).Length, Is.GreaterThan(0));

      // Extract the TAR through our own pipeline and compare.
      var outDir = Path.Combine(dir, "out");
      Directory.CreateDirectory(outDir);
      ArchiveOperations.Extract(tarPath, outDir, null, null);

      // Walk recursively for the two files (TAR layout may flatten or wrap).
      string? aPath = null, bPath = null;
      foreach (var p in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)) {
        var name = Path.GetFileName(p);
        if (name == "a.txt") aPath = p;
        else if (name == "b.bin") bPath = p;
      }
      Assert.That(aPath, Is.Not.Null, "a.txt must round-trip through TAR.");
      Assert.That(bPath, Is.Not.Null, "b.bin must round-trip through TAR.");
      Assert.That(File.ReadAllBytes(aPath!), Is.EqualTo(aBytes));
      Assert.That(File.ReadAllBytes(bPath!), Is.EqualTo(bBytes));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }
}
