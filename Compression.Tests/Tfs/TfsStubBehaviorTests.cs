#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Tfs;

namespace Compression.Tests.Tfs;

/// <summary>
/// Pins the deliberately conservative surface for <see cref="TfsFormatDescriptor"/>.
/// No normative public Trans-FS on-disk specification is available, so tests
/// require byte-preserving opaque reads and reject speculative write/maintenance
/// capability claims.
/// </summary>
[TestFixture]
public class TfsStubBehaviorTests {
  private static byte[] BuildImage(int length = 4096) {
    var image = new byte[length];
    for (var i = 0; i < image.Length; ++i)
      image[i] = unchecked((byte)(i * 31 + 7));

    if (length >= 4) {
      image[0] = 0x54;
      image[1] = 0x46;
      image[2] = 0x53;
      image[3] = 0x01;
    }

    return image;
  }

  [Test, Category("Stub")]
  public void Stub_DescriptorHonestlyAdvertisesCapabilities_AndOpaqueEntries() {
    var descriptor = new TfsFormatDescriptor();

    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False,
      "TFS has no verified creation semantics and must not advertise CanCreate.");
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False,
      "TFS has no verified mutation semantics and must not advertise CanModify.");

    var image = BuildImage();
    using var stream = new MemoryStream(image, writable: false);
    var entries = descriptor.List(stream, null);

    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { "FULL.tfs", "metadata.ini" }));
    Assert.That(entries.Single(e => e.Name == "FULL.tfs").OriginalSize, Is.EqualTo(image.LongLength));
  }

  [Test, Category("HappyPath")]
  public void OpaqueReader_PreservesImageBeyondLegacy64KiBCap() {
    var descriptor = new TfsFormatDescriptor();
    var image = BuildImage(256 * 1024 + 123);

    using (var stream = new MemoryStream(image, writable: false)) {
      var full = descriptor.List(stream, null).Single(e => e.Name == "FULL.tfs");
      Assert.That(full.OriginalSize, Is.EqualTo(image.LongLength));
      Assert.That(full.CompressedSize, Is.EqualTo(image.LongLength));
    }

    var outputDir = Path.Combine(Path.GetTempPath(), "TfsOpaque_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outputDir);
    try {
      using var stream = new MemoryStream(image, writable: false);
      descriptor.Extract(stream, outputDir, password: null, files: ["FULL.tfs"]);
      Assert.That(File.ReadAllBytes(Path.Combine(outputDir, "FULL.tfs")), Is.EqualTo(image));
      Assert.That(File.Exists(Path.Combine(outputDir, "metadata.ini")), Is.False);
    } finally {
      Directory.Delete(outputDir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Metadata_DoesNotInventUnverifiedGeometry() {
    var descriptor = new TfsFormatDescriptor();
    var outputDir = Path.Combine(Path.GetTempPath(), "TfsMetadata_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outputDir);
    try {
      using var stream = new MemoryStream(BuildImage(), writable: false);
      descriptor.Extract(stream, outputDir, password: null, files: ["metadata.ini"]);
      var metadata = File.ReadAllText(Path.Combine(outputDir, "metadata.ini"));

      Assert.Multiple(() => {
        Assert.That(metadata, Does.Contain("parse_status=ok"));
        Assert.That(metadata, Does.Contain("magic_hex=0x54465301"),
          "Keep the existing metadata key for callers while qualifying its evidentiary status separately.");
        Assert.That(metadata, Does.Contain("signature_status=legacy_heuristic"));
        Assert.That(metadata, Does.Contain("layout_status=opaque"));
        Assert.That(metadata, Does.Not.Contain("block_size="),
          "The former 1024-byte block-size claim has no verified public source.");
        Assert.That(File.Exists(Path.Combine(outputDir, "FULL.tfs")), Is.False);
      });
    } finally {
      Directory.Delete(outputDir, recursive: true);
    }
  }

  [Test, Category("Stub")]
  public void Stub_DoesNotClaimSpeculativeMaintenance() {
    var descriptor = new TfsFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveCreatable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.Not.InstanceOf<ILayoutOptimizable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchivePurgeable>());
      Assert.That(descriptor.Description.ToLowerInvariant(), Does.Contain("opaque"));
    });
  }
}
