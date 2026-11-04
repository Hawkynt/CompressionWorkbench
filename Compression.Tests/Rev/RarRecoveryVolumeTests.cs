using System.Reflection;
using System.Text;
using FileFormat.RarRecoveryVolume;

namespace Compression.Tests.Rev;

/// <summary>
/// RAR recovery volumes. The fixtures are the two recovery volumes RAR 7.12 (RAR 5.0 format) and RAR 6.24
/// (RAR 4 format, <c>-ma4</c>) wrote for a four-volume archive of the same 9000-byte file; the data volumes'
/// sizes and CRC32s were measured with zlib. See ReferenceVectors/README.md and docs/REV-ON-DISK.md.
/// </summary>
[TestFixture]
public class RarRecoveryVolumeTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"RevVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded rev vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  // zlib.crc32 of r5.part1..4.rar as RAR 7.12 wrote them.
  private static readonly (long, uint)[] Rar5Volumes = [(3072, 0xcb2d4f55), (3072, 0x23f14b00), (3072, 0x34187c30), (348, 0x8871cbc5)];

  [TestCase("r5-part1.rev", 4)]
  [TestCase("r5-part2.rev", 5)]
  [Category("ThirdPartyVector")]
  public void Given_Rar5RecoveryVolume_When_Read_Then_CountsNumberAndDataVolumeChecks(string vector, int number) {
    var v = RarRecoveryVolumeFormatDescriptor.Read(Vector(vector));
    Assert.That((v.Format, v.DataVolumes, v.RecoveryVolumes, v.VolumeNumber), Is.EqualTo((5, 4, 2, number)));
    Assert.That(v.DataVolumeChecks, Is.EqualTo(Rar5Volumes));
    Assert.That(v.RecoveryData, Has.Length.EqualTo(3147 - 16 - 59));
  }

  [TestCase("r4-part1.rev", 1)]
  [TestCase("r4-part2.rev", 2)]
  [Category("ThirdPartyVector")]
  public void Given_Rar3RecoveryVolume_When_Read_Then_TrailerCountsAndNumber(string vector, int number) {
    var v = RarRecoveryVolumeFormatDescriptor.Read(Vector(vector));
    Assert.That((v.Format, v.DataVolumes, v.RecoveryVolumes, v.VolumeNumber), Is.EqualTo((3, 4, 2, number)));
    Assert.That(v.RecoveryData, Has.Length.EqualTo(3072 - 7));
  }

  [Test, Category("HappyPath")]
  public void Given_RecoveryVolume_When_ListedAndExtracted_Then_MetadataAndRecoveryData() {
    var d = new RarRecoveryVolumeFormatDescriptor();
    Assert.That(d.List(new MemoryStream(Vector("r5-part1.rev")), null).Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini", "recovery.bin" }));
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_rev_{Guid.NewGuid():N}");
    try {
      d.Extract(new MemoryStream(Vector("r5-part1.rev")), dir, null, ["metadata.ini"]);
      Assert.That(File.ReadAllText(Path.Combine(dir, "metadata.ini"), Encoding.UTF8), Does.Contain("data_volume_1=size 3072 crc32 cb2d4f55"));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Given_RevPath_When_Detected_Then_RarRecoveryVolume() {
    var path = Path.Combine(Path.GetTempPath(), $"cwb_rev_{Guid.NewGuid():N}.rev");
    File.WriteAllBytes(path, Vector("r4-part1.rev"));
    try {
      Assert.That(Compression.Lib.FormatDetector.Detect(path).ToString(), Is.EqualTo("RarRecoveryVolume"));
    } finally { File.Delete(path); }
  }

  [Test, Category("EdgeCase")]
  public void Given_DamagedRar5RecoveryData_When_Read_Then_DataCrcRefuses() {
    var bytes = Vector("r5-part1.rev");
    bytes[^1] ^= 0x01;
    Assert.Throws<InvalidDataException>(() => RarRecoveryVolumeFormatDescriptor.Read(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_DamagedRar5Header_When_Read_Then_HeaderCrcRefuses() {
    var bytes = Vector("r5-part1.rev");
    bytes[17] ^= 0x01; // data volume count
    Assert.Throws<InvalidDataException>(() => RarRecoveryVolumeFormatDescriptor.Read(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_DamagedRar3Volume_When_Read_Then_TrailerCrcRefuses() {
    var bytes = Vector("r4-part1.rev");
    bytes[100] ^= 0x01;
    Assert.Throws<InvalidDataException>(() => RarRecoveryVolumeFormatDescriptor.Read(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_ArbitraryBytes_When_Read_Then_NotARecoveryVolume()
    => Assert.Throws<InvalidDataException>(() => RarRecoveryVolumeFormatDescriptor.Read(Encoding.ASCII.GetBytes("just some text, not a rev")));

  [Test, Category("Boundary")]
  public void Given_TooShortForEitherLayout_When_Read_Then_InvalidData() {
    Assert.Throws<InvalidDataException>(() => RarRecoveryVolumeFormatDescriptor.Read(Vector("r5-part1.rev")[..15]));
    Assert.Throws<InvalidDataException>(() => RarRecoveryVolumeFormatDescriptor.Read([1, 2, 3, 4, 5, 6, 7]));
  }
}
