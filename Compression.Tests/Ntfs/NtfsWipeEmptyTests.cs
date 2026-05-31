#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Ntfs;

namespace Compression.Tests.Ntfs;

/// <summary>
/// Cluster-tip / unused-space wiping for the NTFS descriptor. A non-resident
/// file (> 700 bytes) occupies whole 4096-byte clusters; the slack between its
/// logical size and the end of its last cluster is the cluster tip and must be
/// zeroed. Resident files (≤ 700 bytes) live inside the MFT record and own no
/// data cluster, so they have no tip.
/// </summary>
[TestFixture]
public class NtfsWipeEmptyTests {

  private const int ClusterSize = 4096;

  [Test]
  public void NtfsDescriptorImplementsIWipeEmpty() {
    var desc = new NtfsFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("RoundTrip")]
  public void WipeEmpty_ClusterTip_ZeroedAndFileRoundTrips() {
    // 3596 bytes: above the 700-byte resident threshold (so non-resident, one
    // 4096-byte cluster) and below a full cluster (so it leaves a 500-byte tip).
    var fileSize = ClusterSize - 500;
    var content = new byte[fileSize];
    Array.Fill(content, (byte)0xAA);

    var w = new NtfsWriter();
    w.AddFile("SECRET.BIN", content);
    var image = w.Build();

    var desc = new NtfsFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    // Locate the file's single data extent and dirty its cluster tip.
    ms.Position = 0;
    var extent = desc.EnumerateExtents(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.FileName == "SECRET.BIN");
    var tipStart = extent.Offset + fileSize;
    var clusterEnd = extent.Offset + ((fileSize + ClusterSize - 1) / ClusterSize) * ClusterSize;
    var tipLen = (int)(clusterEnd - tipStart);
    Assert.That(tipLen, Is.GreaterThan(0), "the chosen file size must leave a non-empty cluster tip");

    ms.Position = tipStart;
    var junk = new byte[tipLen];
    Array.Fill(junk, (byte)0xFF);
    ms.Write(junk, 0, junk.Length);

    desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    ms.Position = tipStart;
    var tip = new byte[tipLen];
    ms.ReadExactly(tip);
    Assert.That(tip, Is.All.EqualTo((byte)0), "cluster tip slack must be zeroed");

    ms.Position = 0;
    using var reader = new NtfsReader(ms);
    var entry = reader.Entries.First(e => !e.IsDirectory && e.Name == "SECRET.BIN");
    Assert.That(reader.Extract(entry), Is.EqualTo(content), "file content survives the wipe intact");
  }
}
