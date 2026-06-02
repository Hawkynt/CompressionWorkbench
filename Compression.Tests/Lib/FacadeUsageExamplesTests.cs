using Compression.Lib;
using Compression.Core.Dictionary.Lzh;
using FileFormat.Lzh;

namespace Compression.Tests.Lib;

/// <summary>
/// End-to-end examples — the canonical usage patterns the facade was built
/// to enable. These mirror the snippets in the design doc so a regression in
/// either the reader or the writer surfaces here first.
/// </summary>
[TestFixture]
public class FacadeUsageExamplesTests {

  private static string MakeTempDir() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-fux-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    return dir;
  }

  private static void TryClean(string dir) {
    try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
  }

  [Test, Category("HappyPath")]
  public void Example_ExtractEntryToFile() {
    // Canonical pattern: open an LHA archive, enumerate the files, CopyTo a
    // FileInfo-style target. Asserts bytes match.
    var dir = MakeTempDir();
    try {
      // Build a fixture LHA archive.
      var lhaPath = Path.Combine(dir, "fixture.lzh");
      var fileBytes = new byte[256];
      for (var i = 0; i < fileBytes.Length; ++i) fileBytes[i] = (byte)(i & 0xFF);
      var writer = new LhaWriter(LhaConstants.MethodLh5);
      writer.AddFile("payload.bin", fileBytes);
      File.WriteAllBytes(lhaPath, writer.ToArray());

      // The example pattern: open + enumerate + CopyTo.
      var outputRoot = Path.Combine(dir, "extracted");
      using (var reader = ArchiveReader.Open(lhaPath)) {
        foreach (var entry in reader.Files) {
          var target = new FileInfo(Path.Combine(outputRoot, entry.FileName));
          entry.CopyTo(target.FullName);
        }
      }

      var extractedPath = Path.Combine(outputRoot, "payload.bin");
      Assert.That(File.Exists(extractedPath), Is.True);
      Assert.That(File.ReadAllBytes(extractedPath), Is.EqualTo(fileBytes),
        "extracted bytes must match the source payload");
    } finally { TryClean(dir); }
  }

  [Test, Category("HappyPath")]
  public void Example_BuildArchiveWithDirAndFile() {
    // Canonical pattern: create a ZIP (RAR's create capability exists but the
    // CLI smoke path uses ZIP here because it's the most common write target;
    // RAR's writer is exercised by RarFormatDescriptor's own round-trip tests),
    // MkDir, CreateFileEntry with declared length, write from a source stream.
    // Asserts the resulting archive opens and contains the entry.
    var dir = MakeTempDir();
    try {
      var archive = Path.Combine(dir, "built.zip");
      var src = "Source data to embed in the archive via the facade."u8.ToArray();

      using (var w = ArchiveWriter.Create(archive, "Zip")) {
        w.MkDir("docs/");
        using var sourceStream = new MemoryStream(src);
        using var es = w.CreateFileEntry("docs/note.txt", src.LongLength);
        sourceStream.CopyTo(es);
      }

      // Reopen with the reader facade — verifies the archive is structurally
      // valid and the entry round-trips byte-for-byte.
      using var reader = ArchiveReader.Open(archive);
      Assert.That(reader.FormatId, Is.EqualTo("Zip"));
      var entry = reader.Files.Single(e => e.Name == "docs/note.txt");
      using var read = entry.OpenRead();
      using var sink = new MemoryStream();
      read.CopyTo(sink);
      Assert.That(sink.ToArray(), Is.EqualTo(src));
    } finally { TryClean(dir); }
  }
}
