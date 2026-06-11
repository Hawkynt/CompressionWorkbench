using System.Buffers.Binary;
using FileFormat.Qed;

namespace Compression.Tests.Qed;

[TestFixture]
public class QedTests {

  private const uint ClusterSize = 4096;

  // Build a minimal QED: one allocated data cluster mapped via L1[0]->L2[0]->data.
  // image_size = 2 clusters; cluster 0 is allocated (filled with pattern), cluster 1
  // is unallocated (reads back zero).
  private static byte[] BuildSyntheticQed(byte fill) {
    const uint tableSizeClusters = 1; // 4096 bytes / 8 = 512 entries per table
    const long headerCluster = 0;
    const long l1Offset = ClusterSize;        // 4096
    const long l2Offset = ClusterSize * 2;    // 8192
    const long dataOffset = ClusterSize * 3;  // 12288
    const ulong imageSize = ClusterSize * 2;  // two clusters

    var total = (int)(ClusterSize * 4);
    var buf = new byte[total];

    // Header (little-endian).
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), 0x00444551u); // 'QED\0'
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), ClusterSize);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), tableSizeClusters);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), 1); // header size clusters
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(16, 8), 0); // features
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(24, 8), 0); // compat
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(32, 8), 0); // autoclear
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(40, 8), (ulong)l1Offset);
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(48, 8), imageSize);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(56, 4), 0); // backing offset
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(60, 4), 0); // backing size
    _ = headerCluster;

    // L1[0] -> L2 offset.
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan((int)l1Offset, 8), (ulong)l2Offset);
    // L2[0] -> data offset (cluster 0). L2[1] left zero => cluster 1 unallocated.
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan((int)l2Offset, 8), (ulong)dataOffset);

    // Data cluster contents.
    for (var i = 0; i < ClusterSize; ++i)
      buf[(int)dataOffset + i] = fill;

    return buf;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new QedFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Qed"));
    Assert.That(d.Extensions, Contains.Item(".qed"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndDisk() {
    var img = BuildSyntheticQed(0xAB);
    var d = new QedFormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.qed"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "disk.raw"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_ReconstructsDiskAndFullByteIdentical() {
    var img = BuildSyntheticQed(0xAB);
    var d = new QedFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "qed_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, dir, null, null);

      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.qed"));
      Assert.That(full, Is.EqualTo(img));

      var disk = File.ReadAllBytes(Path.Combine(dir, "disk.raw"));
      Assert.That(disk.Length, Is.EqualTo((int)(ClusterSize * 2)));
      // Cluster 0 == 0xAB, cluster 1 == zero (unallocated).
      Assert.That(disk[0], Is.EqualTo(0xAB));
      Assert.That(disk[(int)ClusterSize - 1], Is.EqualTo(0xAB));
      Assert.That(disk[(int)ClusterSize], Is.EqualTo(0));
      Assert.That(disk[^1], Is.EqualTo(0));

      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("cluster_size=4096"));
      Assert.That(meta, Does.Contain($"image_size={ClusterSize * 2}"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow() {
    var garbage = new byte[256];
    Array.Fill(garbage, (byte)0x33);
    var d = new QedFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "qed_bad_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(garbage);
      Assert.DoesNotThrow(() => d.List(ms, null));
      ms.Position = 0;
      Assert.DoesNotThrow(() => d.Extract(ms, dir, null, null));
      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.qed"));
      Assert.That(full, Is.EqualTo(garbage));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
