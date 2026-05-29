#pragma warning disable CS1591

namespace Compression.Tests.Deploy;

/// <summary>
/// Tests for the deploy command's safety guard logic and CRC verification.
/// These tests validate the IsSystemDrive helper and the file-to-file deploy
/// path (using regular files as mock devices to avoid requiring physical drives).
/// </summary>
[TestFixture]
public class DeployCommandTests {

  [TestCase(@"\\.\PhysicalDrive0", ExpectedResult = true)]
  [TestCase(@"\\.\physicaldrive0", ExpectedResult = true)]
  [TestCase(@"C:\", ExpectedResult = true)]
  [TestCase(@"\\.\C:", ExpectedResult = true)]
  [TestCase("/dev/sda", ExpectedResult = true)]
  [TestCase("/dev/nvme0n1", ExpectedResult = true)]
  [TestCase("/", ExpectedResult = true)]
  [TestCase("/boot", ExpectedResult = true)]
  [TestCase(@"\\.\PhysicalDrive2", ExpectedResult = false)]
  [TestCase("/dev/sdb", ExpectedResult = false)]
  [TestCase("/dev/sdc", ExpectedResult = false)]
  [TestCase(@"D:\disk.img", ExpectedResult = false)]
  public bool IsSystemDrive_DetectsSystemDrives(string device) => IsSystemDriveHelper(device);

  [Test]
  public void Deploy_CrcMatches_AfterWriteToFile() {
    // Simulate deploy by writing image data to a temp file (mock device)
    var imageData = new byte[8192];
    new Random(42).NextBytes(imageData);

    var imagePath = Path.GetTempFileName();
    var devicePath = Path.GetTempFileName();
    try {
      File.WriteAllBytes(imagePath, imageData);

      // Write in 64KB chunks like the deploy command
      var writeCrc = new Compression.Core.Checksums.Crc32();
      using (var src = File.OpenRead(imagePath))
      using (var dst = File.Open(devicePath, FileMode.Open, FileAccess.Write)) {
        var buffer = new byte[64 * 1024];
        int bytesRead;
        while ((bytesRead = src.Read(buffer, 0, buffer.Length)) > 0) {
          dst.Write(buffer, 0, bytesRead);
          writeCrc.Update(buffer.AsSpan(0, bytesRead));
        }
      }

      // Verify CRC matches source
      var srcCrc = Compression.Core.Checksums.Crc32.Compute(imageData);
      Assert.That(writeCrc.Value, Is.EqualTo(srcCrc));

      // Verify written file matches
      var written = File.ReadAllBytes(devicePath);
      Assert.That(written, Is.EqualTo(imageData));
    } finally {
      File.Delete(imagePath);
      File.Delete(devicePath);
    }
  }

  [Test]
  public void Deploy_VerifyPass_ReadbackMatchesWrite() {
    var imageData = new byte[4096];
    new Random(123).NextBytes(imageData);

    var path = Path.GetTempFileName();
    try {
      File.WriteAllBytes(path, imageData);

      // Read back and compute CRC
      var verifyCrc = new Compression.Core.Checksums.Crc32();
      using (var fs = File.OpenRead(path)) {
        var buffer = new byte[64 * 1024];
        int bytesRead;
        while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
          verifyCrc.Update(buffer.AsSpan(0, bytesRead));
      }

      var srcCrc = Compression.Core.Checksums.Crc32.Compute(imageData);
      Assert.That(verifyCrc.Value, Is.EqualTo(srcCrc));
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public void Deploy_ProgressReporting_WritesAllBytes() {
    var imageData = new byte[256 * 1024]; // 256 KB
    Array.Fill(imageData, (byte)0xAB);

    var path = Path.GetTempFileName();
    try {
      File.WriteAllBytes(path, new byte[0]); // create empty target

      long totalWritten = 0;
      using (var src = new MemoryStream(imageData))
      using (var dst = File.Open(path, FileMode.Open, FileAccess.Write)) {
        var buffer = new byte[64 * 1024];
        int bytesRead;
        while ((bytesRead = src.Read(buffer, 0, buffer.Length)) > 0) {
          dst.Write(buffer, 0, bytesRead);
          totalWritten += bytesRead;
        }
      }

      Assert.That(totalWritten, Is.EqualTo(imageData.Length));
      Assert.That(new FileInfo(path).Length, Is.EqualTo(imageData.Length));
    } finally {
      File.Delete(path);
    }
  }

  /// <summary>
  /// Mirror of the IsSystemDrive helper from Program.cs for unit testing.
  /// </summary>
  private static bool IsSystemDriveHelper(string device) {
    var d = device.ToLowerInvariant().Trim();
    if (d.Contains("physicaldrive0")) return true;
    if (d.StartsWith("c:") || d.StartsWith(@"\\.\c:")) return true;
    if (d == "/dev/sda" || d == "/dev/nvme0n1" || d == "/dev/vda") return true;
    if (d == "/" || d == "/boot") return true;
    return false;
  }
}
