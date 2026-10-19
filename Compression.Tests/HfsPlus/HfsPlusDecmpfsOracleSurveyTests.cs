using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// Which independent HFS+ readers decode which decmpfs methods of our writer's volumes. libfshfs
/// (<see cref="HfsPlusLibfshfsOracleTests"/>) decodes 3/4, 7/8, 9/10, 11/12 and 5. 7-Zip 26.03
/// decodes zlib (3/4) and LZVN (7/8); HFSExplorer's unhfs decodes zlib. None of them decodes
/// method 1 (uncompressed bytes in the attribute) or LZBITMAP (13/14), and neither do hfsfuse
/// (types 3/4, 5, 7/8, 11/12 in its source) or Apple's open-source fsck_hfs, which checks no
/// decmpfs content. These tests record that absence instead of claiming an oracle: a reader that
/// learns a method makes its test fail, which is the signal to turn it into a byte-for-byte check.
/// Files our writer stores uncompressed (noise does not shrink) are read by every tool and keep
/// the comparison honest.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class HfsPlusDecmpfsOracleSurveyTests {

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb_hfsdecmpfs_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  /// <summary>Every file 7-Zip extracts from the volume, by path below the folder it names after the volume.</summary>
  private Dictionary<string, byte[]> SevenZip(byte[] image) {
    FsInteropToolbox.Require7z();
    var path = Path.Combine(this._tmp, $"{Guid.NewGuid():N}.hfs");
    var output = Path.Combine(this._tmp, $"7z-{Guid.NewGuid():N}");
    File.WriteAllBytes(path, image);
    FsInteropToolbox.Run7z($"x -y \"-o{output}\" \"{path}\"");
    return Directory.Exists(output)
      ? Directory.GetFiles(output, "*", SearchOption.AllDirectories).ToDictionary(
          f => string.Join('/', Path.GetRelativePath(output, f).Replace('\\', '/').Split('/').Skip(1)), File.ReadAllBytes)
      : [];
  }

  private Dictionary<string, byte[]> Unhfs(byte[] image) => HfsPlusOracles.Unhfs(image, this._tmp);

  private static void AssertDecodes(Dictionary<string, byte[]> extracted, string tool, params string[] except) {
    Assert.Multiple(() => {
      foreach (var (name, content) in HfsPlusDecmpfsMethodsTests.Files().Where(f => f.Content.Length > 0 && !except.Contains(f.Name)))
        Assert.That(extracted.GetValueOrDefault(name), Is.EqualTo(content), $"{tool}: {name}");
    });
  }

  private static void AssertDoesNotDecode(Dictionary<string, byte[]> extracted, string tool) {
    var compressed = HfsPlusDecmpfsMethodsTests.Files().Single(static f => f.Name == "inline.txt").Content;
    var stored = HfsPlusDecmpfsMethodsTests.Files().Single(static f => f.Name == "noise.bin").Content;
    Assert.Multiple(() => {
      Assert.That(extracted.GetValueOrDefault("noise.bin"), Is.EqualTo(stored), $"{tool} reads what is stored uncompressed");
      Assert.That(extracted.GetValueOrDefault("inline.txt"), Is.Not.EqualTo(compressed),
        $"{tool} now decodes this method: turn this test into a byte-for-byte oracle comparison");
    });
  }

  [TestCase(HfsPlusCompression.Zlib, TestName = "GivenOurZlibVolume_When7ZipExtractsIt_ThenEveryFileIsItsContent")]
  [TestCase(HfsPlusCompression.Lzvn, TestName = "GivenOurLzvnVolume_When7ZipExtractsIt_ThenEveryFileIsItsContent")]
  [Category("HappyPath")]
  public void SevenZipDecodes(HfsPlusCompression compression) {
    FsInteropToolbox.Require7z();
    // Measured with 7-Zip 26.03; older builds (the p7zip 16.02 distributions ship) lack LZVN.
    var banner = FsInteropToolbox.Run7z("").StdOut;
    var version = System.Text.RegularExpressions.Regex.Match(banner, @"7-Zip (?:\(a\) )?(\d+)\.(\d+)");
    if (!version.Success || int.Parse(version.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) < 26)
      Assert.Ignore($"7-Zip 26 or later is needed for this comparison; found: {banner.Split('\n').FirstOrDefault(static l => l.Contains("7-Zip", StringComparison.Ordinal))?.Trim()}");
    AssertDecodes(this.SevenZip(HfsPlusDecmpfsMethodsTests.CompressedVolume(compression)), "7-Zip");
  }

  /// <summary>
  /// unhfs 2026.4.9 extracts nothing for "chunk-plus-one.bin", whose one-byte last chunk does not
  /// shrink and is stored behind the 0xFF marker, which libfshfs and 7-Zip both read; unhfs counts
  /// the marker byte into the stored chunk's length. That file is left out of this comparison
  /// rather than worked around in the writer.
  /// </summary>
  [Test, Category("HappyPath")]
  public void GivenOurZlibVolume_WhenUnhfsExtractsIt_ThenEveryFileIsItsContent()
    => AssertDecodes(this.Unhfs(HfsPlusDecmpfsMethodsTests.CompressedVolume(HfsPlusCompression.Zlib)), "unhfs", "chunk-plus-one.bin");

  [TestCase(HfsPlusCompression.InlineUncompressed, TestName = "GivenOurMethodOneVolume_When7ZipExtractsIt_ThenItHasNoDecoderForIt")]
  [TestCase(HfsPlusCompression.Lzbitmap, TestName = "GivenOurLzbitmapVolume_When7ZipExtractsIt_ThenItHasNoDecoderForIt")]
  [Category("BoundaryCase")]
  public void SevenZipLacks(HfsPlusCompression compression)
    => AssertDoesNotDecode(this.SevenZip(HfsPlusDecmpfsMethodsTests.CompressedVolume(compression)), "7-Zip");

  [TestCase(HfsPlusCompression.InlineUncompressed, TestName = "GivenOurMethodOneVolume_WhenUnhfsExtractsIt_ThenItHasNoDecoderForIt")]
  [TestCase(HfsPlusCompression.Lzbitmap, TestName = "GivenOurLzbitmapVolume_WhenUnhfsExtractsIt_ThenItHasNoDecoderForIt")]
  [Category("BoundaryCase")]
  public void UnhfsLacks(HfsPlusCompression compression)
    => AssertDoesNotDecode(this.Unhfs(HfsPlusDecmpfsMethodsTests.CompressedVolume(compression)), "unhfs");
}
