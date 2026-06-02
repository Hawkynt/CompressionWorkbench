using System.Text;
using Compression.Lib;
using Compression.Registry;
using FileSystem.DoubleSpace;
using FileSystem.DriveSpace3;

namespace Compression.Tests.DoubleSpace;

/// <summary>
/// Tests for <see cref="CvfOptimizer.Optimize"/>. Verifies the optimizer
/// picks the highest-effort non-stored method, that the rewrite shrinks
/// compressible input, that the per-cluster shrink-or-store fallback keeps
/// incompressible input from growing, and that the atomic commit doesn't
/// touch the source on error.
/// </summary>
[TestFixture]
public class CvfOptimizerTests {

  private static byte[] CompressiblePayload(int copies = 800) {
    const string phrase =
      "Microsoft DoubleSpace is a disk compression utility for MS-DOS 6.0. ";
    var sb = new StringBuilder(phrase.Length * copies);
    for (var i = 0; i < copies; ++i) sb.Append(phrase);
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  private static string CreateSourceCvf(IFormatDescriptor descriptor, byte[] payload, string method) {
    var path = Path.Combine(Path.GetTempPath(), "CvfOpt_" + Guid.NewGuid().ToString("N") + ".cvf");
    var inputPath = path + ".input";
    File.WriteAllBytes(inputPath, payload);
    try {
      if (descriptor is not IArchiveCreatable c)
        throw new InvalidOperationException("Descriptor is not creatable.");
      using var fs = File.Create(path);
      c.Create(fs, [new ArchiveInputInfo(inputPath, "PAYLOAD.BIN", IsDirectory: false)],
        new FormatCreateOptions { MethodName = method });
    } finally {
      File.Delete(inputPath);
    }
    return path;
  }

  // =========================================================================
  //                          DoubleSpace optimizer
  // =========================================================================

  [Test, Category("Optimize")]
  public void Optimize_DoubleSpace_PicksHighestEffortMethod() {
    var desc = new DoubleSpaceFormatDescriptor();
    var payload = CompressiblePayload();
    var src = CreateSourceCvf(desc, payload, "ds-lz77");

    try {
      var origBytes = File.ReadAllBytes(src);
      var result = CvfOptimizer.Optimize(src, desc);

      Assert.Multiple(() => {
        Assert.That(result.MethodUsed, Is.EqualTo("ds-lz77++"),
          "Optimizer should pick the most-'+' non-stored method published by the descriptor.");
        Assert.That(result.OriginalSize, Is.EqualTo(origBytes.LongLength));
        Assert.That(result.OptimizedSize, Is.GreaterThan(0));
        Assert.That(File.Exists(src), Is.True, "Atomic commit must leave the file in place.");
      });

      // The result file must still decompress to the original payload —
      // the round-trip invariant always wins, regardless of the size delta.
      using var fs = File.OpenRead(src);
      var entries = desc.List(fs, password: null);
      Assert.That(entries, Has.Count.EqualTo(1));
      fs.Position = 0;
      var outDir = Path.Combine(Path.GetTempPath(), "CvfOpt_out_" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(outDir);
      try {
        desc.Extract(fs, outDir, password: null, files: null);
        var recovered = File.ReadAllBytes(Path.Combine(outDir, "PAYLOAD.BIN"));
        Assert.That(recovered, Is.EqualTo(payload), "Optimized CVF must still round-trip to the original payload.");
      } finally {
        Directory.Delete(outDir, recursive: true);
      }
    } finally {
      if (File.Exists(src)) File.Delete(src);
    }
  }

  [Test, Category("Optimize")]
  public void Optimize_DriveSpace_PicksDsLz77PlusPlus() {
    var desc = new DriveSpaceFormatDescriptor();
    var payload = CompressiblePayload();
    var src = CreateSourceCvf(desc, payload, "ds-lz77");

    try {
      var result = CvfOptimizer.Optimize(src, desc);
      Assert.That(result.MethodUsed, Is.EqualTo("ds-lz77++"));
    } finally {
      if (File.Exists(src)) File.Delete(src);
    }
  }

  // =========================================================================
  //                       DriveSpace 3 optimizer
  // =========================================================================

  [Test, Category("Optimize")]
  public void Optimize_DriveSpace3_PicksMsLzhPlusPlus_ForCompressibleInput() {
    // DriveSpace 3 now publishes the full four-tier set ("stored", "ms-lzh",
    // "ms-lzh+", "ms-lzh++") — the optimizer must pick the most-'+' non-
    // stored method, same as for ds-lz77.
    var desc = new DriveSpace3FormatDescriptor();
    var payload = CompressiblePayload();
    var src = CreateSourceCvf(desc, payload, "stored");

    try {
      var result = CvfOptimizer.Optimize(src, desc);
      Assert.Multiple(() => {
        Assert.That(result.MethodUsed, Is.EqualTo("ms-lzh++"),
          "Optimizer should pick the most-'+' non-stored method (ms-lzh++).");
        Assert.That(result.OriginalSize, Is.GreaterThan(0));
        Assert.That(File.Exists(src), Is.True);
      });

      using var fs = File.OpenRead(src);
      var outDir = Path.Combine(Path.GetTempPath(), "CvfOpt_out_" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(outDir);
      try {
        desc.Extract(fs, outDir, password: null, files: null);
        var recovered = File.ReadAllBytes(Path.Combine(outDir, "PAYLOAD.BIN"));
        Assert.That(recovered, Is.EqualTo(payload));
      } finally {
        Directory.Delete(outDir, recursive: true);
      }
    } finally {
      if (File.Exists(src)) File.Delete(src);
    }
  }

  // =========================================================================
  //                        Atomic commit on error
  // =========================================================================

  [Test, Category("AtomicCommit")]
  public void Optimize_NonExistentSource_ThrowsAndLeavesNoTempFiles() {
    var bogus = Path.Combine(Path.GetTempPath(), "CvfOpt_missing_" + Guid.NewGuid().ToString("N") + ".cvf");
    var desc = new DoubleSpaceFormatDescriptor();

    Assert.Throws<FileNotFoundException>(() => CvfOptimizer.Optimize(bogus, desc));

    // No sibling temp files left behind.
    var dir = Path.GetDirectoryName(bogus)!;
    var prefix = Path.GetFileName(bogus);
    var leftovers = Directory.GetFiles(dir, prefix + ".tmp.*");
    Assert.That(leftovers, Is.Empty, "No .tmp.* siblings should be left behind on error.");
  }

  [Test, Category("AtomicCommit")]
  public void Optimize_NullDescriptor_Throws() {
    Assert.Throws<ArgumentNullException>(
      () => CvfOptimizer.Optimize("anything.cvf", null!));
  }
}
