#pragma warning disable CS1591
using System.Text;
using Compression.Lib;
using FileSystem.Ti99;

namespace Compression.Tests.Ti99;

[TestFixture]
public class Ti99ConversionTests {

  [Test, Category("CrossFormat")]
  public void ConvertArchive_Ti99SectorDumpToTarAndBack_PreservesContents() {
    FormatRegistration.EnsureInitialized();

    var dir = Path.Combine(Path.GetTempPath(), "ti99_conv_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try {
      var alpha = Encoding.ASCII.GetBytes("alpha for TI-99");
      var beta = new byte[400];
      for (var i = 0; i < beta.Length; i++) beta[i] = (byte)((i * 17) & 0xFF);

      var w = new Ti99Writer();
      w.AddFile("ALPHA", alpha);
      w.AddFile("BETA", beta);
      var img = w.BuildSectorDump();

      var ti99Path = Path.Combine(dir, "src.tifd");
      File.WriteAllBytes(ti99Path, img);

      var tarPath = Path.Combine(dir, "out.tar");
      ArchiveOperations.ConvertArchive(ti99Path, tarPath, "Tar");
      Assert.That(new FileInfo(tarPath).Length, Is.GreaterThan(0));

      var ti99BackPath = Path.Combine(dir, "back.tifd");
      ArchiveOperations.ConvertArchive(tarPath, ti99BackPath, "Ti99");

      using var fs = File.OpenRead(ti99BackPath);
      using var r = new Ti99Reader(fs);
      var entries = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
      Assert.That(entries, Does.ContainKey("ALPHA"));
      Assert.That(entries, Does.ContainKey("BETA"));
      var extractedAlpha = r.Extract(entries["ALPHA"]);
      var extractedBeta = r.Extract(entries["BETA"]);
      Assert.That(extractedAlpha.AsSpan(0, alpha.Length).ToArray(), Is.EqualTo(alpha));
      Assert.That(extractedBeta.AsSpan(0, beta.Length).ToArray(), Is.EqualTo(beta));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }
}
