#pragma warning disable CS1591
using System.Text;
using Compression.Lib;
using FileSystem.ApplePascal;

namespace Compression.Tests.ApplePascal;

[TestFixture]
public class ApplePascalConversionTests {

  [Test, Category("CrossFormat")]
  public void ConvertArchive_ApplePascalToTarAndBack_PreservesContents() {
    FormatRegistration.EnsureInitialized();

    var dir = Path.Combine(Path.GetTempPath(), "applepascal_conv_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try {
      var alpha = Encoding.ASCII.GetBytes("alpha content");
      var beta = Encoding.ASCII.GetBytes(new string('B', 400));

      var w = new ApplePascalWriter();
      w.AddFile("ALPHA.TXT", alpha);
      w.AddFile("BETA.DAT", beta);
      var img = w.Build();

      var pvolPath = Path.Combine(dir, "src.pvol");
      File.WriteAllBytes(pvolPath, img);

      var tarPath = Path.Combine(dir, "out.tar");
      ArchiveOperations.ConvertArchive(pvolPath, tarPath, "Tar");
      Assert.That(new FileInfo(tarPath).Length, Is.GreaterThan(0));

      var pvolBackPath = Path.Combine(dir, "back.pvol");
      ArchiveOperations.ConvertArchive(tarPath, pvolBackPath, "ApplePascal");

      using var fs = File.OpenRead(pvolBackPath);
      using var r = new ApplePascalReader(fs);
      var entries = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
      Assert.That(entries, Does.ContainKey("ALPHA.TXT"));
      Assert.That(entries, Does.ContainKey("BETA.DAT"));
      var extractedAlpha = r.Extract(entries["ALPHA.TXT"]);
      var extractedBeta = r.Extract(entries["BETA.DAT"]);
      Assert.That(extractedAlpha.AsSpan(0, alpha.Length).ToArray(), Is.EqualTo(alpha));
      Assert.That(extractedBeta.AsSpan(0, beta.Length).ToArray(), Is.EqualTo(beta));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }
}
