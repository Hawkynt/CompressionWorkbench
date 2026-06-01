#pragma warning disable CS1591
using Compression.Lib;
using FileSystem.Trsdos;

namespace Compression.Tests.Trsdos;

/// <summary>
/// End-to-end conversion via
/// <see cref="ArchiveOperations.ConvertArchive(string, string, string?, Compression.Registry.FormatCreateOptions?)"/>:
/// build a TRSDOS image, convert to TAR, convert back, verify byte-identical contents.
/// </summary>
[TestFixture]
public class TrsdosConversionTests {

  [Test, Category("CrossFormat")]
  public void ConvertArchive_TrsdosToTar_PreservesContents() {
    FormatRegistration.EnsureInitialized();

    var dir = Path.Combine(Path.GetTempPath(), "trsdos_conv_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try {
      var fileA = "alpha file content"u8.ToArray();
      var fileB = new byte[200];
      for (var i = 0; i < fileB.Length; i++) fileB[i] = (byte)(i & 0xFF);

      var w = new TrsdosWriter();
      w.SetGeometry(40, 18);
      w.AddFile("A.TXT", fileA);
      w.AddFile("B.BIN", fileB);
      var img = w.Build();

      var trsdosPath = Path.Combine(dir, "src.trsdos");
      File.WriteAllBytes(trsdosPath, img);

      // Convert .trsdos -> .tar.
      var tarPath = Path.Combine(dir, "out.tar");
      ArchiveOperations.ConvertArchive(trsdosPath, tarPath, "Tar");
      Assert.That(File.Exists(tarPath), Is.True);
      Assert.That(new FileInfo(tarPath).Length, Is.GreaterThan(0));

      // Convert .tar -> .trsdos.
      var trsdosBackPath = Path.Combine(dir, "back.trsdos");
      ArchiveOperations.ConvertArchive(tarPath, trsdosBackPath, "Trsdos");
      Assert.That(File.Exists(trsdosBackPath), Is.True);

      using var fs = File.OpenRead(trsdosBackPath);
      using var r = new TrsdosReader(fs);
      var names = r.Entries.Select(e => e.Name).ToList();
      Assert.That(names, Does.Contain("A.TXT"));
      Assert.That(names, Does.Contain("B.BIN"));

      var entryA = r.Entries.Single(e => e.Name == "A.TXT");
      var entryB = r.Entries.Single(e => e.Name == "B.BIN");
      var extractedA = r.Extract(entryA);
      var extractedB = r.Extract(entryB);
      Assert.That(extractedA.AsSpan(0, fileA.Length).ToArray(), Is.EqualTo(fileA),
        "A.TXT must round-trip byte-for-byte.");
      Assert.That(extractedB.AsSpan(0, fileB.Length).ToArray(), Is.EqualTo(fileB),
        "B.BIN must round-trip byte-for-byte.");
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }
}
