using Compression.Lib;
using FileFormat.Zip;

namespace Compression.Tests.Lib;

/// <summary>
/// Validates the high-level <see cref="ArchiveWriter"/> facade.
/// </summary>
[TestFixture]
public class ArchiveWriterFacadeTests {

  private static string MakeTempDir() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-wf-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    return dir;
  }

  private static void TryClean(string dir) {
    try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
  }

  [Test, Category("HappyPath")]
  public void Writer_CreateZipArchive_FromStreamsRoundTrips() {
    var dir = MakeTempDir();
    try {
      var archive = Path.Combine(dir, "out.zip");
      var payload = "Hello from facade-built archive."u8.ToArray();

      using (var w = ArchiveWriter.Create(archive, "Zip")) {
        using (var es = w.CreateFileEntry("docs/note.txt", payload.LongLength)) {
          es.Write(payload, 0, payload.Length);
        }
      }

      Assert.That(File.Exists(archive), Is.True, "writer must commit atomically on dispose");

      // Round-trip: open with the reader facade and verify.
      using var reader = ArchiveReader.Open(archive);
      var entry = reader.Files.Single();
      Assert.That(entry.Name, Is.EqualTo("docs/note.txt"));
      using var s = entry.OpenRead();
      using var sink = new MemoryStream();
      s.CopyTo(sink);
      Assert.That(sink.ToArray(), Is.EqualTo(payload));
    } finally { TryClean(dir); }
  }

  [Test, Category("ErrorHandling")]
  public void Writer_OverrunDeclaredLength_Throws() {
    var dir = MakeTempDir();
    try {
      var archive = Path.Combine(dir, "out.zip");
      using var w = ArchiveWriter.Create(archive, "Zip");
      using var es = w.CreateFileEntry("a.bin", 4);
      es.Write(new byte[] { 1, 2, 3, 4 }, 0, 4);
      Assert.Throws<InvalidOperationException>(() => es.Write(new byte[] { 5 }, 0, 1),
        "writing past declared length must throw");
      w.Cancel();
    } finally { TryClean(dir); }
  }

  [Test, Category("ErrorHandling")]
  public void Writer_UnderrunDeclaredLength_ThrowsOnDispose() {
    var dir = MakeTempDir();
    try {
      var archive = Path.Combine(dir, "out.zip");
      ArchiveWriter? writer = null;
      try {
        writer = ArchiveWriter.Create(archive, "Zip");
        var es = writer.CreateFileEntry("a.bin", 16);
        es.Write(new byte[] { 1, 2, 3, 4 }, 0, 4); // underrun
        Assert.Throws<InvalidOperationException>(() => es.Dispose());
      } finally {
        writer?.Cancel();
        writer?.Dispose();
      }
    } finally { TryClean(dir); }
  }

  [Test, Category("HappyPath")]
  public void Writer_MkDir_ProducesDirectoryEntry() {
    var dir = MakeTempDir();
    try {
      var archive = Path.Combine(dir, "out.zip");
      using (var w = ArchiveWriter.Create(archive, "Zip")) {
        w.MkDir("docs/");
        using var es = w.CreateFileEntry("docs/file.txt", 3);
        es.Write(new byte[] { (byte)'a', (byte)'b', (byte)'c' }, 0, 3);
      }

      using var reader = ArchiveReader.Open(archive);
      Assert.That(reader.Directories.Any(d => d.Name.TrimEnd('/').Equals("docs", StringComparison.OrdinalIgnoreCase)),
        Is.True, "directory entry must round-trip through the writer");
    } finally { TryClean(dir); }
  }

  [Test, Category("Spec")]
  public void Writer_AtomicCommit_NoTornOutputOnCrash() {
    // Simulate a caller-side exception mid-build (before dispose); verify the
    // target path is never created — the temp file path is dropped instead.
    var dir = MakeTempDir();
    try {
      var archive = Path.Combine(dir, "out.zip");
      try {
        using var w = ArchiveWriter.Create(archive, "Zip");
        using (var es = w.CreateFileEntry("a.bin", 4)) {
          es.Write(new byte[] { 1, 2, 3, 4 }, 0, 4);
        }
        // Simulate caller failure here — Cancel + throw before dispose
        w.Cancel();
        throw new InvalidOperationException("simulated caller failure");
      } catch (InvalidOperationException) {
        // expected
      }

      Assert.That(File.Exists(archive), Is.False,
        "target must not exist when writer is cancelled mid-build");

      // No orphan temp files in the dir (other than the intentional 'archive' name
      // which we already verified is absent).
      var leftovers = Directory.GetFiles(dir);
      Assert.That(leftovers, Is.Empty, "no torn temp files must be left behind");
    } finally { TryClean(dir); }
  }

  [Test, Category("HappyPath")]
  public void Writer_ZeroLengthEntry_RoundTrips() {
    var dir = MakeTempDir();
    try {
      var archive = Path.Combine(dir, "out.zip");
      using (var w = ArchiveWriter.Create(archive, "Zip")) {
        using var es = w.CreateFileEntry("empty.txt", 0);
        // Write nothing — the bounded stream allows zero-length entries.
      }

      using var reader = ArchiveReader.Open(archive);
      var e = reader.Files.Single();
      Assert.That(e.Size, Is.EqualTo(0));
    } finally { TryClean(dir); }
  }
}
