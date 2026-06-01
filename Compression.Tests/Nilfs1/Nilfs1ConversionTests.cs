#pragma warning disable CS1591
using System.Text;
using Compression.Lib;
using FileSystem.Nilfs1;

namespace Compression.Tests.Nilfs1;

[TestFixture]
public class Nilfs1ConversionTests {

  [Test, Category("CrossFormat")]
  public void ConvertArchive_Nilfs1ToTarAndBack_PreservesContents() {
    FormatRegistration.EnsureInitialized();

    var dir = Path.Combine(Path.GetTempPath(), "nilfs1_conv_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try {
      var alpha = Encoding.UTF8.GetBytes("alpha file content for nilfs v1");
      var beta = new byte[800];
      for (var i = 0; i < beta.Length; i++) beta[i] = (byte)((i * 37) & 0xFF);

      var w = new Nilfs1Writer();
      w.AddFile("alpha.txt", alpha);
      w.AddFile("beta.bin", beta);
      var img = w.Build();

      var nilfsPath = Path.Combine(dir, "src.nilfs1");
      File.WriteAllBytes(nilfsPath, img);

      var tarPath = Path.Combine(dir, "out.tar");
      ArchiveOperations.ConvertArchive(nilfsPath, tarPath, "Tar");
      Assert.That(new FileInfo(tarPath).Length, Is.GreaterThan(0));

      var nilfsBackPath = Path.Combine(dir, "back.nilfs1");
      ArchiveOperations.ConvertArchive(tarPath, nilfsBackPath, "Nilfs1");

      using var fs = File.OpenRead(nilfsBackPath);
      using var r = new Nilfs1Reader(fs);
      var entries = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
      Assert.That(entries, Does.ContainKey("alpha.txt"));
      Assert.That(entries, Does.ContainKey("beta.bin"));
      Assert.That(r.Extract(entries["alpha.txt"]), Is.EqualTo(alpha));
      Assert.That(r.Extract(entries["beta.bin"]), Is.EqualTo(beta));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }
}
