using FileFormat.SplitFile;

namespace Compression.Tests.SplitFile;

[TestFixture]
public class SplitFileTests {

  [Test, Category("HappyPath")]
  public void Reader_JoinsPartsInOrder() {
    var part1 = new MemoryStream("Hello, "u8.ToArray());
    var part2 = new MemoryStream("World!"u8.ToArray());

    var r = new SplitFileReader("test.bin", [part1, part2]);
    var joined = r.Extract();
    Assert.That(joined, Is.EqualTo("Hello, World!"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Reader_EntryReportsCorrectSize() {
    var part1 = new MemoryStream(new byte[100]);
    var part2 = new MemoryStream(new byte[200]);
    var part3 = new MemoryStream(new byte[50]);

    var r = new SplitFileReader("data.bin", [part1, part2, part3]);
    Assert.That(r.Entry.Size, Is.EqualTo(350));
    Assert.That(r.Entry.PartCount, Is.EqualTo(3));
    Assert.That(r.Entry.Name, Is.EqualTo("data.bin"));
  }

  [Test, Category("HappyPath")]
  public void Reader_SinglePart() {
    var data = "Single part"u8.ToArray();
    var r = new SplitFileReader("file.txt", [new MemoryStream(data)]);
    Assert.That(r.Extract(), Is.EqualTo(data));
    Assert.That(r.Entry.PartCount, Is.EqualTo(1));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_SplitsAndRejoins() {
    var original = new byte[1000];
    Random.Shared.NextBytes(original);
    var tempDir = Path.Combine(Path.GetTempPath(), "splitfile_test_" + Guid.NewGuid().ToString("N"));

    try {
      using var input = new MemoryStream(original);
      var partCount = SplitFileWriter.Split(input, tempDir, "data.bin", 400);
      Assert.That(partCount, Is.EqualTo(3)); // 400+400+200

      // Verify files exist
      Assert.That(File.Exists(Path.Combine(tempDir, "data.bin.001")), Is.True);
      Assert.That(File.Exists(Path.Combine(tempDir, "data.bin.002")), Is.True);
      Assert.That(File.Exists(Path.Combine(tempDir, "data.bin.003")), Is.True);

      // Rejoin via reader
      var reader = new SplitFileReader(Path.Combine(tempDir, "data.bin.001"));
      Assert.That(reader.Entry.PartCount, Is.EqualTo(3));
      Assert.That(reader.Extract(), Is.EqualTo(original));
    } finally {
      if (Directory.Exists(tempDir))
        Directory.Delete(tempDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Writer_PartSizeExact() {
    var data = new byte[300];
    var tempDir = Path.Combine(Path.GetTempPath(), "splitfile_exact_" + Guid.NewGuid().ToString("N"));

    try {
      using var input = new MemoryStream(data);
      var count = SplitFileWriter.Split(input, tempDir, "exact", 100);
      Assert.That(count, Is.EqualTo(3));
      Assert.That(new FileInfo(Path.Combine(tempDir, "exact.001")).Length, Is.EqualTo(100));
      Assert.That(new FileInfo(Path.Combine(tempDir, "exact.002")).Length, Is.EqualTo(100));
      Assert.That(new FileInfo(Path.Combine(tempDir, "exact.003")).Length, Is.EqualTo(100));
    } finally {
      if (Directory.Exists(tempDir))
        Directory.Delete(tempDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void ExtractTo_WritesToStream() {
    var data1 = new byte[] { 1, 2, 3 };
    var data2 = new byte[] { 4, 5, 6 };
    var r = new SplitFileReader("out.bin", [new MemoryStream(data1), new MemoryStream(data2)]);

    using var output = new MemoryStream();
    r.ExtractTo(output);
    Assert.That(output.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6 }));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_FilePath_NotFound_Throws() {
    Assert.Throws<FileNotFoundException>(() =>
      _ = new SplitFileReader("/nonexistent/path/file.001"));
  }

  [Test, Category("ErrorHandling")]
  public void Writer_ZeroPartSize_Throws() {
    using var ms = new MemoryStream([1]);
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      SplitFileWriter.Split(ms, Path.GetTempPath(), "x", 0));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new SplitFileFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("SplitFile"));
    Assert.That(desc.Extensions, Does.Contain(".001"));
  }

  [Test, Category("HappyPath")]
  public void Detect_ByExtension() {
    var format = Compression.Lib.FormatDetector.DetectByExtension("archive.001");
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.SplitFile));
  }
}
