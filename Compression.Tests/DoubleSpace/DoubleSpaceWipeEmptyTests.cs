using Compression.Registry;
using FileSystem.DoubleSpace;

namespace Compression.Tests.DoubleSpace;

/// <summary>
/// Unused-space wiping for DoubleSpace / DriveSpace compressed volume files. Both
/// descriptors implement <see cref="IWipeEmpty"/> so free physical sectors in the
/// DATA region can be zeroed without disturbing live file content.
/// <para>
/// Cluster-tip wiping is not applicable to a CVF: the DATA region holds
/// compressed/stored sector runs whose physical byte length is unrelated to the
/// logical (uncompressed) file size in the inner FAT directory. Zeroing a tail by
/// logical-size offset would corrupt the encoded run, so the wiper clears whole
/// free sectors only.
/// </para>
/// </summary>
[TestFixture]
public class DoubleSpaceWipeEmptyTests {

  private static byte[] BuildImage(CvfVariant variant, byte[] payload) {
    var w = new DoubleSpaceWriter { Variant = variant };
    // Force a stored run so the round-trip path is deterministic and so the
    // DATA region contains identifiable, non-trivial content.
    w.AddFile("PAYLOAD.BIN", payload, compress: false);
    return w.Build();
  }

  private static void AssertWipeRoundTripsAndZerosFree(IWipeEmpty descriptor, IFilesystemExtentMap extentMap, byte[] disk, byte[] payload) {
    using var ms = new MemoryStream();
    ms.Write(disk);

    // Dirty a known free DATA sector so the wipe has observable work to do.
    ms.Position = 0;
    var freeExtent = extentMap.EnumerateExtents(ms)
                              .First(e => e.Kind == DefragBlockKind.Free && e.Length >= 512);
    var dirtyOffset = freeExtent.Offset;
    ms.Position = dirtyOffset;
    var dirty = new byte[512];
    Array.Fill(dirty, (byte)0xFF);
    ms.Write(dirty);

    descriptor.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    var bytes = ms.ToArray();
    for (var i = dirtyOffset; i < dirtyOffset + 512; i++)
      Assert.That(bytes[i], Is.EqualTo(0), $"free sector byte at {i} must be zero after wipe");

    ms.Position = 0;
    var reader = new DoubleSpaceReader(ms);
    var entry = reader.Entries.Single(e => !e.IsDirectory);
    Assert.That(reader.Extract(entry), Is.EqualTo(payload), "file content intact after wipe");
  }

  [Test, Category("Wipe")]
  public void DoubleSpace_WipeUnusedSpace_RoundTripsFileAndZerosFreeSectors() {
    var payload = new byte[200];
    Array.Fill(payload, (byte)0xAA);
    var disk = BuildImage(CvfVariant.DoubleSpace60, payload);
    var descriptor = new DoubleSpaceFormatDescriptor();
    AssertWipeRoundTripsAndZerosFree(descriptor, descriptor, disk, payload);
  }

  [Test, Category("Wipe")]
  public void DriveSpace_WipeUnusedSpace_RoundTripsFileAndZerosFreeSectors() {
    var payload = new byte[200];
    Array.Fill(payload, (byte)0xAA);
    var disk = BuildImage(CvfVariant.DriveSpace62, payload);
    var descriptor = new DriveSpaceFormatDescriptor();
    AssertWipeRoundTripsAndZerosFree(descriptor, descriptor, disk, payload);
  }
}
