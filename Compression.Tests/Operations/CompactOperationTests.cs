#pragma warning disable CS1591
using Compression.Lib;

namespace Compression.Tests.Operations;

/// <summary>
/// Verifies the composite <c>compact</c> verb (defrag → optimize → shrink) and
/// its <c>--minimal</c> geometry rebuild: contents are preserved byte-for-byte,
/// the standard pass never grows the container, and the minimal pass on a fixed
/// 1.44&#160;MB FAT floppy collapses the image well below the standard pass by
/// re-creating it at minimal geometry (auto-fit size, 512&#160;B clusters,
/// 16-entry root).
/// </summary>
[TestFixture]
public class CompactOperationTests {

  private string _work = "";

  [SetUp]
  public void SetUp() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    _work = Path.Combine(Path.GetTempPath(), "cwb_compact_test_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(_work);
  }

  [TearDown]
  public void TearDown() {
    if (Directory.Exists(_work)) try { Directory.Delete(_work, recursive: true); } catch { /* best effort */ }
  }

  private string MakeSourceFile(string name, byte[] data) {
    var p = Path.Combine(_work, name);
    File.WriteAllBytes(p, data);
    return p;
  }

  /// <summary>Creates a FAT image at <paramref name="imageSize"/> holding <paramref name="files"/>.</summary>
  private string CreateFatImage(string imageSize, params (string Name, byte[] Data)[] files) {
    var imgPath = Path.Combine(_work, "disk_" + Guid.NewGuid().ToString("N")[..6] + ".img");
    var inputs = files
      .Select(f => new ArchiveInput(MakeSourceFile(f.Name, f.Data), f.Name))
      .ToList();
    ArchiveOperations.Create(imgPath, inputs,
      new CompressionOptions(),
      FormatDetector.Format.Fat,
      new Dictionary<string, string> { ["ImageSize"] = imageSize });
    return imgPath;
  }

  private static Dictionary<string, byte[]> ReadAll(string path) {
    var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_read_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tempDir);
      ArchiveOperations.Extract(path, tempDir, password: null, files: null);
      foreach (var f in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tempDir, f).Replace('\\', '/');
        map[rel] = File.ReadAllBytes(f);
      }
    } finally {
      if (Directory.Exists(tempDir)) try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
    }
    return map;
  }

  [Test]
  public void Compact_Standard_PreservesContents_AndDoesNotGrow() {
    var payload = "the quick brown fox jumps over the lazy dog\n"u8.ToArray();
    var img = CreateFatImage("1.44 MB (3.5\" HD)", ("README.TXT", payload));
    var before = new FileInfo(img).Length;

    var result = CompactOperation.Compact(img, new CompactOperation.CompactOptions { Minimal = false });

    Assert.That(result.NewSize, Is.LessThanOrEqualTo(before), "standard compact must never grow the image");
    var got = ReadAll(img);
    Assert.That(got["README.TXT"], Is.EqualTo(payload), "file content must survive compaction byte-for-byte");
  }

  [Test]
  public void Compact_Minimal_OnFloppy_ShrinksFarBelowStandard_AndKeepsContents() {
    var payload = new byte[4096];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7);

    // Two identical 1.44 MB floppies with the same small payload.
    var stdImg = CreateFatImage("1.44 MB (3.5\" HD)", ("DATA.BIN", payload));
    var minImg = CreateFatImage("1.44 MB (3.5\" HD)", ("DATA.BIN", payload));
    var floppySize = new FileInfo(stdImg).Length; // ~1.44 MB

    var stdResult = CompactOperation.Compact(stdImg, new CompactOperation.CompactOptions { Minimal = false });
    var minResult = CompactOperation.Compact(minImg, new CompactOperation.CompactOptions { Minimal = true });

    Assert.Multiple(() => {
      Assert.That(minResult.Minimal, Is.True);
      Assert.That(minResult.NewSize, Is.LessThan(floppySize), "minimal must shrink the fixed floppy");
      Assert.That(minResult.NewSize, Is.LessThan(stdResult.NewSize),
        "minimal-geometry rebuild must beat the standard (geometry-preserving) compact");
      // Tight FAT12 geometry: a 4 KB payload should land well under 32 KB — the
      // image is essentially [reserved + 2 small FATs + 16-entry root + data].
      Assert.That(minResult.NewSize, Is.LessThan(32 * 1024),
        "minimal FAT geometry must be a few KB, not the writer's default headroom");
    });

    // Contents survive the geometry rewrite, and the result is still a valid FAT.
    var got = ReadAll(minImg);
    Assert.That(got["DATA.BIN"], Is.EqualTo(payload));
  }

  [Test]
  public void Compact_Zip_PreservesEntries() {
    var a = new byte[2000];
    Array.Fill(a, (byte)0x5A);
    var b = "compact me"u8.ToArray();
    var zipPath = Path.Combine(_work, "bundle.zip");
    ArchiveOperations.Create(zipPath,
      [new ArchiveInput(MakeSourceFile("a.bin", a), "a.bin"),
       new ArchiveInput(MakeSourceFile("b.txt", b), "b.txt")],
      new CompressionOptions());

    var before = new FileInfo(zipPath).Length;
    var result = CompactOperation.Compact(zipPath, new CompactOperation.CompactOptions { Minimal = false });

    Assert.That(result.NewSize, Is.LessThanOrEqualTo(before));
    var got = ReadAll(zipPath);
    Assert.That(got["a.bin"], Is.EqualTo(a));
    Assert.That(got["b.txt"], Is.EqualTo(b));
  }
}
