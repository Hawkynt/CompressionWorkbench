using Compression.Lib;
using Compression.Core.Dictionary.Lzh;
using FileFormat.Lzh;
using FileFormat.Zip;

namespace Compression.Tests.Lib;

/// <summary>
/// Validates the high-level <see cref="ArchiveReader"/> facade.
/// </summary>
[TestFixture]
public class ArchiveReaderFacadeTests {

  private static string MakeTempDir() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-rf-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    return dir;
  }

  private static void TryClean(string dir) {
    try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
  }

  private static string CreateZipFixture(string dir) {
    var path = Path.Combine(dir, "fixture.zip");
    using var fs = File.Create(path);
    using (var w = new ZipWriter(fs, leaveOpen: true)) {
      w.AddDirectory("docs/");
      w.AddEntry("docs/readme.txt", "Hello, ZIP world!"u8.ToArray());
      w.AddEntry("docs/data.bin", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x10, 0x20, 0x30 });
      w.AddEntry("top.txt", "top-level"u8.ToArray());
      w.Finish();
    }
    return path;
  }

  private static string CreateLzhFixture(string dir) {
    var path = Path.Combine(dir, "fixture.lzh");
    var writer = new LhaWriter(LhaConstants.MethodLh0);
    writer.AddFile("alpha.txt", "alpha contents"u8.ToArray());
    writer.AddFile("beta.bin", new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });
    File.WriteAllBytes(path, writer.ToArray());
    return path;
  }

  [Test, Category("HappyPath")]
  public void Reader_LzhArchive_EnumeratesFilesAndDirectories() {
    var dir = MakeTempDir();
    try {
      var archive = CreateLzhFixture(dir);
      using var reader = ArchiveReader.Open(archive);

      Assert.That(reader.FormatId, Is.EqualTo("Lzh"));
      Assert.That(reader.Entries, Has.Count.EqualTo(2));
      Assert.That(reader.Files.Count(), Is.EqualTo(2));
      Assert.That(reader.Files.Select(e => e.FileName), Is.EquivalentTo(new[] { "alpha.txt", "beta.bin" }));
    } finally { TryClean(dir); }
  }

  [Test, Category("HappyPath")]
  public void Reader_ZipEntry_OpenRead_CopiesBytes() {
    var dir = MakeTempDir();
    try {
      var archive = CreateZipFixture(dir);
      using var reader = ArchiveReader.Open(archive);

      var entry = reader.Files.First(e => e.FileName == "readme.txt");
      Assert.That(entry.Directory, Is.EqualTo("docs"));
      Assert.That(entry.Name, Is.EqualTo("docs/readme.txt"));

      using var s = entry.OpenRead();
      using var sink = new MemoryStream();
      s.CopyTo(sink);
      Assert.That(sink.ToArray(), Is.EqualTo("Hello, ZIP world!"u8.ToArray()));
    } finally { TryClean(dir); }
  }

  [Test, Category("HappyPath")]
  public void Reader_TwoConcurrentOpenRead_BothSucceed() {
    // Each OpenRead opens its own File.OpenRead handle, so two streams over
    // distinct entries must work in parallel without coordinating cursors.
    var dir = MakeTempDir();
    try {
      var archive = CreateZipFixture(dir);
      using var reader = ArchiveReader.Open(archive);
      var a = reader.Files.First(e => e.FileName == "readme.txt");
      var b = reader.Files.First(e => e.FileName == "data.bin");

      using var sa = a.OpenRead();
      using var sb = b.OpenRead();
      var bufA = new MemoryStream(); sa.CopyTo(bufA);
      var bufB = new MemoryStream(); sb.CopyTo(bufB);

      Assert.That(bufA.ToArray(), Is.EqualTo("Hello, ZIP world!"u8.ToArray()));
      Assert.That(bufB.ToArray(), Is.EqualTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x10, 0x20, 0x30 }));
    } finally { TryClean(dir); }
  }

  [Test, Category("Spec")]
  public void Reader_OpenRead_BoundedToEntrySize() {
    var dir = MakeTempDir();
    try {
      var archive = CreateZipFixture(dir);
      using var reader = ArchiveReader.Open(archive);
      var entry = reader.Files.First(e => e.FileName == "data.bin");

      using var s = entry.OpenRead();
      // Read exactly the entry's bytes...
      var buf = new byte[entry.Size + 8];
      var consumed = 0;
      int n;
      while ((n = s.Read(buf, consumed, buf.Length - consumed)) > 0) {
        consumed += n;
      }
      Assert.That(consumed, Is.EqualTo(entry.Size));

      // Read past Size returns 0 (EOF).
      Assert.That(s.Read(new byte[16], 0, 16), Is.EqualTo(0));
    } finally { TryClean(dir); }
  }

  [Test, Category("HappyPath")]
  public void Reader_CopyToFile_WritesEntryBytes() {
    var dir = MakeTempDir();
    try {
      var archive = CreateZipFixture(dir);
      using var reader = ArchiveReader.Open(archive);
      var entry = reader.Files.First(e => e.FileName == "readme.txt");

      var target = Path.Combine(dir, "extracted", "readme.txt");
      entry.CopyTo(target);

      Assert.That(File.Exists(target), Is.True);
      Assert.That(File.ReadAllBytes(target), Is.EqualTo("Hello, ZIP world!"u8.ToArray()));
    } finally { TryClean(dir); }
  }
}
