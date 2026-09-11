#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.TahoeLafs;

namespace Compression.Tests.TahoeLafs;

/// <summary>
/// Pins the intentionally read-only archive namespace while allowing physical
/// maintenance of the Tahoe storage-share container itself.
/// </summary>
[TestFixture]
public class TahoeLafsStubBehaviorTests {
  private static byte[] BuildImmutable(int payloadLen = 64) {
    const int leaseSize = 72;
    var image = new byte[12 + payloadLen + leaseSize];
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0, 4), 2u);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4, 4), (uint)payloadLen);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(8, 4), 1u);
    for (var i = 0; i < payloadLen; ++i)
      image[12 + i] = (byte)(i ^ 0x5A);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(12 + payloadLen, 4), 1u);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesPhysicalMaintenanceWithoutFakeArchiveWrites() {
    var descriptor = new TahoeLafsFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
      Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveLayoutMap>());
      Assert.That(descriptor, Is.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.Not.InstanceOf<ILayoutOptimizable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchivePurgeable>());
    });
  }

  [Test, Category("HappyPath")]
  public void OpaqueEntries_RemainExactRenderedViews() {
    var image = BuildImmutable(payloadLen: 64);
    var descriptor = new TahoeLafsFormatDescriptor();
    using var stream = new MemoryStream(image, writable: false);
    var entries = descriptor.List(stream, null);

    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "FULL.tahoe-share", "metadata.ini", "share.immutable.bin" }));

    var outDir = Path.Combine(Path.GetTempPath(), "TahoeLafs_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      using var source = new MemoryStream(image, writable: false);
      descriptor.Extract(source, outDir, password: null, files: null);

      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "FULL.tahoe-share")), Is.EqualTo(image));
      Assert.That(
        File.ReadAllBytes(Path.Combine(outDir, "share.immutable.bin")),
        Is.EqualTo(image.AsSpan(12, 64).ToArray()));
    } finally {
      Directory.Delete(outDir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Description_StatesOpaqueReadOnlyNamespace() {
    var description = new TahoeLafsFormatDescriptor().Description.ToLowerInvariant();
    Assert.That(description, Does.Contain("opaque"));
    Assert.That(description, Does.Contain("read-only"));
  }
}
