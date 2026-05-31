using Compression.Registry;
using FileSystem.ExFat;

namespace Compression.Tests.ExFat;

/// <summary>
/// Unused-space wiping for exFAT images. The descriptor implements
/// <see cref="IWipeEmpty"/> so free clusters and cluster-tip slack (the bytes
/// between a file's real size and the end of its last allocated cluster) can be
/// zeroed without disturbing live file content.
/// </summary>
[TestFixture]
public class ExFatWipeEmptyTests {

  // A payload a bit shorter than one 4 KiB cluster guarantees a non-empty
  // cluster tip after the last valid byte.
  private static byte[] BuildImageWithOneShortFile(string name, out int payloadLength) {
    payloadLength = 4000; // < 4096 cluster
    var payload = new byte[payloadLength];
    Array.Fill(payload, (byte)0xAA);

    var w = new ExFatWriter();
    w.AddFile(name, payload);
    return w.BuildAutoSized();
  }

  [Test, Category("Wipe")]
  public void WipeUnusedSpace_LeavesFileContentIntact() {
    var disk = BuildImageWithOneShortFile("payload.bin", out var payloadLength);
    using var ms = new MemoryStream();
    ms.Write(disk);

    new ExFatFormatDescriptor().WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    ms.Position = 0;
    var reader = new ExFatReader(ms);
    var entry = reader.Entries.Single(e => !e.IsDirectory);
    var roundTripped = reader.Extract(entry);

    Assert.That(roundTripped.Length, Is.EqualTo(payloadLength), "file size preserved after wipe");
    Assert.That(roundTripped, Is.All.EqualTo((byte)0xAA), "file content intact after wipe");
  }

  [Test, Category("Wipe")]
  public void WipeUnusedSpace_ZerosClusterTipOfLastFile() {
    var disk = BuildImageWithOneShortFile("payload.bin", out var payloadLength);
    using var ms = new MemoryStream();
    ms.Write(disk);

    // The tip is the slack between the file's real size and the cluster end.
    // Resolve it from the extent map before wiping.
    ms.Position = 0;
    var descriptor = new ExFatFormatDescriptor();
    var fileExtent = descriptor.EnumerateExtents(ms)
                               .Where(e => e.Kind == DefragBlockKind.Used && e.FileName == "payload.bin")
                               .OrderByDescending(e => e.Offset)
                               .First();

    // The exFAT extent map truncates the last run to the valid byte count, so
    // the tip begins exactly at the run's end and spans the rest of the cluster.
    const int clusterBytes = 4096;
    var tipStart = fileExtent.Offset + fileExtent.Length;
    var clusterEnd = fileExtent.Offset - (fileExtent.Offset % clusterBytes) + clusterBytes;
    var tipLength = clusterEnd - tipStart;
    Assert.That(tipLength, Is.GreaterThan(0), "test image must have a non-empty cluster tip");

    descriptor.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    var bytes = ms.ToArray();
    for (var i = tipStart; i < clusterEnd; i++)
      Assert.That(bytes[i], Is.EqualTo(0), $"cluster-tip byte at {i} must be zero after wipe");
  }
}
