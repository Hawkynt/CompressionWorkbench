#pragma warning disable CS1591
using Compression.Lib;
using FileFormat.Tar;

namespace Compression.Tests.Operations.CrossFormat;

/// <summary>
/// Cross-format conversion via <see cref="ArchiveOperations.ConvertArchive"/>:
/// the renamed feature must extract from any source format and rebuild into
/// any creatable target format, including pure archive-to-archive pairs that
/// previously fell outside the FS-image-only scope.
/// </summary>
[TestFixture]
public class ConvertArchiveCrossFormatTests {

  /// <summary>
  /// Builds a ZIP containing two real files, runs <see cref="ArchiveOperations.ConvertArchive"/>
  /// to produce a TAR, then re-reads the TAR and verifies both entries round-trip
  /// byte-for-byte (names and contents).
  /// </summary>
  [Test, Category("CrossFormat")]
  public void ConvertArchive_ZipToTar_PreservesEntryNamesAndContents() {
    FormatRegistration.EnsureInitialized();

    var dir = Path.Combine(Path.GetTempPath(), "cwb_cax_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try {
      // Two source files with distinct, recognizable payloads.
      var aBytes = "hello cross-format world"u8.ToArray();
      var bBytes = new byte[256];
      for (var i = 0; i < bBytes.Length; ++i) bBytes[i] = (byte)i;

      var aPath = Path.Combine(dir, "a.txt");
      var bPath = Path.Combine(dir, "b.bin");
      File.WriteAllBytes(aPath, aBytes);
      File.WriteAllBytes(bPath, bBytes);

      // 1) Build the source ZIP.
      var zipPath = Path.Combine(dir, "src.zip");
      ArchiveOperations.Create(zipPath, [
        new ArchiveInput(aPath, "a.txt"),
        new ArchiveInput(bPath, "b.bin"),
      ], new CompressionOptions());

      Assert.That(File.Exists(zipPath), Is.True, "Sanity: ZIP creation must produce a file.");

      // 2) Convert ZIP -> TAR via ConvertArchive.
      var tarPath = Path.Combine(dir, "dst.tar");
      var warnings = ArchiveOperations.ConvertArchive(zipPath, tarPath);
      Assert.That(File.Exists(tarPath), Is.True, "Conversion must produce the TAR output.");
      Assert.That(new FileInfo(tarPath).Length, Is.GreaterThan(0));

      // 3) Source file must not be deleted.
      Assert.That(File.Exists(zipPath), Is.True, "Source ZIP must survive conversion.");

      // 4) Read the TAR back and collect (name, contents).
      var observed = new Dictionary<string, byte[]>(StringComparer.Ordinal);
      using (var fs = File.OpenRead(tarPath))
      using (var tr = new TarReader(fs)) {
        TarEntry? entry;
        while ((entry = tr.GetNextEntry()) != null) {
          // TAR has its own type taxonomy; only collect regular files.
          if (entry.Size <= 0) continue;
          using var es = tr.GetEntryStream();
          var buf = new byte[entry.Size];
          var read = 0;
          while (read < buf.Length) {
            var n = es.Read(buf, read, buf.Length - read);
            if (n == 0) break;
            read += n;
          }
          // Some ZIP→tempdir→TAR pipelines may add a leading "./" or strip
          // path components depending on the writer's normalisation. Compare
          // by basename to remain robust.
          observed[Path.GetFileName(entry.Name)] = buf;
        }
      }

      Assert.That(observed, Does.ContainKey("a.txt"), $"TAR must contain a.txt; got: [{string.Join(",", observed.Keys)}]");
      Assert.That(observed, Does.ContainKey("b.bin"), $"TAR must contain b.bin; got: [{string.Join(",", observed.Keys)}]");
      Assert.That(observed["a.txt"], Is.EqualTo(aBytes), "a.txt contents must round-trip byte-for-byte.");
      Assert.That(observed["b.bin"], Is.EqualTo(bBytes), "b.bin contents must round-trip byte-for-byte.");
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }
}
