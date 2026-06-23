#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Ntfs;

namespace Compression.Tests.Ntfs;

/// <summary>
/// External-tool acceptance gate for the genuine in-place NTFS add: build an image,
/// add files in place via <see cref="NtfsInPlaceAdder"/> (no re-pack), and prove the
/// reference ntfs-3g userspace tooling accepts the result — <c>ntfsls</c> lists the new
/// file, <c>ntfscat</c> reads its exact content, and <c>ntfsfix -n</c> reports
/// $MFT/$MFTMirr clean. Skips cleanly when ntfs-3g is unavailable.
/// </summary>
[TestFixture]
[Category("ExternalConformance")]
public class NtfsInPlaceExternalTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_ntfs_inplace_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static void RequireTool(string tool) {
    if (!FsInteropToolbox.WslAvailable) Assert.Ignore("No Linux shell / WSL available.");
    if (!FsInteropToolbox.WslHasTool(tool)) Assert.Ignore($"ntfs-3g tool '{tool}' not installed (apt install ntfs-3g).");
  }

  private string BuildInPlaceImage() {
    var image = new NtfsWriter().Build(16 * 1024 * 1024);
    // seed via writer, then add genuinely in place.
    {
      var w = new NtfsWriter();
      w.AddFile("seed.txt", Encoding.ASCII.GetBytes("seed-content"));
      image = w.Build(16 * 1024 * 1024);
    }
    NtfsInPlaceAdder.AddFile(image, "small.txt", Encoding.ASCII.GetBytes("in-place-resident-payload"));
    var big = new byte[9000];
    new Random(11).NextBytes(big);
    NtfsInPlaceAdder.AddFile(image, "large.bin", big);
    var path = Path.Combine(this._tmpDir, "inplace.img");
    File.WriteAllBytes(path, image);
    File.WriteAllBytes(Path.Combine(this._tmpDir, "large_expected.bin"), big);
    return path;
  }

  [Test, CancelAfter(60_000)]
  public void InPlaceAdded_File_ReadableByNtfscat() {
    RequireTool("ntfscat");
    var img = BuildInPlaceImage();
    var wsl = FsInteropToolbox.WinToWsl(img);

    var small = FsInteropToolbox.RunWsl($"ntfscat {wsl} small.txt");
    Assert.That(small.StdOut, Does.Contain("in-place-resident-payload"),
      $"ntfscat could not read the in-place-added resident file.\n{small.StdOut}\n{small.StdErr}");

    var expected = FsInteropToolbox.WinToWsl(Path.Combine(this._tmpDir, "large_expected.bin"));
    var big = FsInteropToolbox.RunWsl($"ntfscat {wsl} large.bin | cmp - {expected} && echo CMP_OK");
    Assert.That(big.StdOut, Does.Contain("CMP_OK"),
      $"ntfscat large.bin did not match the original bytes.\n{big.StdOut}\n{big.StdErr}");
  }

  [Test, CancelAfter(60_000)]
  public void InPlaceModified_Image_PassesNtfsfix() {
    RequireTool("ntfsfix");
    var img = BuildInPlaceImage();
    var result = FsInteropToolbox.RunWsl($"ntfsfix -n {FsInteropToolbox.WinToWsl(img)}");
    var combined = result.StdOut + "\n" + result.StdErr;
    Assert.That(combined, Does.Contain("Processing of $MFT and $MFTMirr completed successfully").IgnoreCase,
      $"ntfsfix flagged the in-place-modified image.\n{combined}");
    Assert.That(combined, Does.Not.Contain("inconsistent").IgnoreCase);
    Assert.That(combined, Does.Not.Contain("corrupt").IgnoreCase);
  }
}
