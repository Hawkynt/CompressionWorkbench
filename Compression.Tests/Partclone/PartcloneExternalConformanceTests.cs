using FileFormat.Partclone;

namespace Compression.Tests.Partclone;

/// <summary>
/// Our partclone writer against partclone itself: <c>partclone.chkimg</c> must accept every image we
/// write, and <c>partclone.restore</c> must turn it back into the partition our own reader
/// reconstructs. The reverse direction — images partclone writes, read by us — is pinned without the
/// tool by the reference vectors in <see cref="PartcloneTests"/>. Skips when partclone is absent
/// (Debian/Ubuntu: <c>apt install partclone</c>; validated with 0.3.27).
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class PartcloneExternalConformanceTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb_pcext_" + Guid.NewGuid().ToString("N")[..10]);
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  private static void RequirePartclone() {
    if (!OperatingSystem.IsWindows() || FsInteropToolbox.WslAvailable) {
      if (FsInteropToolbox.WslHasTool("partclone.chkimg") && FsInteropToolbox.WslHasTool("partclone.restore")) return;
      Assert.Ignore("partclone is not installed. Run (inside WSL on Windows): `sudo apt install -y partclone`.");
    }
    Assert.Ignore("WSL not installed; partclone runs on Linux only.");
  }

  private static byte[] Partition(int blocks, int blockSize) {
    var raw = new byte[blocks * blockSize];
    var rng = new Random(12345);
    for (var b = 0; b < blocks; ++b) {
      if (b % 3 == 2) continue;                 // leave every third block empty
      rng.NextBytes(raw.AsSpan(b * blockSize, blockSize));
    }
    return raw;
  }

  [TestCase("crc32", "1", "256", "1", TestName = "Chkimg_Restore_Crc32_Bit_Strip256")]
  [TestCase("crc32", "1", "1", "1", TestName = "Chkimg_Restore_Crc32_Bit_Strip1")]
  [TestCase("crc32", "1", "7", "0", TestName = "Chkimg_Restore_Crc32_Bit_Strip7_NoReseed")]
  [TestCase("none", "1", "0", "1", TestName = "Chkimg_Restore_NoChecksum_Bit")]
  [TestCase("crc32", "none", "4", "1", TestName = "Chkimg_Restore_Crc32_NoMap")]
  public void OurImage_AcceptedByChkimg_AndRestoredIdentically(string checksum, string bitmap, string strip, string reseed) {
    RequirePartclone();
    const int blockSize = 512;
    var raw = Partition(301, blockSize);            // not a multiple of 8 blocks
    var image = Path.Combine(this._dir, "ours.pcl");
    using (var fs = File.Create(image))
      PartcloneWriter.Write(fs, new MemoryStream(raw), System.Text.Encoding.ASCII.GetBytes($"fs = raw\nblock_size = {blockSize}\n"), [],
        new Dictionary<string, string> {
          ["ChecksumMode"] = checksum, ["BitmapMode"] = bitmap, ["BlocksPerChecksum"] = strip, ["ReseedChecksum"] = reseed,
        });

    var img = FsInteropToolbox.WinToWsl(image);
    var check = FsInteropToolbox.RunWsl($"partclone.chkimg -s {img} -L /dev/null 2>&1");
    Assert.That(check.ExitCode, Is.Zero, $"partclone.chkimg rejected our image:\n{check.StdOut}\n{check.StdErr}");

    // restore writes used blocks only, so hand it a zeroed target of the partition's size.
    var restored = Path.Combine(this._dir, "restored.raw");
    File.WriteAllBytes(restored, new byte[raw.Length]);
    var restore = FsInteropToolbox.RunWsl(
      $"partclone.restore --overwrite {FsInteropToolbox.WinToWsl(restored)} -s {img} -L /dev/null 2>&1");
    Assert.That(restore.ExitCode, Is.Zero, $"partclone.restore failed:\n{restore.StdOut}\n{restore.StdErr}");

    using var ours = File.OpenRead(image);
    Assert.That(File.ReadAllBytes(restored), Is.EqualTo(new PartcloneReader(ours).ReconstructDisk()));
    // Every third block is empty and, with a map, not stored; the rest must survive unchanged.
    var expected = (byte[])raw.Clone();
    Assert.That(File.ReadAllBytes(restored), Is.EqualTo(expected));
  }

  [Test]
  public void OurImageWithDamagedDataChecksum_RejectedByChkimg() {
    RequirePartclone();
    var raw = Partition(40, 512);
    using var ms = new MemoryStream();
    PartcloneWriter.Write(ms, new MemoryStream(raw), "block_size = 512\n"u8.ToArray(), [],
      new Dictionary<string, string> { ["BlocksPerChecksum"] = "4" });
    var bytes = ms.ToArray();
    bytes[^1] ^= 0xFF;                                // last strip's checksum
    var image = Path.Combine(this._dir, "damaged.pcl");
    File.WriteAllBytes(image, bytes);
    var check = FsInteropToolbox.RunWsl($"partclone.chkimg -s {FsInteropToolbox.WinToWsl(image)} -L /dev/null 2>&1");
    Assert.That(check.ExitCode, Is.Not.Zero, "partclone.chkimg accepted an image with a broken checksum, so the positive cases prove nothing");
  }
}
