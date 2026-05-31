using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Ntfs;

/// <summary>
/// Small files are stored RESIDENT: their bytes live directly inside the MFT
/// FILE record's $DATA attribute, so no data clusters are allocated and no
/// cluster-tail slack is wasted. Larger files keep their data in non-resident
/// cluster runs. Both must round-trip through the reader byte-for-byte.
/// </summary>
[TestFixture]
public class NtfsResidentDataTests {

  // A 50-byte file fits comfortably inside the MFT record; a 200 KiB file does not.
  private static readonly byte[] TinyData = Enumerable.Range(0, 50).Select(i => (byte)(i * 7 + 1)).ToArray();
  private static readonly byte[] LargeData = Enumerable.Range(0, 200 * 1024).Select(i => (byte)(i * 31 + 5)).ToArray();

  [Test, Category("RoundTrip")]
  public void TinyFileIsResident_AllocatesNoDataClusters_AndRoundTrips() {
    var w = new FileSystem.Ntfs.NtfsWriter();
    w.AddFile("tiny.bin", TinyData);
    var withTiny = w.Build();

    using var ms = new MemoryStream(withTiny);
    var r = new FileSystem.Ntfs.NtfsReader(ms);
    var tiny = r.Entries.Single(e => e.Name == "tiny.bin");
    Assert.That(r.Extract(tiny), Is.EqualTo(TinyData), "tiny file content round-trips");

    // The tiny file's MFT record must carry a RESIDENT $DATA attribute (form
    // code 0, not the non-resident form), proving no clusters were allocated.
    var record = MftInspector.FindRecordByFileName(withTiny, "tiny.bin");
    Assert.That(MftInspector.DataAttributeIsResident(record), Is.True,
      "tiny file's $DATA is stored resident inside the MFT record");
  }

  [Test, Category("RoundTrip")]
  public void LargeFileIsNonResident_AndRoundTrips() {
    var w = new FileSystem.Ntfs.NtfsWriter();
    w.AddFile("large.bin", LargeData);
    var disk = w.Build(8 * 1024 * 1024);

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Ntfs.NtfsReader(ms);
    var large = r.Entries.Single(e => e.Name == "large.bin");

    Assert.That(r.Extract(large), Is.EqualTo(LargeData), "large file content round-trips");

    var record = MftInspector.FindRecordByFileName(disk, "large.bin");
    Assert.That(MftInspector.DataAttributeIsResident(record), Is.False,
      "large file's $DATA is stored non-resident (cluster runs)");
  }

  [Test, Category("RoundTrip")]
  public void ResidentStorageSavesSpace_ManyTinyFilesFitWithoutClusterAllocation() {
    // Pack enough tiny files that, if each allocated a full cluster, the data
    // region alone would dwarf the 4 MiB image floor. Resident storage keeps the
    // bytes inside the MFT records so no per-file data clusters are claimed,
    // leaving the auto-sized image far smaller. Both writers materialise the same
    // number of MFT records, so the size delta is purely the data clusters.
    const int count = 1500; // 1500 * 4 KiB clusters ≈ 6 MiB of data when non-resident
    var resident = new FileSystem.Ntfs.NtfsWriter();
    var clusterHogs = new FileSystem.Ntfs.NtfsWriter();
    var overThreshold = new byte[701]; // one byte past the resident threshold → one cluster each
    for (var i = 0; i < count; i++) {
      resident.AddFile($"r{i}.txt", TinyData);
      clusterHogs.AddFile($"r{i}.txt", overThreshold);
    }

    var residentImage = resident.BuildAutoSized();
    var clusterImage = clusterHogs.BuildAutoSized();

    Assert.That(residentImage.Length, Is.LessThan(clusterImage.Length),
      "resident tiny files produce a materially smaller image than the same count of cluster-backed files");

    using var ms = new MemoryStream(residentImage);
    var r = new FileSystem.Ntfs.NtfsReader(ms);
    for (var i = 0; i < count; i++) {
      var e = r.Entries.Single(x => x.Name == $"r{i}.txt");
      Assert.That(r.Extract(e), Is.EqualTo(TinyData), $"resident file r{i}.txt round-trips");
    }
  }
}
