using Compression.Analysis;

namespace Compression.Tests.Analysis;

[TestFixture]
public sealed class FilesystemCarverForensicTests {

  [Test, Category("Forensics")]
  public void DamagedFat_WithIndependentSignature_RemainsVisible() {
    var damaged = new byte[4096];
    "FAT32   "u8.CopyTo(damaged.AsSpan(82));
    // Deliberately leave the BPB/geometry invalid. The FAT driver must reject mounting this image,
    // while the independent BS_FilSysType evidence remains useful to a forensic analyst.

    using var stream = new MemoryStream(damaged, writable: false);
    var carver = new FilesystemCarver {
      Options = new FsCarveOptions {
        DescendIntoPartitionTables = false,
        FormatIds = ["Fat"],
        KeepDamagedCandidates = true,
      },
    };

    var hits = carver.CarveStream(stream);
    var fat = hits.SingleOrDefault(hit => hit.FormatId == "Fat" && hit.ByteOffset == 0);

    Assert.That(fat, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(fat!.CanMount, Is.False);
      Assert.That(fat.DriverValidated, Is.False);
      Assert.That(fat.Confidence, Is.GreaterThanOrEqualTo(0.5));
      Assert.That(fat.Limitations, Is.Not.Null.And.Not.Empty);
    });
  }

  [Test, Category("Forensics")]
  public void DamagedFat_CanBeConfiguredFailClosed() {
    var damaged = new byte[4096];
    "FAT32   "u8.CopyTo(damaged.AsSpan(82));

    using var stream = new MemoryStream(damaged, writable: false);
    var carver = new FilesystemCarver {
      Options = new FsCarveOptions {
        DescendIntoPartitionTables = false,
        FormatIds = ["Fat"],
        KeepDamagedCandidates = false,
      },
    };

    var hits = carver.CarveStream(stream);

    Assert.That(hits.Any(hit => hit.FormatId == "Fat" && hit.ByteOffset == 0), Is.False);
  }
}
