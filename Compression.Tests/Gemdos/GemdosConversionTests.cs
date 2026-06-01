#pragma warning disable CS1591
using System.Text;
using Compression.Lib;
using FileSystem.Gemdos;

namespace Compression.Tests.Gemdos;

[TestFixture]
public class GemdosConversionTests {

  [Test, Category("CrossFormat")]
  public void ConvertArchive_GemdosToTarAndBack_PreservesContents() {
    FormatRegistration.EnsureInitialized();

    var dir = Path.Combine(Path.GetTempPath(), "gemdos_conv_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try {
      var alpha = Encoding.ASCII.GetBytes("alpha file content for atari");
      var beta = new byte[600];
      for (var i = 0; i < beta.Length; i++) beta[i] = (byte)(i & 0xFF);

      var w = new GemdosWriter();
      w.AddFile("ALPHA.TXT", alpha);
      w.AddFile("BETA.BIN", beta);
      var img = w.Build();

      var stPath = Path.Combine(dir, "src.st");
      File.WriteAllBytes(stPath, img);

      var tarPath = Path.Combine(dir, "out.tar");
      ArchiveOperations.ConvertArchive(stPath, tarPath, "Tar");
      Assert.That(new FileInfo(tarPath).Length, Is.GreaterThan(0));

      var stBackPath = Path.Combine(dir, "back.st");
      ArchiveOperations.ConvertArchive(tarPath, stBackPath, "Gemdos");

      using var fs = File.OpenRead(stBackPath);
      using var r = new GemdosReader(fs);
      var entries = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
      Assert.That(entries, Does.ContainKey("ALPHA.TXT"));
      Assert.That(entries, Does.ContainKey("BETA.BIN"));
      Assert.That(r.Extract(entries["ALPHA.TXT"]), Is.EqualTo(alpha));
      Assert.That(r.Extract(entries["BETA.BIN"]), Is.EqualTo(beta));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }
}
