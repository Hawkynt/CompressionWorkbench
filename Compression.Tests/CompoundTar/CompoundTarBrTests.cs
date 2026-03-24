namespace Compression.Tests.CompoundTar;

[TestFixture]
public class CompoundTarBrTests {

  [Test, Category("RoundTrip"), Category("End2End")]
  public void TarBr_RoundTrip() {
    var data = "TarBr compound format test content for round-trip verification."u8.ToArray();
    var dir = Path.Combine(Path.GetTempPath(), "cwb_test_tarbr_" + Guid.NewGuid().ToString("N")[..8]);
    var archive = Path.Combine(dir, "test.tar.br");
    var extractDir = Path.Combine(dir, "out");
    try {
      Directory.CreateDirectory(dir);
      var inputFile = Path.Combine(dir, "hello.txt");
      File.WriteAllBytes(inputFile, data);
      var inputs = new List<Compression.Lib.ArchiveInput> { new(inputFile, "hello.txt") };
      Compression.Lib.ArchiveOperations.Create(archive, inputs, new Compression.Lib.CompressionOptions());
      Assert.That(File.Exists(archive), Is.True);

      var entries = Compression.Lib.ArchiveOperations.List(archive, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("hello.txt"));

      Compression.Lib.ArchiveOperations.Extract(archive, extractDir, null, null);
      var extracted = File.ReadAllBytes(Path.Combine(extractDir, "hello.txt"));
      Assert.That(extracted, Is.EqualTo(data));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void TarBr_DetectByExtension_TarBr() {
    var format = Compression.Lib.FormatDetector.DetectByExtension("test.tar.br");
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.TarBr));
  }

  [Test, Category("HappyPath")]
  public void TarBr_DetectByExtension_Tbr() {
    var format = Compression.Lib.FormatDetector.DetectByExtension("test.tbr");
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.TarBr));
  }
}
