#pragma warning disable CS1591
using System.ComponentModel;
using System.Diagnostics;
using FileSystem.Refs;

namespace Compression.Tests.Refs;

/// <summary>
/// Optional real-image acceptance gate for the offline ReFS whole-file clone.
/// It intentionally does not download or vendor a ReFS implementation: point
/// CWB_REFS_CLONE_IMAGE at a disposable/reference raw ReFS image and
/// CWB_REFS_FOREFST at an existing forefst.py checkout. The image must contain
/// two distinct, equal-sized, non-empty, cluster-aligned ordinary extent-backed
/// files named by CWB_REFS_CLONE_SOURCE and CWB_REFS_CLONE_DESTINATION.
///
/// The GPL-3.0-or-later forefst tool is used only as an external behavioral
/// oracle; no source or runtime dependency is incorporated into the product.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public sealed class RefsBlockCloneExternalTests {
  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_refs_clone_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, recursive: true); } catch { /* best effort */ }
  }

  [Test, CancelAfter(180_000), Category("HappyPath")]
  public void RealImage_WholeFileClone_IsReadableByForefst() {
    var fixture = Environment.GetEnvironmentVariable("CWB_REFS_CLONE_IMAGE");
    var forefst = Environment.GetEnvironmentVariable("CWB_REFS_FOREFST");
    var source = Environment.GetEnvironmentVariable("CWB_REFS_CLONE_SOURCE") ?? "clone-source.bin";
    var destination = Environment.GetEnvironmentVariable("CWB_REFS_CLONE_DESTINATION") ?? "clone-destination.bin";

    if (string.IsNullOrWhiteSpace(fixture) || !File.Exists(fixture))
      Assert.Ignore("Set CWB_REFS_CLONE_IMAGE to a real raw ReFS image containing the clone source/destination files.");
    if (string.IsNullOrWhiteSpace(forefst) || !File.Exists(forefst))
      Assert.Ignore("Set CWB_REFS_FOREFST to forefst.py for the external ReFS reader oracle.");

    var image = Path.Combine(this._tmpDir, "clone.refs");
    File.Copy(fixture!, image);
    var beforeSource = ExtractWithForefst(forefst!, image, source, Path.Combine(this._tmpDir, "source-before.bin"));

    using (var stream = File.Open(image, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
      RefsOfflineBlockCloner.CloneWholeFile(stream, source, destination);

    var afterSource = ExtractWithForefst(forefst!, image, source, Path.Combine(this._tmpDir, "source-after.bin"));
    var afterDestination = ExtractWithForefst(forefst!, image, destination, Path.Combine(this._tmpDir, "destination-after.bin"));

    Assert.Multiple(() => {
      Assert.That(afterSource, Is.EqualTo(beforeSource), "External reader sees changed source bytes after clone.");
      Assert.That(afterDestination, Is.EqualTo(beforeSource), "External reader does not see destination as a byte-identical block clone.");
    });
  }

  private static byte[] ExtractWithForefst(
      string forefst,
      string image,
      string path,
      string outputPath) {
    var python = Environment.GetEnvironmentVariable("CWB_PYTHON")
      ?? (OperatingSystem.IsWindows() ? "python" : "python3");
    var start = new ProcessStartInfo {
      FileName = python,
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      CreateNoWindow = true,
    };
    start.ArgumentList.Add(forefst);
    start.ArgumentList.Add(image);
    start.ArgumentList.Add("extract");
    start.ArgumentList.Add(path);

    Process process;
    try {
      process = Process.Start(start)
        ?? throw new InvalidOperationException("Could not start the forefst process.");
    } catch (Win32Exception) {
      Assert.Ignore($"Python executable '{python}' is unavailable for the forefst oracle.");
      return [];
    }

    using (process) {
      var stderr = process.StandardError.ReadToEndAsync();
      using (var output = File.Create(outputPath))
        process.StandardOutput.BaseStream.CopyTo(output);
      process.WaitForExit();
      var error = stderr.GetAwaiter().GetResult();
      Assert.That(process.ExitCode, Is.Zero,
        $"forefst could not extract '{path}' from the ReFS image:\n{error}");
    }

    return File.ReadAllBytes(outputPath);
  }
}
